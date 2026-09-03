// The problem.
//
// The broker can only be reached through an HTTP CONNECT proxy, and the released client has no
// way to be handed a socket, a stream or a transport. So the tunnel has to be established
// outside the client, by a forwarder on the loopback interface, and the client is pointed at
// that. The TLS handshake then validates the broker certificate against 127.0.0.1.
//
// Which of two things happens next depends only on the broker's certificate:
//
//   BROKER_CERT_VARIANT=normal        the name check fails: RemoteCertificateNameMismatch
//   BROKER_CERT_VARIANT=san-loopback  the name check passes, and establishes nothing
//
// See ../Shared/TlsObserver.cs for why the second outcome is not the result of a relaxed
// validation callback.

using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.AMQP.Client;
using RabbitMQ.AMQP.Client.Impl;

namespace TransportSeamRepro.Problem
{
    internal static class Program
    {
        internal static async Task<int> Main()
        {
            LabSettings lab = LabSettings.FromEnvironment();

            using (X509Certificate2 labCertificateAuthority = lab.LoadCertificateAuthority())
            using (var cancellation = new CancellationTokenSource())
            {
                PrintBanner(lab, labCertificateAuthority);

                var forwarder = new LoopbackForwarder(lab);
                forwarder.Start(cancellation.Token);

                var observer = new TlsObserver(labCertificateAuthority);
                try
                {
                    return await ConnectAndReportAsync(lab, forwarder, observer);
                }
                finally
                {
                    cancellation.Cancel();
                }
            }
        }

        private static async Task<int> ConnectAndReportAsync(LabSettings lab,
            LoopbackForwarder forwarder, TlsObserver observer)
        {
            ConnectionSettings connectionSettings = ConnectionSettingsBuilder.Create()
                .Scheme("amqps")
                // The client is pointed at the forwarder because it cannot be pointed at the broker:
                // there is no route to the broker except through the proxy, and no way to tell the
                // client about the proxy.
                .Host(LabSettings.LoopbackAddress)
                .Port(forwarder.Port)
                .ContainerId("transport-seam-repro-problem")
                .Build();

            if (connectionSettings.TlsSettings is null)
            {
                Output.Log("error", "the amqps scheme produced no TLS settings");
                return 1;
            }

            // Strict: no policy error is acceptable, and the callback refuses everything except a
            // trust-anchor problem, which it resolves against the lab certificate authority.
            connectionSettings.TlsSettings.AcceptablePolicyErrors = SslPolicyErrors.None;
            connectionSettings.TlsSettings.RemoteCertificateValidationCallback = observer.Validate;

            IConnection? connection = null;
            Exception? failure = null;
            try
            {
                connection = await AmqpConnection.CreateAsync(connectionSettings);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            observer.Report(nameChecked: LabSettings.LoopbackAddress);

            if (connection is null)
            {
                return ReportRefused(lab, observer, failure);
            }

            try
            {
                return ReportConnected(lab, forwarder, observer, connection);
            }
            finally
            {
                await connection.CloseAsync();
                connection.Dispose();
            }
        }

        private static int ReportConnected(LabSettings lab, LoopbackForwarder forwarder,
            TlsObserver observer, IConnection connection)
        {
            Output.Log("amqp", $"connection state: {connection.State}");
            Output.Blank();

            if (!observer.NameCheckPassed)
            {
                Output.Line("RESULT: connected unexpectedly, with policy errors " +
                    $"'{observer.ObservedPolicyErrors}'.");
                return 1;
            }

            Output.Line("RESULT: connected, and the identity check established nothing.");
            Output.Blank();
            Output.Line("  The hostname check passed, with strict validation configured. No callback");
            Output.Line("  was relaxed: a name mismatch would have been refused outright, and the only");
            Output.Line("  error the platform reported is that a throw-away lab authority is not in");
            Output.Line("  this machine's trust store. Install that authority, as a real deployment");
            Output.Line("  would have done, and the reported value is SslPolicyErrors.None.");
            Output.Blank();
            Output.Line($"  And yet: the name checked was {LabSettings.LoopbackAddress}, the address of a");
            Output.Line("  forwarder running inside this very process. The broker's own name,");
            Output.Line($"  {lab.BrokerHost}, took no part in the check. The certificate happens to list");
            Output.Line("  that loopback address, so the check that exists to detect an impostor could");
            Output.Line("  not have detected one: any process able to hold");
            Output.Line($"  {LabSettings.LoopbackAddress}:{forwarder.Port} would have satisfied exactly the same check.");
            Output.Blank();
            Output.Line("  This is the dangerous outcome. Every indicator agrees that TLS is working,");
            Output.Line("  and the weakest link appears in no log and on no dashboard.");
            return 0;
        }

        private static int ReportRefused(LabSettings lab, TlsObserver observer, Exception? failure)
        {
            Output.Blank();

            if (!observer.SawNameMismatch)
            {
                Output.Line("RESULT: failed for an unexpected reason.");
                Output.Blank();
                Output.Line($"  policy errors : {observer.ObservedPolicyErrors}");
                Output.Line($"  exception     : {failure}");
                return 1;
            }

            Output.Line("RESULT: refused - RemoteCertificateNameMismatch.");
            Output.Blank();
            Output.Line("  The certificate is well formed and was issued by the lab authority, but it");
            Output.Line($"  does not name {LabSettings.LoopbackAddress}, and that address is what the");
            Output.Line("  handshake checked against, because it is the only address the client could");
            Output.Line($"  be given. The broker's real name, {lab.BrokerHost}, never took part in the");
            Output.Line("  check at all.");
            Output.Blank();
            Output.Line("  This is the honest outcome: inconvenient, and at least not misleading. Run");
            Output.Line("  the lab again with BROKER_CERT_VARIANT=san-loopback for the outcome that is.");
            return 0;
        }

        private static void PrintBanner(LabSettings lab, X509Certificate2 labCertificateAuthority)
        {
            Output.Line("transport-seam-repro / Problem");
            Output.Line("  client          : released RabbitMQ.AMQP.Client, no transport seam");
            Output.Line($"  proxy           : {lab.ProxyHost}:{lab.ProxyPort} (CONNECT)");
            Output.Line($"  broker          : {lab.BrokerHost}:{lab.BrokerPort}");
            Output.Line($"  forwarder       : {LabSettings.LoopbackAddress}:{lab.ForwarderPort}" +
                $" -> CONNECT {lab.BrokerHost}:{lab.BrokerPort}");
            Output.Line($"  client dials    : amqps://{LabSettings.LoopbackAddress}:{lab.ForwarderPort}");
            Output.Line($"  TLS target host : {LabSettings.LoopbackAddress}" +
                "   <- the address, not the broker's name");
            Output.Line($"  trust anchor    : {labCertificateAuthority.Subject}");
            Output.Line("  validation      : strict, no policy error accepted");
            Output.Blank();
        }
    }

