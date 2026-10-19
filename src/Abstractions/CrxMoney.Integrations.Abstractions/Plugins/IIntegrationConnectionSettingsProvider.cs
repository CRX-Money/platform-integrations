namespace CrxMoney.Integrations.Abstractions.Plugins;

/// <summary>
/// Optional companion to <see cref="IIntegrationPlugin"/>: the settings a connection needs, declared per
/// adapter (the code for one aggregator or channel inside the plugin) as a JSON Schema the host renders as a
/// form. Plugins without it keep working; the host then uses its own form for them.
/// </summary>
/// <remarks>
/// The host stores the values the owner enters and hands them back on every run in the
/// <see cref="Sync.IntegrationSyncRequest"/> credential slot each field declares
/// (<see cref="IntegrationConnectionSettingsField.Slot"/>). A field marked secret is never shown again after it
/// is saved. Texts are in the culture asked for when the plugin has it, otherwise in its default culture;
/// <see cref="IntegrationConnectionSettingsManifest.Culture"/> says which one was used.
/// </remarks>
public interface IIntegrationConnectionSettingsProvider
{
    /// <summary>Returns the settings of every adapter the plugin offers.</summary>
    /// <param name="culture">A BCP 47 culture name for titles, descriptions and walkthroughs (<c>pt-BR</c>); <c>null</c> for the plugin's default.</param>
    /// <returns>The plugin's settings manifest.</returns>
    IntegrationConnectionSettingsManifest GetConnectionSettings(string? culture = null);
}

