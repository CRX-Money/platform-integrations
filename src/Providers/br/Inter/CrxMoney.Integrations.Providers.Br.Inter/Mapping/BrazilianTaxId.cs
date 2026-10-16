namespace CrxMoney.Integrations.Providers.Br.Inter.Mapping;

/// <summary>A CPF or CNPJ whose check digits are valid, normalised to digits (and, for the 2026 alphanumeric CNPJ, upper-case letters).</summary>
internal readonly record struct BrazilianTaxId(string Value, string Kind)
{
    public const string Cpf = "CPF";
    public const string Cnpj = "CNPJ";

    /// <summary>
    /// Accepts a formatted or bare CPF/CNPJ and returns it only when the check digits are right.
    /// Masked values (<c>***.456.789-**</c>) and placeholders fail, which keeps a guessed field from
    /// being used as a tax id.
    /// </summary>
    public static BrazilianTaxId? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        Span<char> buffer = stackalloc char[14];
        var length = 0;
        foreach (var c in raw)
        {
            if (c is '.' or '-' or '/' or ' ')
                continue;
            if (!char.IsAsciiLetterOrDigit(c) || length == buffer.Length)
                return null;
            buffer[length++] = char.ToUpperInvariant(c);
        }

        var value = buffer[..length];
        if (length == 11 && IsValidCpf(value))
            return new BrazilianTaxId(value.ToString(), Cpf);
        if (length == 14 && IsValidCnpj(value))
            return new BrazilianTaxId(value.ToString(), Cnpj);
        return null;
    }

    private static bool IsValidCpf(ReadOnlySpan<char> cpf)
    {
        foreach (var c in cpf)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        if (AllSame(cpf))
            return false;

        return cpf[9] - '0' == CpfDigit(cpf[..9], 10) && cpf[10] - '0' == CpfDigit(cpf[..10], 11);

        static int CpfDigit(ReadOnlySpan<char> digits, int firstWeight)
        {
            var sum = 0;
            for (var i = 0; i < digits.Length; i++)
                sum += (digits[i] - '0') * (firstWeight - i);
            var remainder = sum * 10 % 11;
            return remainder == 10 ? 0 : remainder;
        }
    }

    // Receita Federal's CNPJ algorithm; since July 2026 the first 12 positions may be letters,
    // valued as their ASCII code minus 48 ('0'), and the two check digits stay numeric.
    private static bool IsValidCnpj(ReadOnlySpan<char> cnpj)
    {
        if (!char.IsAsciiDigit(cnpj[12]) || !char.IsAsciiDigit(cnpj[13]) || AllSame(cnpj))
            return false;

        return cnpj[12] - '0' == CnpjDigit(cnpj[..12]) && cnpj[13] - '0' == CnpjDigit(cnpj[..13]);

        static int CnpjDigit(ReadOnlySpan<char> chars)
        {
            var sum = 0;
            var weight = 2;
            for (var i = chars.Length - 1; i >= 0; i--)
            {
                sum += (chars[i] - '0') * weight;
                weight = weight == 9 ? 2 : weight + 1;
            }

            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }
    }

    private static bool AllSame(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c != value[0])
                return false;
        }

        return true;
    }
}
