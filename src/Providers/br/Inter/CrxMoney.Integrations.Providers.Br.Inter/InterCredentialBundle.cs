using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CrxMoney.Integrations.Providers.Br.Inter;

/// <summary>
/// The connection settings an Inter integration needs besides its client id and secret: the mTLS
/// certificate and key, the account number and the environment, serialised as one JSON document.
/// </summary>
/// <remarks>
/// <para>
/// The plugin contract carries three resolved credential strings per sync. Inter uses them as:
/// <c>ApiKey</c> = client id, <c>ApiSecret</c> = client secret, <c>Passphrase</c> = this bundle.
/// The host stores the bundle as one encrypted secret, like the other two.
/// </para>
/// <para>
/// Build the bundle with <see cref="CreateFromPfx"/> or <see cref="CreateFromPem"/> (Inter's Internet
/// Banking hands out a <c>.crt</c> and a <c>.key</c> file), and use <see cref="Inspect"/> to validate it
/// and read the certificate's expiry. Error messages never contain secret material.
/// </para>
/// </remarks>
public static partial class InterCredentialBundle
{
    /// <summary>The <c>format</c> marker written into every bundle.</summary>
    public const string Format = "crx.inter.credentials";

    /// <summary>The bundle layout version this package writes and reads.</summary>
    public const int Version = 1;

    /// <summary>Builds a bundle from a PKCS#12 (<c>.pfx</c>/<c>.p12</c>) file and its password.</summary>
    /// <exception cref="InterCredentialException">The certificate cannot be loaded or has no private key, or the account number is invalid.</exception>
    public static string CreateFromPfx(ReadOnlySpan<byte> pfx, string? password, InterEnvironment environment, string accountNumber)
    {
        var document = new BundleDocument
        {
            Format = Format,
            Version = Version,
            Environment = environment,
            AccountNumber = NormalizeAccountNumber(accountNumber),
            Certificate = new CertificateDocument
            {
                PfxBase64 = Convert.ToBase64String(pfx),
                Password = string.IsNullOrEmpty(password) ? null : password,
            },
        };

        return SerializeValidated(document);
    }

    /// <summary>Builds a bundle from a PEM certificate and its PEM private key (optionally encrypted with <paramref name="keyPassword"/>).</summary>
    /// <exception cref="InterCredentialException">The certificate cannot be loaded or does not match the key, or the account number is invalid.</exception>
    public static string CreateFromPem(string certificatePem, string privateKeyPem, string? keyPassword, InterEnvironment environment, string accountNumber)
    {
        var document = new BundleDocument
        {
            Format = Format,
            Version = Version,
            Environment = environment,
            AccountNumber = NormalizeAccountNumber(accountNumber),
            Certificate = new CertificateDocument
            {
                CertificatePem = certificatePem?.Trim(),
                PrivateKeyPem = privateKeyPem?.Trim(),
                Password = string.IsNullOrEmpty(keyPassword) ? null : keyPassword,
            },
        };

        return SerializeValidated(document);
    }

    /// <summary>Validates a bundle and describes it without exposing secret material.</summary>
    /// <exception cref="InterCredentialException">The bundle is malformed or its certificate cannot be loaded.</exception>
    public static InterCredentialBundleInfo Inspect(string bundleJson)
    {
        var document = Deserialize(bundleJson);
        using var certificate = InterClientCertificate.Load(document.Certificate!);
        return new InterCredentialBundleInfo(
            document.Environment,
            document.AccountNumber!,
            certificate.Subject,
            certificate.Thumbprint,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
    }

    /// <summary>
    /// Normalises an account number the way the <c>x-conta-corrente</c> header expects it: digits only,
    /// check digit included, no leading zeros.
    /// </summary>
    /// <exception cref="InterCredentialException">No digits remain, or the value contains letters.</exception>
    public static string NormalizeAccountNumber(string accountNumber)
    {
        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new InterCredentialException("The Inter account number is required.");

        var trimmed = accountNumber.Trim();
        if (!AccountNumberInput().IsMatch(trimmed))
            throw new InterCredentialException("The Inter account number may contain only digits, spaces, dots and dashes.");

        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
        if (digits.Length is < 2 or > 20)
            throw new InterCredentialException("The Inter account number must have between 2 and 20 digits, including the check digit.");

        return digits;
    }

    internal static InterConnectionSettings Parse(string? clientId, string? clientSecret, string? bundleJson)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InterCredentialException("The Inter client id is missing.");
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InterCredentialException("The Inter client secret is missing.");
        if (string.IsNullOrWhiteSpace(bundleJson))
            throw new InterCredentialException("The Inter certificate bundle is missing.");

        var document = Deserialize(bundleJson);
        return new InterConnectionSettings(
            clientId.Trim(),
            clientSecret.Trim(),
            document.Environment,
            document.AccountNumber!,
            document.Certificate!);
    }

    private static string SerializeValidated(BundleDocument document)
    {
        using (InterClientCertificate.Load(document.Certificate!))
        {
            // Loading proves the material is usable before anything is stored.
        }

        return JsonSerializer.Serialize(document, InterCredentialJsonContext.Default.BundleDocument);
    }

    private static BundleDocument Deserialize(string bundleJson)
    {
        BundleDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(bundleJson, InterCredentialJsonContext.Default.BundleDocument);
        }
        catch (JsonException)
        {
            throw new InterCredentialException("The Inter certificate bundle is not valid JSON.");
        }

