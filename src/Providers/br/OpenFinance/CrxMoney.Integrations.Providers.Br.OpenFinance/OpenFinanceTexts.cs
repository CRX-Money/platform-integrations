using System.Globalization;
using CrxMoney.Integrations.Abstractions.Plugins;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

/// <summary>
/// User-facing texts of the Open Finance plugin in its two cultures: <c>en</c> (the default) and <c>pt-BR</c>.
/// Any Portuguese culture gets <c>pt-BR</c>; anything else gets <c>en</c>.
/// </summary>
internal abstract class OpenFinanceTexts
{
    public const string English = "en";

    public const string BrazilianPortuguese = "pt-BR";

    private static readonly OpenFinanceTexts EnglishTexts = new EnglishOpenFinanceTexts();
    private static readonly OpenFinanceTexts PortugueseTexts = new PortugueseOpenFinanceTexts();

    /// <summary>The culture the plugin answers <paramref name="culture"/> with.</summary>
    public static string Resolve(string? culture) =>
        culture is not null && (culture.Equals("pt", StringComparison.OrdinalIgnoreCase) || culture.StartsWith("pt-", StringComparison.OrdinalIgnoreCase))
            ? BrazilianPortuguese
            : English;

    public static OpenFinanceTexts For(string? culture) => Resolve(culture) == BrazilianPortuguese ? PortugueseTexts : EnglishTexts;

    public abstract string UnknownAdapter(string adapterId);

    public abstract string Connected(DateTimeOffset? lastRefreshed, DateTimeOffset? consentExpires, IReadOnlyList<IntegrationAccountOperator> operators, bool? isSandbox);

    private sealed class EnglishOpenFinanceTexts : OpenFinanceTexts
    {
        public override string UnknownAdapter(string adapterId) => $"This plugin has no adapter '{adapterId}'.";

        public override string Connected(DateTimeOffset? lastRefreshed, DateTimeOffset? consentExpires, IReadOnlyList<IntegrationAccountOperator> operators, bool? isSandbox) =>
            string.Concat(
                "Connection works.",
                lastRefreshed is { } refreshed ? string.Create(CultureInfo.InvariantCulture, $" Data last refreshed {refreshed.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.") : string.Empty,
                consentExpires is { } expires ? string.Create(CultureInfo.InvariantCulture, $" Consent valid until {expires.ToUniversalTime():yyyy-MM-dd}.") : string.Empty,
                operators.Count > 0 ? $" Accounts at {string.Join(", ", operators.Select(o => o.Name))}." : string.Empty,
                isSandbox == true ? " Sandbox data, not real accounts." : string.Empty);
    }

    private sealed class PortugueseOpenFinanceTexts : OpenFinanceTexts
    {
        public override string UnknownAdapter(string adapterId) => $"Este plugin não tem o adaptador '{adapterId}'.";

        public override string Connected(DateTimeOffset? lastRefreshed, DateTimeOffset? consentExpires, IReadOnlyList<IntegrationAccountOperator> operators, bool? isSandbox) =>
            string.Concat(
                "Conexão funcionando.",
                lastRefreshed is { } refreshed ? string.Create(CultureInfo.InvariantCulture, $" Dados atualizados em {refreshed.ToUniversalTime():dd/MM/yyyy HH:mm} UTC.") : string.Empty,
                consentExpires is { } expires ? string.Create(CultureInfo.InvariantCulture, $" Consentimento válido até {expires.ToUniversalTime():dd/MM/yyyy}.") : string.Empty,
                operators.Count > 0 ? $" Contas em {string.Join(", ", operators.Select(o => o.Name))}." : string.Empty,
                isSandbox == true ? " Dados de sandbox, não de contas reais." : string.Empty);
    }
}
