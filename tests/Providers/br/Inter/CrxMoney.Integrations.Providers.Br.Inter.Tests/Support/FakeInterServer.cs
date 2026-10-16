using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;

/// <summary>A recorded HTTP exchange with the fake Inter API.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string Path => Uri.AbsolutePath;

    public string? Query(string name) => HttpUtility.ParseQueryString(Uri.Query)[name];

    public string? Form(string name) => Body is null ? null : HttpUtility.ParseQueryString(Body)[name];
}

/// <summary>
/// An in-memory Inter API: routes by path to a responder, records every request that reaches the wire.
/// No test talks to a real Inter host.
/// </summary>
internal sealed class FakeInterServer : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly Dictionary<string, Func<RecordedRequest, int, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    public IReadOnlyList<RecordedRequest> RequestsTo(string path) => [.. _requests.Where(r => r.Path == path)];

    /// <summary>Answers <paramref name="path"/>; the responder gets the request and its 0-based call number for that path.</summary>
    public FakeInterServer On(string path, Func<RecordedRequest, int, HttpResponseMessage> responder)
    {
        _routes[path] = responder;
        return this;
    }

    public FakeInterServer OnToken(int expiresIn = 3600) => On("/oauth/v2/token", (_, call) =>
        Json($$"""{"access_token":"synthetic-token-{{call}}","token_type":"Bearer","expires_in":{{expiresIn}},"scope":"extrato.read"}"""));

    public FakeInterServer OnStatementFixturePages() => On("/banking/v2/extrato/completo", (request, _) =>
        Json(Fixtures.Text($"extrato-completo-page-{request.Query("pagina")}.json")));

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

        var call = _calls.AddOrUpdate(recorded.Path, 0, (_, n) => n + 1);
        return responder(recorded, call);
    }
}
