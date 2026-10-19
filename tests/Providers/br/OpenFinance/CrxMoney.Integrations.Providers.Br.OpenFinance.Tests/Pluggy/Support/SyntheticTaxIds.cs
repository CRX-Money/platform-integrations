using System.Globalization;
using System.Text.RegularExpressions;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy.Support;

/// <summary>
/// Valid CPF/CNPJ numbers built at run time from synthetic bases, so no valid number is committed
/// (docs/fixture-policy.md). Written independently of the production check-digit code.
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
        var first = Digit(nineDigits);
        return nineDigits + first + Digit(nineDigits + first);

        static char Digit(string digits)
        {
            var weight = digits.Length + 1;
            var remainder = digits.Sum(d => (d - '0') * weight--) % 11;
            return remainder < 2 ? '0' : (char)('0' + 11 - remainder);
        }
    }

    public static string CompleteCnpj(string twelveCharacters)
    {
        int[] first = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] second = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        var upper = twelveCharacters.ToUpperInvariant();
        var withFirst = upper + Digit(upper, first);
        return withFirst + Digit(withFirst, second);

        static char Digit(string chars, int[] weights)
        {
            var remainder = chars.Select((c, i) => (c - '0') * weights[i]).Sum() % 11;
            return remainder < 2 ? '0' : (char)('0' + 11 - remainder);
        }
    }

    [GeneratedRegex(@"\{\{(?<kind>CPF|CNPJ):(?<n>[0-9]+)\}\}")]
    private static partial Regex Placeholder();
}