/// <summary>
/// Optional companion to <see cref="IIntegrationPlugin"/>: checks a connection's settings before the host saves
/// them, without importing anything, and describes the result in provider-neutral terms.
/// </summary>
public interface IIntegrationConnectionTester
{
    /// <summary>Checks the settings in <paramref name="request"/> for the adapter <paramref name="adapterId"/>.</summary>
    /// <param name="adapterId">One of the plugin's <see cref="IntegrationConnectionAdapterSettings.AdapterId"/> values.</param>
    /// <param name="request">The settings in their credential slots, as a sync would receive them (<c>ConnectionId</c> is 0 for an unsaved connection).</param>
    /// <param name="culture">A BCP 47 culture name for <see cref="IntegrationConnectionTestResult.Summary"/>; <c>null</c> for the plugin's default.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>
    /// The outcome. Return <see cref="IntegrationConnectionTestResult.Failure"/> for expected failures (rejected
    /// credentials, an unknown adapter, an expired consent) rather than throwing.
    /// </returns>
    Task<IntegrationConnectionTestResult> TestConnectionAsync(
        string adapterId,
        Sync.IntegrationSyncRequest request,
        string? culture = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Where the host puts a setting's value in <see cref="Sync.IntegrationSyncRequest"/>.</summary>
public enum IntegrationCredentialSlot
{
    /// <summary>Not carried in a credential slot.</summary>
    None = 0,

    /// <summary><see cref="Sync.IntegrationSyncRequest.ApiKey"/>.</summary>
    ApiKey = 1,

    /// <summary><see cref="Sync.IntegrationSyncRequest.ApiSecret"/>.</summary>
    ApiSecret = 2,

    /// <summary><see cref="Sync.IntegrationSyncRequest.Passphrase"/>.</summary>
    Passphrase = 3
}

/// <summary>The connection settings a plugin declares through <see cref="IIntegrationConnectionSettingsProvider"/>.</summary>
/// <param name="ProviderId">The plugin's <see cref="IIntegrationPlugin.ProviderId"/>.</param>
/// <param name="Culture">The culture the texts are in (<c>en</c>, <c>pt-BR</c>).</param>
/// <param name="Adapters">One entry per adapter, in the order the host should offer them.</param>
public sealed record IntegrationConnectionSettingsManifest(
    string ProviderId,
    string Culture,
    IReadOnlyList<IntegrationConnectionAdapterSettings> Adapters);

/// <summary>The settings of one adapter.</summary>
/// <param name="AdapterId">Stable lower-case identifier, unique within the plugin (<c>pluggy</c>); the host stores it with the connection.</param>
/// <param name="DisplayName">Name shown to users (<c>Pluggy</c>).</param>
/// <param name="SchemaVersion">Version of <paramref name="JsonSchema"/>; increase it for any incompatible change.</param>
/// <param name="JsonSchema">
/// A JSON Schema (draft 2020-12) document for the settings object: one property per field with <c>type</c>,
/// <c>title</c>, <c>description</c> and, where it applies, <c>format</c>, <c>pattern</c> and
/// <c>writeOnly: true</c> for secrets; <c>required</c> lists the mandatory fields.
/// </param>
/// <param name="Fields">The schema's properties as the host stores them: credential slot, secrecy, whether required.</param>
public sealed record IntegrationConnectionAdapterSettings(
    string AdapterId,
    string DisplayName,
    short SchemaVersion,
    string JsonSchema,
    IReadOnlyList<IntegrationConnectionSettingsField> Fields)
{
    /// <summary>Steps that tell the owner where to find each value, when the plugin offers them.</summary>
    public IntegrationConnectionWalkthrough? Walkthrough { get; init; }
}

/// <summary>One settings field.</summary>
/// <param name="Name">The property name in <see cref="IntegrationConnectionAdapterSettings.JsonSchema"/> (<c>clientId</c>).</param>
/// <param name="Slot">Where the host passes the value to the plugin.</param>
/// <param name="IsSecret">Never shown again after it is saved (the schema marks it <c>writeOnly</c>).</param>
/// <param name="IsRequired">Listed in the schema's <c>required</c>.</param>
public sealed record IntegrationConnectionSettingsField(string Name, IntegrationCredentialSlot Slot, bool IsSecret, bool IsRequired);

/// <summary>A short guide shown beside the settings form.</summary>
/// <param name="Title">Heading of the guide.</param>
/// <param name="Steps">The steps, in order.</param>
public sealed record IntegrationConnectionWalkthrough(string Title, IReadOnlyList<IntegrationConnectionWalkthroughStep> Steps)
{
    /// <summary>A closing remark, for example the terms of the plan the steps assume.</summary>
    public string? Note { get; init; }
}

/// <summary>One step of a walkthrough.</summary>
/// <param name="Text">What to do.</param>
public sealed record IntegrationConnectionWalkthroughStep(string Text)
{
    /// <summary>A page the step happens on, when there is one.</summary>
    public Uri? Link { get; init; }
}

/// <summary>An institution holding the connection's accounts, as the plugin identifies it.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Website">Official website (<c>https://host/</c>), when known: the host can match its host to an account operator it knows.</param>
public sealed record IntegrationAccountOperator(string Name, Uri? Website);

/// <summary>The outcome of <see cref="IIntegrationConnectionTester.TestConnectionAsync"/>, in provider-neutral terms.</summary>
/// <param name="Succeeded">The settings work.</param>
/// <param name="Summary">One or two user-facing sentences. No secrets, no personal data.</param>
public sealed record IntegrationConnectionTestResult(bool Succeeded, string Summary)
{
    /// <summary>Why the check failed; <see cref="Sync.IntegrationErrorKind.None"/> on success.</summary>
    public Sync.IntegrationErrorKind ErrorKind { get; init; }

    /// <summary>When the credential or consent expires, if known.</summary>
    public DateTimeOffset? CredentialExpiresAtUtc { get; init; }

    /// <summary>When the provider last refreshed the data from the institution, if known.</summary>
    public DateTimeOffset? LastRefreshedAtUtc { get; init; }

    /// <summary>Whether the settings reach a sandbox rather than real accounts, if known.</summary>
    public bool? IsSandbox { get; init; }

    /// <summary>The institutions holding the connection's accounts, when the check found them.</summary>
    public IReadOnlyList<IntegrationAccountOperator> AccountOperators { get; init; } = [];

    /// <summary>A successful check.</summary>
    public static IntegrationConnectionTestResult Success(string summary) => new(true, summary);

    /// <summary>A failed check; <paramref name="message"/> is shown to the user and must hold no secrets.</summary>
    public static IntegrationConnectionTestResult Failure(Sync.IntegrationErrorKind kind, string message) =>
        new(false, message) { ErrorKind = kind };
}
