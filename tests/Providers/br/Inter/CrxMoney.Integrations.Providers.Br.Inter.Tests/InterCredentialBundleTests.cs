using System.Text.Json.Nodes;
using CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests;

public sealed class InterCredentialBundleTests
{
    [Fact]
    public void Pem_pair_round_trips_and_reports_the_certificate_expiry()
    {
        var notAfter = new DateTimeOffset(2027, 6, 30, 0, 0, 0, TimeSpan.Zero);
        var (certificatePem, keyPem) = TestCertificates.PemPair(notAfter);

        var bundle = InterCredentialBundle.CreateFromPem(certificatePem, keyPem, null, InterEnvironment.Sandbox, "0001234567-8");
        var info = InterCredentialBundle.Inspect(bundle);

        Assert.Equal(InterEnvironment.Sandbox, info.Environment);
        Assert.Equal("12345678", info.AccountNumber);
        Assert.Equal(notAfter, info.CertificateNotAfter);
        Assert.Equal("CN=synthetic-inter-client.example", info.CertificateSubject);
        Assert.Equal("crx.inter.credentials", JsonNode.Parse(bundle)!["format"]!.GetValue<string>());
        Assert.Equal("Sandbox", JsonNode.Parse(bundle)!["environment"]!.GetValue<string>());
    }

    [Fact]
    public void Pfx_with_password_round_trips()
    {
        var pfx = TestCertificates.Pfx("synthetic-pfx-password");

        var bundle = InterCredentialBundle.CreateFromPfx(pfx, "synthetic-pfx-password", InterEnvironment.Production, "98765");

        Assert.Equal(InterEnvironment.Production, InterCredentialBundle.Inspect(bundle).Environment);
    }

    [Fact]
    public void Wrong_pfx_password_is_reported_without_echoing_it()
    {
        var pfx = TestCertificates.Pfx("synthetic-pfx-password");

        var error = Assert.Throws<InterCredentialException>(() =>
            InterCredentialBundle.CreateFromPfx(pfx, "wrong-synthetic-password", InterEnvironment.Sandbox, "98765"));

        Assert.Contains("password", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-synthetic-password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Certificate_without_its_key_is_rejected()
    {
        Assert.Throws<InterCredentialException>(() =>
            InterCredentialBundle.CreateFromPem(TestCertificates.CertificateOnlyPem(), string.Empty, null, InterEnvironment.Sandbox, "98765"));
    }

    [Fact]
    public void Key_that_does_not_match_the_certificate_is_rejected()
    {
        var (certificatePem, _) = TestCertificates.PemPair();
        var (_, otherKeyPem) = TestCertificates.PemPair();

        Assert.Throws<InterCredentialException>(() =>
            InterCredentialBundle.CreateFromPem(certificatePem, otherKeyPem, null, InterEnvironment.Sandbox, "98765"));
    }

    [Theory]
    [InlineData("1234567-8", "12345678")]
    [InlineData(" 00012.345-6 ", "123456")]
    [InlineData("98765", "98765")]
    public void Account_numbers_are_normalised_for_the_header(string input, string expected)
    {
        Assert.Equal(expected, InterCredentialBundle.NormalizeAccountNumber(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("conta-1")]
    [InlineData("0")]
    public void Unusable_account_numbers_are_rejected(string input)
    {
        Assert.Throws<InterCredentialException>(() => InterCredentialBundle.NormalizeAccountNumber(input));
    }

    [Fact]
    public void Malformed_bundles_are_rejected_with_safe_messages()
    {
        Assert.Throws<InterCredentialException>(() => InterCredentialBundle.Inspect("not json"));
        Assert.Throws<InterCredentialException>(() => InterCredentialBundle.Inspect("""{"format":"crx.inter.credentials","version":2}"""));
        Assert.Throws<InterCredentialException>(() => InterCredentialBundle.Inspect("""{"format":"crx.inter.credentials","version":1,"accountNumber":"98765"}"""));
    }

    [Fact]
    public void Parsed_settings_never_print_secrets()
    {
        var settings = InterCredentialBundle.Parse("synthetic-client-id", "synthetic-client-secret", TestCertificates.Bundle());

        Assert.DoesNotContain("synthetic-client-secret", settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", settings.ToString(), StringComparison.Ordinal);
    }
}
