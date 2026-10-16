using System.Globalization;
using System.Text.RegularExpressions;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;

/// <summary>
/// Valid CPF/CNPJ numbers built at run time from synthetic bases, so no valid number is ever committed
/// (docs/fixture-policy.md). Fixtures refer to them as <c>{{CPF:n}}</c> and <c>{{CNPJ:n}}</c>.
/// The check-digit code is written independently of the plugin's, so the tests check one against the other.
/// </summary>
internal static partial class SyntheticTaxIds
{
    public static string Cpf(int n) => CompleteCpf((100_000_000 + n * 7_919).ToString("D9", CultureInfo.InvariantCulture));

    public static string Cnpj(int n) => CompleteCnpj((10_000_000 + n * 104_729).ToString("D8", CultureInfo.InvariantCulture) + "0001");

    public static string Fill(string text) => Placeholder().Replace(text, match =>
    {
        var n = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        return match.Groups["kind"].Value == "CPF" ? Cpf(n) : Cnpj(n);
    });

    public static string CompleteCpf(string nineDigits)
    {
        var first = CpfDigit(nineDigits);
        return nineDigits + first + CpfDigit(nineDigits + first);

        static char CpfDigit(string digits)
        {
            var weight = digits.Length + 1;
            var sum = digits.Sum(d => (d - '0') * weight--);
            var remainder = sum % 11;
            return remainder < 2 ? '0' : (char)('0' + 11 - remainder);
        }
    }

    public static string CompleteCnpj(string twelveCharacters)
    {
        int[] firstWeights = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] secondWeights = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        var upper = twelveCharacters.ToUpperInvariant();
        var withFirst = upper + CnpjDigit(upper, firstWeights);
        return withFirst + CnpjDigit(withFirst, secondWeights);

        static char CnpjDigit(string chars, int[] weights)
        {
            var sum = chars.Select((c, i) => (c - '0') * weights[i]).Sum();
            var remainder = sum % 11;
            return remainder < 2 ? '0' : (char)('0' + 11 - remainder);
        }
    }

    [GeneratedRegex(@"\{\{(?<kind>CPF|CNPJ):(?<n>[0-9]+)\}\}")]
    private static partial Regex Placeholder();
}
