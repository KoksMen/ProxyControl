using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ProxyControl.Services
{
    internal static class ProxyTlsValidator
    {
        public static bool Validate(
            object sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors errors)
        {
            // Some authenticated proxy providers expose a trusted shared TLS
            // endpoint whose certificate name differs from the connection alias.
            // Keep chain validation mandatory; permit only that name mismatch.
            return errors == SslPolicyErrors.None ||
                   errors == SslPolicyErrors.RemoteCertificateNameMismatch;
        }
    }
}