        if (document is null || !string.Equals(document.Format, Format, StringComparison.Ordinal))
            throw new InterCredentialException($"The Inter certificate bundle is not a '{Format}' document.");
        if (document.Version != Version)
            throw new InterCredentialException($"Inter certificate bundle version {document.Version} is not supported; this package reads version {Version}.");
        if (!Enum.IsDefined(document.Environment))
            throw new InterCredentialException("The Inter certificate bundle names an unknown environment.");
        if (document.Certificate is null)
            throw new InterCredentialException("The Inter certificate bundle has no certificate.");

        document.AccountNumber = NormalizeAccountNumber(document.AccountNumber ?? string.Empty);
        return document;
    }

    [GeneratedRegex(@"^[0-9][0-9 .\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountNumberInput();

    internal sealed class BundleDocument
    {
        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("environment")]
        public InterEnvironment Environment { get; set; }

        [JsonPropertyName("accountNumber")]
        public string? AccountNumber { get; set; }

        [JsonPropertyName("certificate")]
        public CertificateDocument? Certificate { get; set; }
    }

    internal sealed class CertificateDocument
    {
        [JsonPropertyName("pfxBase64")]
        public string? PfxBase64 { get; set; }

        [JsonPropertyName("certificatePem")]
        public string? CertificatePem { get; set; }

        [JsonPropertyName("privateKeyPem")]
        public string? PrivateKeyPem { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }
    }
}

/// <summary>What <see cref="InterCredentialBundle.Inspect"/> reports about a bundle; contains no secret material.</summary>
/// <param name="Environment">Sandbox or production.</param>
/// <param name="AccountNumber">The normalised account number (digits and check digit).</param>
/// <param name="CertificateSubject">The client certificate's subject.</param>
/// <param name="CertificateThumbprint">The client certificate's SHA-1 thumbprint, for display and rotation checks.</param>
/// <param name="CertificateNotBefore">Start of the certificate's validity.</param>
/// <param name="CertificateNotAfter">End of the certificate's validity: Inter rejects the connection after this.</param>
public sealed record InterCredentialBundleInfo(
    InterEnvironment Environment,
    string AccountNumber,
    string CertificateSubject,
    string CertificateThumbprint,
    DateTimeOffset CertificateNotBefore,
    DateTimeOffset CertificateNotAfter);

/// <summary>Inter credentials or certificate material are missing or unusable. The message is safe to show to the user.</summary>
public sealed class InterCredentialException : Exception
{
    /// <summary>Creates the exception with a user-safe message.</summary>
    public InterCredentialException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a user-safe message and the underlying cause.</summary>
    public InterCredentialException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record InterConnectionSettings(
    string ClientId,
    string ClientSecret,
    InterEnvironment Environment,
    string AccountNumber,
    InterCredentialBundle.CertificateDocument Certificate)
{
    /// <summary>Never print secrets, even by accident through a record's generated ToString.</summary>
    public override string ToString() => $"InterConnectionSettings {{ Environment = {Environment} }}";
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InterCredentialBundle.BundleDocument))]
internal sealed partial class InterCredentialJsonContext : JsonSerializerContext;

internal static class InterClientCertificate
{
    /// <summary>
    /// Loads the client certificate with its private key, in a form SslStream can present on every
    /// platform: Windows SChannel cannot use an ephemeral key for client authentication, so there the
    /// PEM pair is round-tripped through PKCS#12 into a (non-persisted) key container.
    /// </summary>
    public static X509Certificate2 Load(InterCredentialBundle.CertificateDocument document)
    {
        var hasPfx = !string.IsNullOrWhiteSpace(document.PfxBase64);
        var hasPem = !string.IsNullOrWhiteSpace(document.CertificatePem) || !string.IsNullOrWhiteSpace(document.PrivateKeyPem);
        if (hasPfx == hasPem)
            throw new InterCredentialException("Supply the Inter certificate either as a PFX file or as a PEM certificate and key pair, not both.");

        X509Certificate2 certificate;
        try
        {
            certificate = hasPfx ? LoadPfx(document) : LoadPem(document);
        }
        catch (InterCredentialException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new InterCredentialException("The Inter certificate or private key could not be read. Check the files and the password.", ex);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InterCredentialException("The Inter certificate has no private key. Supply the key file that came with it.");
        }

        return certificate;
    }

    private static X509Certificate2 LoadPfx(InterCredentialBundle.CertificateDocument document)
    {
        var pfx = Convert.FromBase64String(document.PfxBase64!);
        return X509CertificateLoader.LoadPkcs12(pfx, document.Password, KeyStorageFlags, Pkcs12LoaderLimits.Defaults);
    }

    private static X509Certificate2 LoadPem(InterCredentialBundle.CertificateDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.CertificatePem) || string.IsNullOrWhiteSpace(document.PrivateKeyPem))
            throw new InterCredentialException("Both the Inter certificate (.crt) and its private key (.key) are required.");

        using var pemCertificate = string.IsNullOrEmpty(document.Password)
            ? X509Certificate2.CreateFromPem(document.CertificatePem, document.PrivateKeyPem)
            : X509Certificate2.CreateFromEncryptedPem(document.CertificatePem, document.PrivateKeyPem, document.Password);

        if (!OperatingSystem.IsWindows())
            return X509CertificateLoader.LoadPkcs12(pemCertificate.Export(X509ContentType.Pkcs12), null, KeyStorageFlags, Pkcs12LoaderLimits.Defaults);

        // SChannel needs the key in a key container; export with a throwaway password and reload.
        var transportPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var pkcs12 = pemCertificate.Export(X509ContentType.Pkcs12, transportPassword);
        return X509CertificateLoader.LoadPkcs12(pkcs12, transportPassword, KeyStorageFlags, Pkcs12LoaderLimits.Defaults);
    }

    private static X509KeyStorageFlags KeyStorageFlags =>
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
}
