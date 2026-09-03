using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace TransportSeamRepro
{
    /// <summary>
    /// Where the lab is and how to reach it. Every default matches the docker-compose.yml in this
    /// repository, so nothing has to be configured to run the demonstrations as documented.
    /// </summary>
    internal sealed class LabSettings
    {
        internal const string LoopbackAddress = "127.0.0.1";

        private LabSettings(string proxyHost, int proxyPort, string brokerHost, int brokerPort,
            int forwarderPort, string certificateAuthorityFile)
        {
            ProxyHost = proxyHost;
            ProxyPort = proxyPort;
            BrokerHost = brokerHost;
            BrokerPort = brokerPort;
            ForwarderPort = forwarderPort;
            CertificateAuthorityFile = certificateAuthorityFile;
        }

        /// <summary>Host the proxy is published on, as seen from this machine.</summary>
        internal string ProxyHost { get; }

        /// <summary>Port the proxy is published on, as seen from this machine.</summary>
        internal int ProxyPort { get; }

        /// <summary>The broker's own name, resolvable only on the lab network behind the proxy.</summary>
        internal string BrokerHost { get; }

        /// <summary>The broker's TLS port on the lab network.</summary>
        internal int BrokerPort { get; }

        /// <summary>Loopback port the Problem app's forwarder listens on.</summary>
        internal int ForwarderPort { get; }

        internal string CertificateAuthorityFile { get; }

        internal static LabSettings FromEnvironment()
        {
            return new LabSettings(
                proxyHost: Read("PROXY_HOST", LoopbackAddress),
                proxyPort: ReadPort("PROXY_PORT", 3128),
                brokerHost: Read("BROKER_HOST", "broker-a"),
                brokerPort: ReadPort("BROKER_PORT", 5671),
                forwarderPort: ReadPort("FORWARDER_PORT", 15671),
                certificateAuthorityFile: Read("CA_CERT_FILE",
                    FindRepositoryFile(Path.Combine("certs", "ca", "ca-cert.pem"))));
        }

        /// <summary>
        /// Loads the lab certificate authority. It is the trust anchor both apps use in place of the
        /// machine trust store, because a throw-away lab CA has no business being installed there.
        /// </summary>
        internal X509Certificate2 LoadCertificateAuthority()
        {
            if (!File.Exists(CertificateAuthorityFile))
            {
                throw new FileNotFoundException(
                    $"the lab certificate authority was not found at '{CertificateAuthorityFile}'. " +
                    "Run ./generate-certs.sh first.", CertificateAuthorityFile);
            }

            return X509Certificate2.CreateFromPem(File.ReadAllText(CertificateAuthorityFile));
        }

        private static string Read(string name, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static int ReadPort(string name, int fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return int.Parse(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Walks up from the built output looking for a file in the repository, so that both apps run
        /// the same from any working directory.
        /// </summary>
        private static string FindRepositoryFile(string relativePath)
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return relativePath;
        }
    }
}