    /// <summary>
    /// The workaround itself. A listener on the loopback interface that opens a CONNECT tunnel for
    /// every accepted connection and copies bytes both ways. It is the only reason the client has an
    /// address it can be given, and the reason that address is not the broker's.
    /// </summary>
    internal sealed class LoopbackForwarder
    {
        private readonly LabSettings _lab;
        private readonly TcpListener _listener;

        internal LoopbackForwarder(LabSettings lab)
        {
            _lab = lab;
            _listener = new TcpListener(IPAddress.Loopback, lab.ForwarderPort);
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal void Start(CancellationToken cancellationToken)
        {
            _listener.Start();
            Output.Log("forwarder", $"listening on {IPAddress.Loopback}:{Port}");
            _ = AcceptLoopAsync(cancellationToken);
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient inbound = await _listener.AcceptTcpClientAsync();
                    _ = TunnelAsync(inbound, cancellationToken);
                }
            }
            catch (Exception exception)
                when (exception is ObjectDisposedException || exception is SocketException)
            {
                // The listener was stopped once the demonstration finished.
            }
            finally
            {
                _listener.Stop();
            }
        }

        private async Task TunnelAsync(TcpClient inbound, CancellationToken cancellationToken)
        {
            try
            {
                using (inbound)
                using (Stream tunnel = await ProxyTunnel.OpenAsync(_lab.ProxyHost, _lab.ProxyPort,
                    _lab.BrokerHost, _lab.BrokerPort, cancellationToken))
                {
                    NetworkStream local = inbound.GetStream();
                    Task toBroker = local.CopyToAsync(tunnel, cancellationToken);
                    Task toClient = tunnel.CopyToAsync(local, cancellationToken);
                    await Task.WhenAny(toBroker, toClient);
                }
            }
            catch (Exception exception)
            {
                Output.Log("forwarder",
                    $"tunnel ended: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}
