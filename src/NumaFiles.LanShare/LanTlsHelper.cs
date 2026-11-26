using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NumaFiles.LanShare;

internal static class LanTlsHelper
{
    private static readonly object CertGate = new();
    private static X509Certificate2? _cachedServerCert;

    public static X509Certificate2 GetOrCreateServerCertificate(string deviceId)
    {
        lock (CertGate)
        {
            if (_cachedServerCert is not null)
                return _cachedServerCert;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NumaFiles");
            Directory.CreateDirectory(dir);
            var pfxPath = Path.Combine(dir, "device_cert.pfx");

            try
            {
                if (File.Exists(pfxPath))
                {
                    var loaded = new X509Certificate2(
                        pfxPath,
                        "numa-lan-local",
                        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
                    if (loaded.NotAfter > DateTime.UtcNow.AddDays(30))
                    {
                        _cachedServerCert = loaded;
                        return loaded;
                    }
                    loaded.Dispose();
                }
            }
            catch
            {
                // Regenerate below if corrupted
            }

            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                $"CN=NumaFiles-{deviceId}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(5));

            var pfxBytes = cert.Export(X509ContentType.Pfx, "numa-lan-local");
            File.WriteAllBytes(pfxPath, pfxBytes);

            _cachedServerCert = new X509Certificate2(
                pfxBytes,
                "numa-lan-local",
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

            return _cachedServerCert;
        }
    }

    public static async Task<SslStream> WrapServerAsync(NetworkStream rawStream, string deviceId, CancellationToken ct)
    {
        var cert = GetOrCreateServerCertificate(deviceId);
        var ssl = new SslStream(rawStream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, ct).ConfigureAwait(false);
        return ssl;
    }

    public static async Task<SslStream> WrapClientAsync(NetworkStream rawStream, string targetHost, CancellationToken ct)
    {
        var ssl = new SslStream(
            rawStream,
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, cert, _, _) => cert is not null);

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, ct).ConfigureAwait(false);
        return ssl;
    }
}
