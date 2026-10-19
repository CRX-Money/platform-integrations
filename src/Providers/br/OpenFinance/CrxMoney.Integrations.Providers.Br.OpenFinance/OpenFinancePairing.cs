using CrxMoney.Integrations.Countries.Br;
namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

/// <summary>
/// Finds the other side of transfer-like movements the bank reports on two accounts of the same connection: a
/// checking-account sweep and the investment movement it funded or came from, and a card bill paid from checking and
/// the payment the card received. The match is the same amount within a few days and one candidate account; it only
/// adds provider data (<c>pair</c>, <c>card</c>, <c>investment</c>), never changes keys or amounts.
/// </summary>
/// <remarks>
/// Matching runs over everything the read returned (including lines before the send window), in a fixed order
/// (booking date, then key), so the same read always gives the same pairs.
/// </remarks>
internal static class OpenFinancePairing
{
    /// <summary>Days an investment movement may be booked before or after its cash line.</summary>
    public const int InvestmentToleranceDays = 2;

    /// <summary>Days the card may book a payment before the checking debit.</summary>
    public const int CardEarlierDays = 1;

    /// <summary>Days the card may book a payment after the checking debit.</summary>
    public const int CardLaterDays = 3;

    /// <summary>
    /// A movement that can take part in a pair. <paramref name="CashDirection"/> is always the direction of the
    /// money on the cash account: a sweep into a position and the application it funded are both <c>Debit</c>.
    /// </summary>
    public sealed record Side(string Key, string ExternalAccountId, OpenFinanceDirection CashDirection, decimal Amount, DateOnly BookingDate, bool IsSent)
    {
        /// <summary>The card line (card side) or the position (investment side) behind this side.</summary>
        public object? Origin { get; init; }
    }

    /// <summary>Counts for the run diagnostics, over the lines sent.</summary>
    public sealed record Counts(int Lines, int Paired, int Ambiguous, int Unmatched, int OtherSideUnpaired);

    /// <summary>
    /// Pairs each cash-side movement with at most one other-side movement of the opposite direction, same amount,
    /// booked between <paramref name="earlierDays"/> before and <paramref name="laterDays"/> after it. Candidates on
    /// more than one account at the same distance make the line ambiguous; it stays unpaired.
    /// </summary>
    public static (IReadOnlyDictionary<string, Side> CashToOther, IReadOnlyDictionary<string, Side> OtherToCash, Counts Counts) Match(
        IEnumerable<Side> cashSide,
        IEnumerable<Side> otherSide,
        int earlierDays,
        int laterDays)
    {
        var others = otherSide.OrderBy(o => o.BookingDate).ThenBy(o => o.Key, StringComparer.Ordinal).ToList();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var cashToOther = new Dictionary<string, Side>(StringComparer.Ordinal);
        var otherToCash = new Dictionary<string, Side>(StringComparer.Ordinal);
        int lines = 0, paired = 0, ambiguous = 0, unmatched = 0;

        foreach (var cash in cashSide.OrderBy(c => c.BookingDate).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var candidates = others
                .Where(o => !claimed.Contains(o.Key)
                    && o.CashDirection == cash.CashDirection
                    && o.Amount == cash.Amount
                    && o.BookingDate.DayNumber - cash.BookingDate.DayNumber is var delta
                    && delta >= -earlierDays && delta <= laterDays)
                .ToList();
            var best = candidates.Count == 0 ? null : candidates
                .GroupBy(o => Math.Abs(o.BookingDate.DayNumber - cash.BookingDate.DayNumber))
                .OrderBy(g => g.Key)
                .First()
                .ToList();
            Side? match = best is not null && best.Select(o => o.ExternalAccountId).Distinct(StringComparer.Ordinal).Count() == 1 ? best[0] : null;
            if (match is not null)
            {
                claimed.Add(match.Key);
                cashToOther[cash.Key] = match;
                otherToCash[match.Key] = cash;
            }

            if (!cash.IsSent)
                continue;
            lines++;
            if (match is not null)
                paired++;
            else if (best is not null)
                ambiguous++;
            else
                unmatched++;
        }

        var otherUnpaired = others.Count(o => o.IsSent && !otherToCash.ContainsKey(o.Key));
        return (cashToOther, otherToCash, new Counts(lines, paired, ambiguous, unmatched, otherUnpaired));
    }
}
