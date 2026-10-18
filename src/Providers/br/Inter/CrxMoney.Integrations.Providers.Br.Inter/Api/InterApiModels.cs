using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrxMoney.Integrations.Providers.Br.Inter.Api;

// Wire models for the Inter Empresas API, named after the portal reference
// (developers.inter.co/references/token and /references/banking). Only the fields the plugin uses
// are typed; the portal leaves the transaction's "detalhes" object untyped, so it stays a JsonElement.

/// <summary>Response of <c>POST /oauth/v2/token</c>.</summary>
internal sealed record InterTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] double? ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope);

/// <summary>One page of <c>GET /banking/v2/extrato/completo</c> in traditional pagination.</summary>
internal sealed record InterStatementPage(
    [property: JsonPropertyName("totalPaginas")] int? TotalPaginas,
    [property: JsonPropertyName("totalElementos")] long? TotalElementos,
    [property: JsonPropertyName("ultimaPagina")] bool? UltimaPagina,
    [property: JsonPropertyName("primeiraPagina")] bool? PrimeiraPagina,
    [property: JsonPropertyName("tamanhoPagina")] int? TamanhoPagina,
    [property: JsonPropertyName("numeroDeElementos")] int? NumeroDeElementos,
    [property: JsonPropertyName("transacoes")] IReadOnlyList<InterTransaction>? Transacoes);

/// <summary>One enriched statement entry.</summary>
internal sealed record InterTransaction(
    [property: JsonPropertyName("idTransacao")] string? IdTransacao,
    [property: JsonPropertyName("dataInclusao")] string? DataInclusao,
    [property: JsonPropertyName("dataTransacao")] string? DataTransacao,
    [property: JsonPropertyName("tipoTransacao")] string? TipoTransacao,
    [property: JsonPropertyName("tipoOperacao")] string? TipoOperacao,
    [property: JsonPropertyName("valor")] JsonElement Valor,
    [property: JsonPropertyName("titulo")] string? Titulo,
    [property: JsonPropertyName("descricao")] string? Descricao,
    [property: JsonPropertyName("numeroDocumento")] JsonElement NumeroDocumento,
    [property: JsonPropertyName("detalhes")] JsonElement Detalhes);

/// <summary>Response of <c>GET /banking/v2/saldo</c>.</summary>
internal sealed record InterBalance(
    [property: JsonPropertyName("disponivel")] decimal? Disponivel,
    [property: JsonPropertyName("bloqueadoCheque")] decimal? BloqueadoCheque,
    [property: JsonPropertyName("bloqueadoJudicialmente")] decimal? BloqueadoJudicialmente,
    [property: JsonPropertyName("bloqueadoAdministrativo")] decimal? BloqueadoAdministrativo,
    [property: JsonPropertyName("limite")] decimal? Limite,
    [property: JsonPropertyName("dataReferencia")] string? DataReferencia);

/// <summary>Inter's error body (<c>title</c>, <c>detail</c> and per-field <c>violacoes</c>).</summary>
internal sealed record InterErrorResponse(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("violacoes")] IReadOnlyList<InterViolation>? Violacoes);

/// <summary>One field violation. <c>valor</c> is deliberately not read: it may echo submitted data.</summary>
internal sealed record InterViolation(
    [property: JsonPropertyName("razao")] string? Razao,
    [property: JsonPropertyName("propriedade")] string? Propriedade);

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(InterTokenResponse))]
[JsonSerializable(typeof(InterStatementPage))]
[JsonSerializable(typeof(InterBalance))]
[JsonSerializable(typeof(InterErrorResponse))]
internal sealed partial class InterApiJsonContext : JsonSerializerContext;
