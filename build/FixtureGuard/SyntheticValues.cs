namespace CrxMoney.Integrations.FixtureGuard;

/// <summary>
/// The value forms fixtures may use because they are obviously not real. Documented for
/// contributors in docs/fixture-policy.md; keep the two in sync.
/// </summary>
internal static class SyntheticValues
{
    private static readonly string[] Markers = ["TEST", "SYNTH", "FAKE", "EXAMPLE", "DEMO", "XXXX"];

    private static readonly string[] ReservedEmailDomains = ["example.com", "example.org", "example.net"];

    private static readonly string[] ReservedEmailSuffixes = [".example", ".test", ".invalid", ".localhost"];

    /// <summary>
    /// A bank account number or OFX account id is synthetic when it carries a marker (TEST, SYNTH,
    /// FAKE, EXAMPLE, DEMO, XXXX), has fewer than five digits, repeats a single digit, or is an
    /// ascending run such as 12345678-9.
    /// </summary>
    public static bool IsSyntheticAccount(string value)
    {
        var upper = value.ToUpperInvariant();
        if (Markers.Any(upper.Contains))
        {
            return true;
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length < 5 || BrazilianDocuments.IsRepeatedCharacter(digits))
        {
            return true;
        }

        for (var i = 1; i < digits.Length; i++)
        {
            if (digits[i] - '0' != (digits[i - 1] - '0' + 1) % 10)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>E-mail addresses are synthetic only on reserved domains (RFC 2606 / RFC 6761).</summary>
    public static bool IsSyntheticEmail(string email)
    {
        var at = email.LastIndexOf('@');
        if (at < 0)
        {
            return false;
        }

        var domain = email[(at + 1)..].ToLowerInvariant();
        return ReservedEmailDomains.Any(reserved => domain == reserved || domain.EndsWith("." + reserved, StringComparison.Ordinal))
            || ReservedEmailSuffixes.Any(domain.EndsWith);
    }

    /// <summary>
    /// Brazilian phone numbers are synthetic when the area code (DDD) ends in 0, for example
    /// +55 10 91234-5678: no Brazilian DDD ends in 0, so such a number cannot be dialled.
    /// </summary>
    public static bool IsSyntheticPhone(string areaCode) => areaCode.Length == 2 && areaCode[1] == '0';

    /// <summary>Pix random keys (EVP) are synthetic when the first group is all zeros: 00000000-xxxx-....</summary>
    public static bool IsSyntheticUuid(string uuid) => uuid.StartsWith("00000000-", StringComparison.Ordinal);
}
