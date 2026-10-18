using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace CrxMoney.Integrations.Providers.Br.Pluggy.Tests.Support;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string Path => Uri.AbsolutePath;

    public string? Query(string name) => HttpUtility.ParseQueryString(Uri.Query)[name];
}

/// <summary>An in-memory Pluggy API serving the synthetic fixtures. No test calls api.pluggy.ai.</summary>
internal sealed class FakePluggyServer : HttpMessageHandler
{
    public const string ItemId = "00000000-0000-4000-8000-0000000000a1";
    public const string BankAccountId = "00000000-0000-4000-8000-0000000000b1";
    public const string CardAccountId = "00000000-0000-4000-8000-0000000000b2";

    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly Dictionary<string, Func<RecordedRequest, int, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    public IReadOnlyList<RecordedRequest> RequestsTo(string path) => [.. _requests.Where(r => r.Path == path)];

    public static FakePluggyServer WithFixtures() => new FakePluggyServer()
        .On("/auth", (_, call) => Json($$"""{"apiKey":"synthetic-api-key-{{call}}"}"""))
        .On($"/items/{ItemId}", (_, _) => Json(Fixtures.Text("item.json")))
        .On("/accounts", (_, _) => Json(Fixtures.Text("accounts.json")))
        .On("/v2/transactions", (request, _) => request.Query("accountId") switch
        {
            BankAccountId when request.Query("cursor") is null => Json(Fixtures.Text("transactions-bank-page-0.json")),
            BankAccountId when request.Query("cursor") == "synthetic-cursor-0001" => Json(Fixtures.Text("transactions-bank-page-1.json")),
            CardAccountId => Json(Fixtures.Text("transactions-card.json")),
            _ => Status(HttpStatusCode.NotFound),
        });

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

    public PluggyTestHost(FakePluggyServer server, TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        if (timeProvider is not null)
            services.AddSingleton(timeProvider);
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
