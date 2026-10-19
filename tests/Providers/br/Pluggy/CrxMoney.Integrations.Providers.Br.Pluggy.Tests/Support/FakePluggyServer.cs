using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CrxMoney.Integrations.Providers.Br.Pluggy.Tests.Support;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string Path => Uri.AbsolutePath;

    public string? Query(string name) => HttpUtility.ParseQueryString(Uri.Query)[name];

    public IReadOnlyList<string> QueryNames() => [.. HttpUtility.ParseQueryString(Uri.Query).AllKeys.Select(k => k ?? string.Empty)];
}

/// <summary>An in-memory Pluggy API serving the synthetic fixtures. No test calls api.pluggy.ai.</summary>
internal sealed class FakePluggyServer : HttpMessageHandler
{
    public const string ItemId = "00000000-0000-4000-8000-0000000000a1";
    public const string BankAccountId = "00000000-0000-4000-8000-0000000000b1";
    public const string CardAccountId = "00000000-0000-4000-8000-0000000000b2";

    private static readonly string[] TransactionQueryNames = ["accountId", "ids", "dateFrom", "dateTo", "createdAtFrom", "after"];
    private static readonly string[] InvestmentQueryNames = ["itemId", "type", "page", "pageSize"];
    private static readonly string[] InvestmentTransactionQueryNames = ["page", "pageSize"];

    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly Dictionary<string, Func<RecordedRequest, int, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Query, int Offset)> _cursors = new(StringComparer.Ordinal);

    private FakePluggyServer()
    {
        On("/v2/transactions", (request, _) => Transactions(request));
        On("/investments", (request, _) => Investments(request));
        foreach (var investmentId in InvestmentIds)
            On($"/investments/{investmentId}/transactions", (request, _) => InvestmentTransactions(request, investmentId));
    }

    /// <summary>The positions of <c>investments.json</c>, each with a <c>/investments/{id}/transactions</c> route.</summary>
    public static readonly string[] InvestmentIds =
        ["00000000-0000-4000-8000-0000000000f1", "00000000-0000-4000-8000-0000000000f2", "00000000-0000-4000-8000-0000000000f3"];

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    public IReadOnlyList<RecordedRequest> RequestsTo(string path) => [.. _requests.Where(r => r.Path == path)];

    public IReadOnlyList<RecordedRequest> TransactionRequests(string accountId) =>
        [.. RequestsTo("/v2/transactions").Where(r => r.Query("accountId") == accountId)];

    /// <summary>Lines per <c>/v2/transactions</c> page; Pluggy's default is 500.</summary>
    public int TransactionPageSize { get; set; } = 500;

    /// <summary>Results per <c>/investments</c> and <c>/investments/{id}/transactions</c> page when the client sends no <c>pageSize</c>; Pluggy's default is 500.</summary>
    public int InvestmentPageSize { get; set; } = 500;

    public static FakePluggyServer WithFixtures() => new FakePluggyServer()
        .On("/auth", (_, call) => Json($$"""{"apiKey":"synthetic-api-key-{{call}}"}"""))
        .On($"/items/{ItemId}", (_, _) => Json(Fixtures.Text("item.json")))
        .On("/accounts", (_, _) => Json(Fixtures.Text("accounts.json")));

    /// <summary>
    /// <c>GET /v2/transactions</c> as documented: <c>dateFrom</c>/<c>dateTo</c> (<c>yyyy-mm-dd</c>, on the line's
    /// <c>date</c>), <c>createdAtFrom</c> (not with <c>dateFrom</c>), and <c>after</c> cursors handed out in
    /// <c>next</c> as a ready query string that, like Pluggy's example, names only the account and the cursor.
    /// A cursor is only accepted with the account and window it was issued for; unknown parameters are rejected.
    /// </summary>
    private HttpResponseMessage Transactions(RecordedRequest request)
    {
        if (request.QueryNames().FirstOrDefault(n => !TransactionQueryNames.Contains(n, StringComparer.Ordinal)) is { } unknown)
            return Error(HttpStatusCode.BadRequest, $"Unknown query parameter '{unknown}'");
        if (request.Query("dateFrom") is not null && request.Query("createdAtFrom") is not null)
            return Error(HttpStatusCode.BadRequest, "dateFrom cannot be combined with createdAtFrom");
        if (!TryDate(request.Query("dateFrom"), out var dateFrom) || !TryDate(request.Query("dateTo"), out var dateTo))
            return Error(HttpStatusCode.BadRequest, "dateFrom and dateTo must be yyyy-mm-dd");

        var accountId = request.Query("accountId");
        var fixture = accountId switch
        {
            BankAccountId => "transactions-bank.json",
            CardAccountId => "transactions-card.json",
            _ => null,
        };
        if (fixture is null)
            return Error(HttpStatusCode.NotFound, "Account not found");

        var window = $"accountId={accountId}&dateFrom={request.Query("dateFrom")}&dateTo={request.Query("dateTo")}&createdAtFrom={request.Query("createdAtFrom")}";
        var offset = 0;
        if (request.Query("after") is { } after)
        {
            if (!_cursors.TryGetValue(after, out var cursor) || cursor.Query != window)
                return Error(HttpStatusCode.BadRequest, "The after cursor does not belong to this query");
            offset = cursor.Offset;
        }

        var lines = JsonNode.Parse(Fixtures.Text(fixture))!["results"]!.AsArray()
            .Where(line =>
            {
                var date = DateOnly.FromDateTime(DateTimeOffset.Parse(line!["date"]!.GetValue<string>(), CultureInfo.InvariantCulture).UtcDateTime);
                return (dateFrom is null || date >= dateFrom) && (dateTo is null || date <= dateTo);
            })
            .ToList();
        var end = offset + TransactionPageSize;
        string? next = null;
        if (end < lines.Count)
        {
            // Base64 with '+', '/' and '=' ("+++/AA==", "+++/AQ==", ...) so the client's escaping is exercised.
            var token = Convert.ToBase64String([0xFB, 0xEF, 0xBF, (byte)_cursors.Count]);
            _cursors[token] = (window, end);
            next = $"?accountId={Uri.EscapeDataString(accountId!)}&after={Uri.EscapeDataString(token)}";
        }

        var page = new JsonObject
        {
            ["results"] = new JsonArray([.. lines.Skip(offset).Take(TransactionPageSize).Select(l => l!.DeepClone())]),
            ["next"] = next,
        };
        return Json(page.ToJsonString());
    }

    /// <summary><c>GET /investments?itemId=</c> as documented: optional <c>type</c>, page-number paging (<c>page</c> from 1).</summary>
    private HttpResponseMessage Investments(RecordedRequest request)
    {
        if (request.QueryNames().FirstOrDefault(n => !InvestmentQueryNames.Contains(n, StringComparer.Ordinal)) is { } unknown)
            return Error(HttpStatusCode.BadRequest, $"Unknown query parameter '{unknown}'");
        if (request.Query("itemId") != ItemId)
            return Error(HttpStatusCode.BadRequest, "itemId is required and must be an item of this application");

        var results = JsonNode.Parse(Fixtures.Text("investments.json"))!["results"]!.AsArray()
            .Where(i => request.Query("type") is not { } type || i!["type"]!.GetValue<string>() == type)
            .ToList();
        return NumberedPage(request, results);
    }

    /// <summary>
    /// <c>GET /investments/{id}/transactions</c>, page-number paging. <c>investment-transactions.json</c> holds each
    /// position's movements by id; the server wraps them in Pluggy's envelope (<c>total</c>, <c>totalPages</c>, <c>page</c>, <c>results</c>).
    /// </summary>
    private HttpResponseMessage InvestmentTransactions(RecordedRequest request, string investmentId)
    {
        if (request.QueryNames().FirstOrDefault(n => !InvestmentTransactionQueryNames.Contains(n, StringComparer.Ordinal)) is { } unknown)
            return Error(HttpStatusCode.BadRequest, $"Unknown query parameter '{unknown}'");
        var results = JsonNode.Parse(Fixtures.Text("investment-transactions.json"))![investmentId]?.AsArray().ToList() ?? [];
        return NumberedPage(request, results);
    }

    private HttpResponseMessage NumberedPage(RecordedRequest request, IReadOnlyList<JsonNode?> results)
    {
        var page = 1;
        var pageSize = InvestmentPageSize;
        if (request.Query("page") is { } pageText && (!int.TryParse(pageText, CultureInfo.InvariantCulture, out page) || page < 1))
            return Error(HttpStatusCode.BadRequest, "page must be a positive number");
        if (request.Query("pageSize") is { } sizeText && (!int.TryParse(sizeText, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > 500))
            return Error(HttpStatusCode.BadRequest, "pageSize must be between 1 and 500");

        var envelope = new JsonObject
        {
            ["total"] = results.Count,
            ["totalPages"] = (results.Count + pageSize - 1) / pageSize,
            ["page"] = page,
            ["results"] = new JsonArray([.. results.Skip((page - 1) * pageSize).Take(pageSize).Select(r => r!.DeepClone())]),
        };
        return Json(envelope.ToJsonString());
    }

    private static bool TryDate(string? value, out DateOnly? date)
    {
        date = null;
        if (value is null)
            return true;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        date = parsed;
        return true;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        Json(new JsonObject { ["message"] = message, ["code"] = (int)status }.ToJsonString(), status);

    public FakePluggyServer On(string path, Func<RecordedRequest, int, HttpResponseMessage> responder)
    {
        _routes[path] = responder;
        return this;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent(string.Empty) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        _requests.Enqueue(recorded);
        if (!_routes.TryGetValue(recorded.Path, out var responder))
            return Status(HttpStatusCode.NotFound);
        return responder(recorded, _calls.AddOrUpdate(recorded.Path, 0, (_, n) => n + 1));
    }
}

internal static class Fixtures
{
    public static string Text(string name) =>
        SyntheticTaxIds.Fill(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
}

/// <summary>Builds the plugin the way a host does, with the HTTP transport replaced by <see cref="FakePluggyServer"/>.</summary>
internal sealed class PluggyTestHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;

    /// <summary>"Now" in the tests unless one passes its own clock: the day after the fixtures' last refresh.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public PluggyTestHost(FakePluggyServer server, TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider ?? new FakeTimeProvider(Now));
        services.AddPluggyIntegration();
        services.AddHttpClient<Api.PluggyApiClient>().ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(server));
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateScope();
        Plugin = _scope.ServiceProvider.GetServices<IIntegrationPlugin>().OfType<PluggyIntegrationPlugin>().Single();
    }

    public PluggyIntegrationPlugin Plugin { get; }

    public static IntegrationSyncRequest Request(long connectionId = 7, string? itemId = FakePluggyServer.ItemId, DateTimeOffset? windowStart = null) =>
        new(connectionId, PluggyIntegrationPlugin.Id, windowStart, null,
            ApiKey: "synthetic-client-id", ApiSecret: "synthetic-client-secret", Passphrase: itemId);

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
    }

    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
