using System.Globalization;
using System.Text.RegularExpressions;

namespace CrxMoney.Integrations.FixtureGuard;

/// <summary>A value in a fixture that looks like a real identifier.</summary>
/// <param name="Path">Repository-relative file path.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Rule">Rule id, for example FG001.</param>
/// <param name="Description">What the value looks like.</param>
/// <param name="Value">The matched value. Never print it unmasked: it may be real personal data.</param>
internal sealed record Finding(string Path, int Line, string Rule, string Description, string Value)
{
    /// <summary>The value with everything after the first three characters hidden.</summary>
    public string Masked => Value.Length <= 3 ? new string('*', Value.Length) : Value[..3] + new string('*', Value.Length - 3);
}

/// <summary>
/// Scans text for values resembling real CPF/CNPJ numbers, Pix keys, phone numbers, e-mail
/// addresses and bank account numbers. The rules are deliberately strict; genuine synthetic values
/// that trip them go in the allowlist with a reason.
/// </summary>
internal static partial class FixtureScanner
{
    /// <summary>Lines either side of an agency number within which an account number counts as a pair.</summary>
    private const int AgencyAccountWindow = 3;

    public static IEnumerable<Finding> Scan(string path, string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var agencyLines = new List<int>();
        var accountCandidates = new List<(int Line, string Value)>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var number = index + 1;
            var pixContext = line.Contains("pix", StringComparison.OrdinalIgnoreCase)
                || line.Contains("chave", StringComparison.OrdinalIgnoreCase);
            var cnpjContext = line.Contains("cnpj", StringComparison.OrdinalIgnoreCase);

            foreach (Match match in FormattedCpf().Matches(line).Concat(BareCpf().Matches(line)))
            {
                if (BrazilianDocuments.IsValidCpf(match.Value) && !BrazilianDocuments.IsRepeatedCharacter(match.Value))
                {
                    yield return new Finding(path, number, "FG001", "CPF with valid check digits", match.Value);
                }
            }

            var cnpjMatches = FormattedCnpj().Matches(line).Concat(BareNumericCnpj().Matches(line));
            if (cnpjContext)
            {
                cnpjMatches = cnpjMatches.Concat(BareAlphanumericCnpj().Matches(line));
            }

            foreach (var match in cnpjMatches.DistinctBy(match => match.Index))
            {
                if (BrazilianDocuments.IsValidCnpj(match.Value)
                    && !BrazilianDocuments.IsRepeatedCharacter(match.Value)
                    && !IsTimestamp(match.Value))
                {
                    yield return new Finding(path, number, "FG002", "CNPJ with valid check digits", match.Value);
                }
            }

            foreach (Match match in Email().Matches(line))
            {
                if (!SyntheticValues.IsSyntheticEmail(match.Value))
                {
                    yield return new Finding(path, number, "FG003", "e-mail address (possible Pix key) outside reserved domains", match.Value);
                }
            }

            foreach (Match match in Phone().Matches(line))
            {
                if (!SyntheticValues.IsSyntheticPhone(match.Groups["ddd"].Value))
                {
                    yield return new Finding(path, number, "FG004", "Brazilian phone number (possible Pix key) with a real area code", match.Value);
                }
            }

            if (pixContext)
            {
                foreach (Match match in Uuid().Matches(line))
                {
                    if (!SyntheticValues.IsSyntheticUuid(match.Value))
                    {
                        yield return new Finding(path, number, "FG005", "random Pix key (EVP) not starting with 00000000-", match.Value);
                    }
                }
            }

            foreach (Match match in OfxAccountId().Matches(line))
            {
                var value = match.Groups["value"].Value.Trim();
                if (value.Length > 0 && !SyntheticValues.IsSyntheticAccount(value))
                {
                    yield return new Finding(path, number, "FG007", "OFX <ACCTID> that is not obviously synthetic", value);
                }
            }

            if (Agency().IsMatch(line))
            {
                agencyLines.Add(number);
            }

            foreach (Match match in Account().Matches(line))
            {
                accountCandidates.Add((number, match.Groups["value"].Value));
            }
        }

        foreach (var (line, value) in accountCandidates)
        {
            if (agencyLines.Any(agencyLine => Math.Abs(agencyLine - line) <= AgencyAccountWindow)
                && !SyntheticValues.IsSyntheticAccount(value))
            {
                yield return new Finding(path, line, "FG006", "bank account number next to an agency number", value);
            }
        }
    }

    /// <summary>OFX and API timestamps (yyyyMMddHHmmss) are 14 digits too; they are not CNPJs.</summary>
    private static bool IsTimestamp(string value) =>
        value.Length == 14
        && DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        && parsed.Year is >= 1900 and <= 2100;

    [GeneratedRegex(@"(?<![\d.])\d{3}\.\d{3}\.\d{3}-\d{2}(?!\d)")]
    private static partial Regex FormattedCpf();

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)")]
    private static partial Regex BareCpf();

    [GeneratedRegex(@"(?<![0-9A-Za-z.])[0-9A-Z]{2}\.[0-9A-Z]{3}\.[0-9A-Z]{3}/[0-9A-Z]{4}-\d{2}(?!\d)")]
    private static partial Regex FormattedCnpj();

    [GeneratedRegex(@"(?<!\d)\d{14}(?!\d)")]
    private static partial Regex BareNumericCnpj();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9A-Z]{12}\d{2}(?![0-9A-Za-z])")]
    private static partial Regex BareAlphanumericCnpj();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // +55 11 91234-5678, +5511912345678, (11) 91234-5678, (11) 3123-4567.
    [GeneratedRegex(@"(?:\+55[\s\-]?\(?(?<ddd>\d{2})\)?[\s\-]?9?\d{4}[\s\-]?\d{4}|\((?<ddd>\d{2})\)\s?9?\d{4}-?\d{4})(?!\d)")]
    private static partial Regex Phone();

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?![0-9a-fA-F])")]
    private static partial Regex Uuid();

    [GeneratedRegex(@"<ACCTID>(?<value>[^<\r\n]*)", RegexOptions.IgnoreCase)]
    private static partial Regex OfxAccountId();

    [GeneratedRegex(@"(?i)(?:ag[eê]ncia|\bag\b\.?|branch(?:id)?|bankbranch)\W{0,4}\d{3,5}")]
    private static partial Regex Agency();

    [GeneratedRegex(@"(?i)(?:conta(?:\s?corrente)?|n[uú]mero\s?(?:da\s)?conta|\bc/?c\b|account(?:number)?)\W{0,4}(?<value>\d[\d.\-]{3,18}[\dXx]?)")]
    private static partial Regex Account();
}
