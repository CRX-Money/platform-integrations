using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;

/// <summary>
/// Throwaway self-signed client certificates generated per test run. Nothing certificate-like is committed
/// (pull request template: "no certificates or private keys, not even expired or sandbox ones").
/// </summary>
internal static class TestCertificates
{
    public static (string CertificatePem, string PrivateKeyPem) PemPair(DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        using var certificate = SelfSigned(rsa, notAfter);
        return (certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    public static byte[] Pfx(string password, DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        using var certificate = SelfSigned(rsa, notAfter);
        return certificate.Export(X509ContentType.Pkcs12, password);
    }

    public static string CertificateOnlyPem()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = SelfSigned(rsa, null);
        return certificate.ExportCertificatePem();
    }

    /// <summary>A bundle for the sandbox with a fresh certificate, ready for <see cref="InterIntegrationPlugin"/>.</summary>
    public static string Bundle(string accountNumber = "1234567-8", DateTimeOffset? notAfter = null)
    {
        var (certificatePem, keyPem) = PemPair(notAfter);
        return InterCredentialBundle.CreateFromPem(certificatePem, keyPem, keyPassword: null, InterEnvironment.Sandbox, accountNumber);
    }

    private static X509Certificate2 SelfSigned(RSA rsa, DateTimeOffset? notAfter)
    {
        var request = new CertificateRequest("CN=synthetic-inter-client.example", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var end = notAfter ?? DateTimeOffset.UtcNow.AddYears(1);
        return request.CreateSelfSigned(end.AddYears(-2), end);
    }
}
