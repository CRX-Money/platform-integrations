namespace CrxMoney.Integrations.FixtureGuard;

/// <summary>
/// Check-digit rules for CPF (individuals) and CNPJ (companies), including the alphanumeric CNPJ
/// introduced by the Receita Federal in 2026.
/// </summary>
internal static class BrazilianDocuments
{
    private static readonly int[] CnpjFirstWeights = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
    private static readonly int[] CnpjSecondWeights = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    /// <summary>True when <paramref name="value"/> is 11 digits with valid CPF check digits.</summary>
    public static bool IsValidCpf(string value)
    {
        var digits = Strip(value);
        if (digits.Length != 11 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        return digits[9] == CpfCheckDigit(digits[..9]) && digits[10] == CpfCheckDigit(digits[..10]);
    }

    /// <summary>True when <paramref name="value"/> is a numeric or alphanumeric CNPJ with valid check digits.</summary>
    public static bool IsValidCnpj(string value)
    {
        var chars = Strip(value).ToUpperInvariant();
        if (chars.Length != 14
            || !chars[..12].All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c))
            || !chars[12..].All(char.IsAsciiDigit))
        {
            return false;
        }

        return chars[12] == CnpjCheckDigit(chars[..12], CnpjFirstWeights)
            && chars[13] == CnpjCheckDigit(chars[..13], CnpjSecondWeights);
    }

    /// <summary>Appends the two CPF check digits to a 9-digit base.</summary>
    public static string CompleteCpf(string nineDigits)
    {
        var withFirst = nineDigits + CpfCheckDigit(nineDigits);
        return withFirst + CpfCheckDigit(withFirst);
    }

    /// <summary>Appends the two CNPJ check digits to a 12-character base.</summary>
    public static string CompleteCnpj(string twelveCharacters)
    {
        var upper = twelveCharacters.ToUpperInvariant();
        var withFirst = upper + CnpjCheckDigit(upper, CnpjFirstWeights);
        return withFirst + CnpjCheckDigit(withFirst, CnpjSecondWeights);
    }

    /// <summary>True when every character is the same, for example 111.111.111-11. Such numbers are never issued.</summary>
    public static bool IsRepeatedCharacter(string value)
    {
        var chars = Strip(value);
        return chars.Length > 0 && chars.All(c => c == chars[0]);
    }

    private static string Strip(string value) => new(value.Where(char.IsAsciiLetterOrDigit).ToArray());

    private static char CpfCheckDigit(string digits)
    {
        var weight = digits.Length + 1;
        var sum = 0;
        foreach (var digit in digits)
        {
            sum += (digit - '0') * weight--;
        }

        var remainder = sum % 11;
        return remainder < 2 ? '0' : (char)('0' + (11 - remainder));
    }

    private static char CnpjCheckDigit(string characters, int[] weights)
    {
        var sum = 0;
        for (var i = 0; i < characters.Length; i++)
        {
            // Receita rule for alphanumeric CNPJ: a character's value is its ASCII code minus 48.
            sum += (characters[i] - '0') * weights[i];
        }

        var remainder = sum % 11;
        return remainder < 2 ? '0' : (char)('0' + (11 - remainder));
    }
}
