using CrxMoney.Integrations.Abstractions.Plugins;
using Microsoft.Extensions.Logging;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;

/// <summary>
/// Open Finance Brasil through the Pluggy aggregator: the plugin's Pluggy adapter. All mapping and keys come
/// from <see cref="OpenFinanceIntegrationPlugin"/>; this class names the adapter, declares its connection
/// settings and supplies the Pluggy source. Settings: <c>clientId</c> (<c>ApiKey</c>), <c>clientSecret</c>
/// (<c>ApiSecret</c>), <c>itemId</c> (<c>Passphrase</c>).
/// </summary>
public sealed class PluggyIntegrationPlugin : OpenFinanceIntegrationPlugin
{
    /// <summary>
    /// The provider id the host stores with each connection; equals the platform's <c>Integration.Provider</c>
    /// name. Unchanged from the former <c>CrxMoney.Integrations.Providers.Br.Pluggy</c> package.
    /// </summary>
    public const string Id = "Pluggy";

    /// <summary>The adapter id in the connection settings.</summary>
    public const string AdapterId = "pluggy";

    /// <summary>The default Pluggy API address.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://api.pluggy.ai/");

    internal PluggyIntegrationPlugin(PluggyOpenFinanceSource source, TimeProvider timeProvider, ILogger<PluggyIntegrationPlugin> logger)
        : base(source, timeProvider, logger)
    {
    }

    /// <inheritdoc />
    public override string ProviderId => Id;

    /// <inheritdoc />
    public override string DisplayName => "Pluggy (Open Finance)";

    /// <inheritdoc />
    public override IntegrationConnectionSettingsManifest GetConnectionSettings(string? culture = null) =>
        PluggyConnectionSettings.Manifest(culture);
}
