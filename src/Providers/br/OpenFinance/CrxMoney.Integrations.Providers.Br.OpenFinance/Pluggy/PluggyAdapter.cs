namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;

/// <summary>
/// The Pluggy adapter of the Open Finance Brasil plugin. Settings: <c>clientId</c> (<c>ApiKey</c>),
/// <c>clientSecret</c> (<c>ApiSecret</c>), <c>itemId</c> (<c>Passphrase</c>). All mapping and keys come from
/// <see cref="OpenFinanceIntegrationPlugin"/>.
/// </summary>
public static class PluggyAdapter
{
    /// <summary>
    /// The adapter id: the aggregator's name, equal to the platform's <c>Integration.Aggregator</c> name. It is
    /// also the <c>provider</c> written in provider data, unchanged from the former Pluggy package.
    /// </summary>
    public const string Name = "Pluggy";

    /// <summary>The default Pluggy API address.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://api.pluggy.ai/");
}
