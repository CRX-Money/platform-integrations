using System.Text.RegularExpressions;

namespace CrxMoney.Integrations.Countries.Br;

/// <summary>
/// A Brazilian deposit account identified the way the banks themselves identify it: the institution's
/// COMPE code, the branch and the account number with its check digit. This identity is the same
/// whichever aggregator, API or file it is read from, which is what lets imports from different
/// sources land on the same ledger account.
/// </summary>
/// <param name="Compe">Three-digit COMPE code of the institution (<c>077</c> for Banco Inter).</param>
/// <param name="Branch">Branch (agência) digits without its check digit, leading zeros kept (<c>0001</c>).</param>
/// <param name="Number">Account number digits including the check digit, leading zeros removed.</param>
public sealed partial record BrazilianBankAccount(string Compe, string Branch, string Number)
{
    /// <summary>
    /// The external account id used by every Brazilian bank source: <c>br:{compe}:{branch}:{number}</c>.
    /// Changing it would detach existing account maps; treat it as a released contract.
    /// </summary>
    public string ExternalAccountId => $"br:{Compe}:{Branch}:{Number}";

    /// <summary>The external account id of a card issued by <paramref name="compe"/>: <c>br:{compe}:card:{last4}</c>.</summary>
    public static string CardExternalAccountId(string compe, string last4) => $"br:{NormalizeCompe(compe)}:card:{last4}";

    /// <summary>
    /// The external account id of an investment position held at <paramref name="compe"/>:
    /// <c>br:{compe}:inv:{basis}:{key}</c>. The basis says what the key is (<c>number</c>, <c>isin</c>, <c>code</c>,
    /// or a source name when only the aggregator's id is available); the key keeps letters and digits only, upper-cased.
    /// </summary>
    /// <exception cref="FormatException">The basis or the key has no letters or digits.</exception>
    public static string InvestmentExternalAccountId(string compe, string basis, string key)
    {
        var normalizedBasis = Alphanumeric(basis).ToLowerInvariant();
        var normalizedKey = Alphanumeric(key);
        if (normalizedBasis.Length == 0 || normalizedKey.Length == 0)
            throw new FormatException("An investment id needs a basis and a key with letters or digits.");
        return $"br:{NormalizeCompe(compe)}:inv:{normalizedBasis}:{normalizedKey}";
    }

    private static string Alphanumeric(string? value) =>
        new((value ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// Builds the identity from its parts, normalising punctuation: the COMPE code is padded to three
    /// digits, the branch loses a check digit after a dash (<c>1234-5</c> becomes <c>1234</c>, as most
    /// sources omit it), and the account number keeps its check digit but loses dashes, dots and leading zeros.
    /// </summary>
    /// <exception cref="FormatException">A part is empty or contains anything but digits and separators.</exception>
    public static BrazilianBankAccount Create(string compe, string branch, string number)
    {
        var dash = branch?.IndexOf('-') ?? -1;
        var normalizedBranch = Digits(dash > 0 ? branch![..dash] : branch!, "branch");
        var normalizedNumber = Digits(number, "account number").TrimStart('0');
        if (normalizedNumber.Length == 0)
            throw new FormatException("The account number has no significant digits.");
        return new BrazilianBankAccount(NormalizeCompe(compe), normalizedBranch, normalizedNumber);
    }

    /// <summary>
    /// Parses the <c>COMPE/branch/account-digit</c> form some aggregators expose
    /// (for example Pluggy's <c>bankData.transferNumber</c>, <c>077/0001/12345678-9</c>).
    /// </summary>
    public static BrazilianBankAccount? TryParseTransferNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = TransferNumberPattern().Match(value.Trim());
        if (!match.Success)
            return null;

        try
        {
            return Create(match.Groups["compe"].Value, match.Groups["branch"].Value, match.Groups["number"].Value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Pads a COMPE code to three digits.</summary>
    /// <exception cref="FormatException">The code is not one to three digits.</exception>
    public static string NormalizeCompe(string compe)
    {
        var digits = Digits(compe, "COMPE code");
        if (digits.Length > 3)
            throw new FormatException("A COMPE code has at most three digits.");
        return digits.PadLeft(3, '0');
    }

    private static string Digits(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"The {what} is empty.");

        var digits = new char[value.Length];
        var length = 0;
        foreach (var c in value.Trim())
        {
            if (char.IsAsciiDigit(c))
                digits[length++] = c;
            else if (c is not ('-' or '.' or ' '))
                throw new FormatException($"The {what} may only contain digits, dashes, dots and spaces.");
        }

        if (length == 0)
            throw new FormatException($"The {what} has no digits.");
        return new string(digits, 0, length);
    }

    [GeneratedRegex(@"^(?<compe>\d{1,3})\s*/\s*(?<branch>[\d.\- ]+?)\s*/\s*(?<number>[\d.\- ]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TransferNumberPattern();
}
