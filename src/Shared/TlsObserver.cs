using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace TransportSeamRepro
{
    /// <summary>
    /// Records what the platform reported about the broker certificate, and decides whether to
    /// accept it.
    /// <para>
    ///   This is not a relaxed callback, and the distinction matters for the whole lab. It replaces
    ///   one thing: the trust anchor, which becomes the lab certificate authority instead of the
    ///   machine trust store. Every other check is left exactly as strict as the platform made it,
    ///   and <see cref="SslPolicyErrors.RemoteCertificateNameMismatch"/> in particular is always
    ///   refused. If the identity check can be satisfied here, it is because the certificate really
    ///   did name the host the connection was configured for.
    /// </para>
    /// </summary>
    internal sealed class TlsObserver
    {
        private const string SubjectAlternativeNameOid = "2.5.29.17";

        /// <summary>
        /// Borrowed, not owned: the caller that loaded it disposes it.
        /// </summary>
        private readonly X509Certificate2 _certificateAuthority;

        private SslPolicyErrors _observedPolicyErrors;
        private string _certificateSubject = "(no certificate)";
        private string _certificateIssuer = "(no certificate)";
        private string _subjectAlternativeNames = "(none)";

        internal TlsObserver(X509Certificate2 certificateAuthority)
        {
            _certificateAuthority = certificateAuthority;
        }

        /// <summary>What the platform reported for the handshake, verbatim.</summary>
        internal SslPolicyErrors ObservedPolicyErrors => _observedPolicyErrors;

        internal bool SawNameMismatch =>
            _observedPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);

        /// <summary>
        /// Whether the hostname check succeeded. This, and not the whole of
        /// <see cref="ObservedPolicyErrors"/>, is the verdict the lab is about: the trust-anchor
        /// error below is an artefact of a throw-away lab authority that is deliberately not
        /// installed in this machine's trust store.
        /// </summary>
        internal bool NameCheckPassed => WasCalled && !SawNameMismatch;

        internal bool WasCalled { get; private set; }

        internal bool Validate(object sender, X509Certificate? certificate, X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            WasCalled = true;
            _observedPolicyErrors = sslPolicyErrors;

            if (certificate is not null)
            {
                using (var presented = new X509Certificate2(certificate))
                {
                    _certificateSubject = presented.Subject;
                    _certificateIssuer = presented.Issuer;
                    _subjectAlternativeNames = DescribeSubjectAlternativeNames(presented);
                }
            }

            // Anything that is not a trust-anchor problem is refused outright. That includes the
            // name mismatch, which is the only error this lab is really about.
            if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors)
                != SslPolicyErrors.None)
            {
                return false;
            }

            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            return ChainsToLabCertificateAuthority(certificate);
        }

        internal void Report(string nameChecked)
        {
            if (!WasCalled)
            {
                Output.Log("tls", "the handshake never got as far as validating a certificate");
                return;
            }

            Output.Log("tls", $"certificate subject       : {_certificateSubject}");
            Output.Log("tls", $"certificate issuer        : {_certificateIssuer}");
            Output.Log("tls", $"subject alternative names : {_subjectAlternativeNames}");
            Output.Log("tls", $"SslPolicyErrors           : {_observedPolicyErrors}");
            Output.Log("tls", $"name checked against      : {nameChecked}" +
                $"  ->  {(SawNameMismatch ? "FAILED" : "passed")}");

            if (_observedPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            {
                Output.Log("tls", "trust anchor              : the lab authority is not installed in " +
                    "this machine's trust store,");
                Output.Log("tls", "                            so the chain is resolved against it " +
                    "explicitly instead");
            }
        }

        private static string DescribeSubjectAlternativeNames(X509Certificate2 certificate)
        {
            foreach (X509Extension extension in certificate.Extensions)
            {
                if (string.Equals(extension.Oid?.Value, SubjectAlternativeNameOid,
                    StringComparison.Ordinal))
                {
                    return extension.Format(false);
                }
            }

            return "(none)";
        }

        private bool ChainsToLabCertificateAuthority(X509Certificate? certificate)
        {
            if (certificate is null)
            {
                return false;
            }

            using (var presented = new X509Certificate2(certificate))
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(_certificateAuthority);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(presented);
            }
        }
    }
}
