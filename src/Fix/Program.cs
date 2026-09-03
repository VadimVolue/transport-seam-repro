// The fix.
//
// The same broker, behind the same proxy, with the same strict validation. The difference is
// ConnectionSettings.TransportFactory (pull request #180): the application establishes the
// CONNECT tunnel and hands the library the connected stream, and the library still owns TLS,
// SASL and the AMQP open.
//
// So the connection is configured for broker-a and the certificate is checked against
// broker-a, whatever address the tunnel had to be dialled on. There is no forwarder, no
// loopback address in the picture, and nothing for a certificate's loopback SAN to satisfy.

using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.AMQP.Client;
using RabbitMQ.AMQP.Client.Impl;

namespace TransportSeamRepro.Fix
{
    internal static class Program
    {
        private static readonly TimeSpan s_consumeTimeout = TimeSpan.FromSeconds(15);

        internal static async Task<int> Main()
        {
            LabSettings lab = LabSettings.FromEnvironment();

            using (X509Certificate2 labCertificateAuthority = lab.LoadCertificateAuthority())
            {
                PrintBanner(lab, labCertificateAuthority);

                var observer = new TlsObserver(labCertificateAuthority);
                int transportFactoryInvocations = 0;

                ConnectionSettings connectionSettings = ConnectionSettingsBuilder.Create()
                    .Scheme("amqps")
                    // The broker's own name and TLS port. Neither has to be reachable from this
                    // machine: the factory below is what turns them into a byte stream.
                    .Host(lab.BrokerHost)
                    .Port(lab.BrokerPort)
                    .ContainerId("transport-seam-repro-fix")
                    .TransportFactory((host, port, cancellationToken) =>
                    {
                        Interlocked.Increment(ref transportFactoryInvocations);
                        Output.Log("factory", $"asked for a transport to {host}:{port}");
                        return ProxyTunnel.OpenAsync(lab.ProxyHost, lab.ProxyPort, host, port,
                            cancellationToken);
                    })
                    .Build();

                if (connectionSettings.TlsSettings is null)
                {
                    Output.Log("error", "the amqps scheme produced no TLS settings");
                    return 1;
                }

                connectionSettings.TlsSettings.AcceptablePolicyErrors = SslPolicyErrors.None;
                connectionSettings.TlsSettings.RemoteCertificateValidationCallback =
                    observer.Validate;

                try
                {
                    return await RoundTripAsync(lab, connectionSettings, observer,
                        () => transportFactoryInvocations);
                }
                catch (Exception exception)
                {
                    Output.Blank();
                    Output.Line("RESULT: failed.");
                    Output.Blank();
                    Output.Line($"  policy errors : {observer.ObservedPolicyErrors}");
                    Output.Line($"  exception     : {exception}");
                    return 1;
                }
            }
        }

        private static async Task<int> RoundTripAsync(LabSettings lab,
            ConnectionSettings connectionSettings, TlsObserver observer,
            Func<int> transportFactoryInvocations)
        {
            IConnection connection = await AmqpConnection.CreateAsync(connectionSettings);
            try
            {
                observer.Report(nameChecked: lab.BrokerHost);
                Output.Log("amqp", $"connection state: {connection.State}");

                IManagement management = connection.Management();
                string queueName = $"transport-seam-repro-{Guid.NewGuid():N}";
                IQueueSpecification queue = management.Queue(queueName)
                    .Exclusive(true)
                    .AutoDelete(true);
                await queue.DeclareAsync();
                Output.Log("amqp", $"queue declared: {queueName}");

                string tag = $"round-trip-{Guid.NewGuid():N}";

                IPublisher publisher = await connection.PublisherBuilder().Queue(queue)
                    .BuildAsync();
                try
                {
                    PublishResult publishResult = await publisher.PublishAsync(new AmqpMessage(tag));
                    Output.Log("amqp", $"published: {tag} ({publishResult.Outcome.State})");
                    if (publishResult.Outcome.State != OutcomeState.Accepted)
                    {
                        Output.Blank();
                        Output.Line($"RESULT: the broker did not accept the message " +
                            $"({publishResult.Outcome.State}).");
                        return 1;
                    }
                }
                finally
                {
                    await publisher.CloseAsync();
                    publisher.Dispose();
                }

                var receivedTcs = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                IConsumer consumer = await connection.ConsumerBuilder().Queue(queue)
                    .MessageHandler((context, message) =>
                    {
                        context.Accept();
                        receivedTcs.TrySetResult(message.BodyAsString());
                        return Task.CompletedTask;
                    })
                    .BuildAndStartAsync();
                string received;
                try
                {
                    received = await receivedTcs.Task.WaitAsync(s_consumeTimeout);
                    Output.Log("amqp", $"consumed : {received}");
                }
                finally
                {
                    await consumer.CloseAsync();
                    consumer.Dispose();
                }

                return Report(lab, observer, tag, received, transportFactoryInvocations());
            }
            finally
            {
                await connection.CloseAsync();
                connection.Dispose();
            }
        }

        private static int Report(LabSettings lab, TlsObserver observer, string sent,
            string received, int transportFactoryInvocations)
        {
            Output.Blank();

            if (!string.Equals(sent, received, StringComparison.Ordinal))
            {
                Output.Line("RESULT: the message did not survive the round trip.");
                Output.Line($"  sent     : {sent}");
                Output.Line($"  received : {received}");
                return 1;
            }

            if (!observer.NameCheckPassed)
            {
                Output.Line("RESULT: the round trip worked, but the name check did not.");
                return 1;
            }

            Output.Line("RESULT: round trip OK, through the proxy, against the broker's own name.");
            Output.Blank();
            Output.Line($"  The certificate was checked against {lab.BrokerHost}, the name the");
            Output.Line("  connection was configured for, and no loopback address took part at any");
            Output.Line("  point. A certificate listing 127.0.0.1 would have bought nothing here,");
            Output.Line("  because nothing asked about 127.0.0.1 - and the certificate in use for");
            Output.Line("  this run does not list it.");
            Output.Blank();
            Output.Line($"  The transport factory was invoked {transportFactoryInvocations} time(s):");
            Output.Line("  one transport for one connection attempt. TLS, SASL and the AMQP open all");
            Output.Line("  stayed inside the library; the application only produced the byte stream.");
            return 0;
        }

        private static void PrintBanner(LabSettings lab, X509Certificate2 labCertificateAuthority)
        {
            Output.Line("transport-seam-repro / Fix");
            Output.Line("  client          : RabbitMQ.AMQP.Client with ConnectionSettings.TransportFactory");
            Output.Line($"  proxy           : {lab.ProxyHost}:{lab.ProxyPort} (CONNECT)");
            Output.Line($"  broker          : {lab.BrokerHost}:{lab.BrokerPort}");
            Output.Line($"  client dials    : amqps://{lab.BrokerHost}:{lab.BrokerPort}" +
                " (through the transport factory)");
            Output.Line($"  TLS target host : {lab.BrokerHost}   <- the broker's own name");
            Output.Line($"  trust anchor    : {labCertificateAuthority.Subject}");
            Output.Line("  validation      : strict, no policy error accepted");
            Output.Blank();
        }
    }
}
