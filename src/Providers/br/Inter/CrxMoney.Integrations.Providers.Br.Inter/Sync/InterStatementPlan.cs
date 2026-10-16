using System.Globalization;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.Inter.Mapping;

namespace CrxMoney.Integrations.Providers.Br.Inter.Sync;

/// <summary>
/// Which Brazilian calendar days one sync reads, split into windows Inter accepts.
/// </summary>
/// <remarks>
/// The start is the earliest of: the host's window start, and the last synced date (checkpoint)
/// minus <see cref="InterIntegrationOptions.StatementOverlapDays"/>. Without either (a full-history
/// backfill) it is the plugin's suggested backfill date. The end is the host's window end, never
/// later than today in Brazil. The checkpoint written back is the end date.
/// </remarks>
internal sealed record InterStatementPlan(DateOnly From, DateOnly To, IReadOnlyList<(DateOnly From, DateOnly To)> Windows)
{
    public const string CheckpointResource = "statement";

    public bool IsEmpty => Windows.Count == 0;

    public static InterStatementPlan Create(
        IntegrationSyncRequest request,
        DateOnly today,
        DateOnly fullHistoryFrom,
        InterIntegrationOptions options)
    {
        var checkpoint = ReadCheckpoint(request);

        DateOnly? from = request.WindowStartUtc is { } start ? InterTransactionMapper.BankingDate(start) : null;
        if (checkpoint is { } lastSynced)
        {
            var resumeFrom = lastSynced.AddDays(-options.StatementOverlapDays);
            from = from is { } requested && requested < resumeFrom ? requested : resumeFrom;
        }

        var effectiveFrom = from ?? fullHistoryFrom;
        var to = request.WindowEndUtc is { } end ? InterTransactionMapper.BankingDate(end) : today;
        if (to > today)
            to = today;

        var windows = new List<(DateOnly, DateOnly)>();
        for (var windowFrom = effectiveFrom; windowFrom <= to; windowFrom = windowFrom.AddDays(options.StatementWindowDays))
        {
            var windowTo = windowFrom.AddDays(options.StatementWindowDays - 1);
            windows.Add((windowFrom, windowTo < to ? windowTo : to));
        }

        return new InterStatementPlan(effectiveFrom, to, windows);
    }

    public static IntegrationCheckpoint CheckpointFor(DateOnly lastSyncedDate) =>
        new(CheckpointResource, lastSyncedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <exception cref="FormatException">The host passed back a statement checkpoint this plugin did not write.</exception>
    internal static DateOnly? ReadCheckpoint(IntegrationSyncRequest request)
    {
        var cursor = request.PreviousCheckpoints?
            .LastOrDefault(c => string.Equals(c.Resource, CheckpointResource, StringComparison.Ordinal))?
            .CursorValue;
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        // The host may echo the cursor back as an ISO timestamp ("2026-03-10T00:00:00.0000000+00:00").
        var datePart = cursor.Length >= 10 ? cursor[..10] : cursor;
        if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;

        throw new FormatException($"The Inter '{CheckpointResource}' checkpoint is not a yyyy-MM-dd date.");
    }
}
