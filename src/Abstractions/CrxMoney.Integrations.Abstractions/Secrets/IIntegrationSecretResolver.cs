namespace CrxMoney.Integrations.Abstractions.Secrets;

/// <summary>
/// Resolves a credential locator into its secret value. Implemented by the host, not by plugins.
/// </summary>
/// <remarks>
/// Plugins never read secret stores themselves: the host resolves the connection's credentials
/// before the call and passes them in <see cref="Sync.IntegrationSyncRequest"/>. The locator format
/// is host-defined and opaque to plugins.
/// </remarks>
public interface IIntegrationSecretResolver
{
    /// <summary>Resolves <paramref name="locator"/> without tenant context.</summary>
    /// <param name="locator">Host-defined credential locator.</param>
    /// <returns>The secret value, or <see langword="null"/> when the locator cannot be resolved.</returns>
    string? Resolve(string locator);

    /// <summary>Resolves <paramref name="locator"/> within the tenant identified by <paramref name="tenantUuid"/>.</summary>
    /// <param name="locator">Host-defined credential locator.</param>
    /// <param name="tenantUuid">Tenant that owns the connection.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The secret value, or <see langword="null"/> when the locator cannot be resolved.</returns>
    Task<string?> ResolveAsync(string locator, Guid tenantUuid, CancellationToken cancellationToken = default)
        => Task.FromResult(Resolve(locator));
}
