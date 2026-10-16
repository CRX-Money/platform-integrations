namespace CrxMoney.Integrations.FixtureGuard.Tests;

public sealed class AllowlistTests
{
    // A valid CPF built at run time; see the note in FixtureScannerTests.
    private static readonly string ValidCpf = BrazilianDocuments.CompleteCpf("123456789");

    [Fact]
    public void Requires_a_reason_for_every_value()
    {
        Allowlist.Parse(["# comment", "", ValidCpf], out var errors);

        Assert.Equal(["line 3: add '# reason' after the value"], errors);
    }

    [Fact]
    public void Matches_numbers_regardless_of_punctuation()
    {
        var formatted = $"{ValidCpf[..3]}.{ValidCpf[3..6]}.{ValidCpf[6..9]}-{ValidCpf[9..]}";
        var allowlist = Allowlist.Parse([formatted + "  # documented example used by the parser tests"], out var errors);

        Assert.Empty(errors);
        Assert.True(allowlist.Allows(ValidCpf));
        Assert.False(allowlist.Allows("12345678900"));
    }

    [Fact]
    public void Matches_email_addresses_case_insensitively()
    {
        var allowlist = Allowlist.Parse(["Someone@Bank.Example  # reserved domain spelled in upper case"], out _);

        Assert.True(allowlist.Allows("someone@bank.example"));
    }
}
