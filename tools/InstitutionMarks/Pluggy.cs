using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>A Pluggy connector as <c>GET /connectors</c> returns it (only the fields the tool uses).</summary>
internal sealed record PluggyConnector(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("institutionUrl")] string? InstitutionUrl,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("isSandbox")] bool IsSandbox);

/// <summary>The paged envelope of <c>GET /connectors</c>.</summary>
internal sealed record PluggyConnectorPage(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("results")] IReadOnlyList<PluggyConnector>? Results);

/// <summary>Reads the connector list from Pluggy with a short-lived API key obtained from the client id and secret.</summary>
internal sealed class PluggyClient(HttpClient http)
{
    private const string Base = "https://api.pluggy.ai/";

    private sealed record AuthRequest(
        [property: JsonPropertyName("clientId")] string ClientId,
        [property: JsonPropertyName("clientSecret")] string ClientSecret);

    private sealed record AuthResponse([property: JsonPropertyName("apiKey")] string? ApiKey);

    /// <summary>Every Brazilian connector, sandbox ones excluded.</summary>
    public async Task<IReadOnlyList<PluggyConnector>> ListBrazilianConnectorsAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        using var auth = await http.PostAsJsonAsync(new Uri(Base + "auth"), new AuthRequest(clientId, clientSecret), ct);
        // Never echo the response body: it is the credential exchange.
        if (!auth.IsSuccessStatusCode)
            throw new InvalidOperationException($"Pluggy authentication failed with HTTP {(int)auth.StatusCode}.");
        var apiKey = (await auth.Content.ReadFromJsonAsync<AuthResponse>(ct))?.ApiKey
            ?? throw new InvalidOperationException("Pluggy authentication returned no apiKey.");

        var all = new List<PluggyConnector>();
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Base}connectors?countries=BR&sandbox=false&page={page}&pageSize=500"));
            request.Headers.Add("X-API-KEY", apiKey);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<PluggyConnectorPage>(ct)
                ?? throw new InvalidOperationException("Pluggy returned an empty connector page.");
            all.AddRange(body.Results ?? []);
            if (page >= body.TotalPages || body.Results is not { Count: > 0 })
                break;
        }

        return all.Where(c => !c.IsSandbox && (c.Country is null || c.Country.Equals("BR", StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
