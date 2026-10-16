using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.Inter.Api;

namespace CrxMoney.Integrations.Providers.Br.Inter.Sync;

/// <summary>
/// Reads every statement entry of a window with traditional pagination (<c>pagina</c>/<c>tamanhoPagina</c>).
/// Inter serves at most 10,000 entries per window that way; a window reporting more is split in half
/// and read again, down to a single day. That keeps the plugin off the scroll mode, which allows only
/// one active scroll per account and expires after six minutes.
/// </summary>
internal sealed class InterStatementReader(InterApiClient client, InterIntegrationOptions options)
{
    internal const long TraditionalPaginationLimit = 10_000;

    public int PagesRead { get; private set; }

    public async Task<IReadOnlyList<InterTransaction>> ReadAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var transactions = new List<InterTransaction>();
        await ReadWindowAsync(from, to, transactions, cancellationToken).ConfigureAwait(false);
        return transactions;
    }

    private async Task ReadWindowAsync(DateOnly from, DateOnly to, List<InterTransaction> into, CancellationToken cancellationToken)
    {
        var first = await ReadPageAsync(from, to, 0, cancellationToken).ConfigureAwait(false);
        if (first.TotalElementos > TraditionalPaginationLimit)
        {
            if (from == to)
            {
                throw new InterApiException(IntegrationErrorKind.Upstream, null,
                    $"Inter reports more than {TraditionalPaginationLimit:N0} statement entries on {from:yyyy-MM-dd}; one day that large is not supported.");
            }

            var middle = from.AddDays((to.DayNumber - from.DayNumber) / 2);
            await ReadWindowAsync(from, middle, into, cancellationToken).ConfigureAwait(false);
            await ReadWindowAsync(middle.AddDays(1), to, into, cancellationToken).ConfigureAwait(false);
            return;
        }

        var maxPages = (int)Math.Ceiling(TraditionalPaginationLimit / (double)options.StatementPageSize);
        var page = first;
        for (var pageNumber = 0; ; pageNumber++)
        {
            into.AddRange(page.Transacoes ?? []);
            var isLast = page.UltimaPagina == true
                || page.Transacoes is null or { Count: 0 }
                || (page.TotalPaginas is { } totalPages && pageNumber + 1 >= totalPages);
            if (isLast)
                return;

            if (pageNumber + 1 >= maxPages)
            {
                throw new InterApiException(IntegrationErrorKind.Upstream, null,
                    $"Inter kept paging past {maxPages} pages for {from:yyyy-MM-dd}..{to:yyyy-MM-dd} without reporting the last page.");
            }

            page = await ReadPageAsync(from, to, pageNumber + 1, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<InterStatementPage> ReadPageAsync(DateOnly from, DateOnly to, int pageNumber, CancellationToken cancellationToken)
    {
        PagesRead++;
        return await client.GetStatementPageAsync(from, to, pageNumber, options.StatementPageSize, cancellationToken).ConfigureAwait(false);
    }
}
