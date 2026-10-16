namespace CrxMoney.Integrations.Abstractions.Plugins;

/// <summary>
/// Optional companion to <see cref="IIntegrationPlugin"/> for plugins that put structured JSON in
/// <see cref="Sync.IntegrationImportEvent.ProviderData"/>.
/// </summary>
/// <remarks>
/// The host persists the manifest, validates incoming events against it, and refuses events that
/// reference an unknown or inactive schema.
/// </remarks>
public interface IIntegrationImportSchemaManifestProvider
{
    /// <summary>Returns every provider-data schema the plugin may emit.</summary>
    /// <returns>The plugin's schema manifest.</returns>
    IntegrationImportSchemaManifest GetImportSchemaManifest();
}
