namespace CrxMoney.Integrations.Abstractions.Plugins;

/// <summary>
/// One versioned JSON schema a plugin may use for <see cref="Sync.IntegrationImportEvent.ProviderData"/>.
/// </summary>
/// <param name="SchemaId">Stable schema identifier, unique within the provider.</param>
/// <param name="Version">Schema version; increase it for any incompatible change to the JSON shape.</param>
/// <param name="Name">Human-readable schema name.</param>
/// <param name="Description">Optional description of what the schema carries.</param>
/// <param name="JsonSchema">Optional JSON Schema document the host uses for validation.</param>
/// <param name="SchemaHash">Optional hash of <paramref name="JsonSchema"/>, used to detect drift.</param>
/// <param name="IsActive">Whether new events may still reference this schema version.</param>
public sealed record IntegrationImportSchemaManifestItem(
    string SchemaId,
    short Version,
    string Name,
    string? Description = null,
    string? JsonSchema = null,
    string? SchemaHash = null,
    bool IsActive = true);

/// <summary>
/// The set of provider-data schemas a plugin declares through
/// <see cref="IIntegrationImportSchemaManifestProvider"/>.
/// </summary>
/// <param name="Schemas">Declared schemas.</param>
public sealed record IntegrationImportSchemaManifest(
    IReadOnlyList<IntegrationImportSchemaManifestItem> Schemas)
{
    /// <summary>A manifest that declares no schemas.</summary>
    public static readonly IntegrationImportSchemaManifest Empty = new([]);
}
