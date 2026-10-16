# CrxMoney.Integrations.Abstractions

The plugin contract for CRX•Money Platform integrations. A provider (a bank API,
an exchange, a file format) implements these interfaces; the CRX•Money platform
discovers the plugin, prepares each sync, and imports what the plugin returns.

The package has no dependencies beyond .NET 10 and carries no provider code,
database types, secret-store clients or tenant internals. Anything
platform-specific stays on the host side of the interfaces below.

```
dotnet add package CrxMoney.Integrations.Abstractions --prerelease
```

## The contract

| Type | Namespace | Role |
| --- | --- | --- |
| `IIntegrationPlugin` | `.Plugins` | What every provider implements: `ProviderId`, `DisplayName`, `Capabilities`, `Defaults`, `SyncAsync`. |
| `IntegrationCapability` | `.Plugins` | Flags: `Balances`, `Trades`, `Transfers`, `Prices`. |
| `IntegrationPluginDefaults` | `.Plugins` | Suggested sync cadence (minutes) and backfill anchor date. |
| `IIntegrationImportSchemaManifestProvider`, `IntegrationImportSchemaManifest`, `IntegrationImportSchemaManifestItem` | `.Plugins` | Optional: declare the JSON schemas used in `ProviderData`. |
| `IntegrationSyncRequest` | `.Sync` | Input to one run: connection id, window, previous checkpoints, resolved credentials. |
| `IntegrationSyncResult` | `.Sync` | Output of one run; build it with `Success(...)` or `Failure(...)`. |
| `IntegrationImportEvent` | `.Sync` | The canonical, idempotent movement every provider folds into. |
| `IntegrationCheckpoint` | `.Sync` | A resume cursor per resource, persisted by the host. |
| `IntegrationSyncStatus`, `IntegrationErrorKind` | `.Sync` | Run outcome and failure classification. |
| `IIntegrationSecretResolver` | `.Secrets` | Implemented by the host; plugins receive resolved credentials, never locators. |
| `BalanceSnapshot`, `TradeFill`, `TransferRecord`, `TransferKind` | `.Models` | Normalised DTOs for provider state. |

## Writing a plugin

```csharp
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;

public sealed class ExampleBankPlugin : IIntegrationPlugin
{
    public string ProviderId => "example-bank";
    public string DisplayName => "Example Bank";
    public IntegrationCapability Capabilities => IntegrationCapability.Balances | IntegrationCapability.Transfers;

    public async Task<IntegrationSyncResult> SyncAsync(
        IntegrationSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        // Read request.PreviousCheckpoints, call the provider, map movements to IntegrationImportEvent.
        await Task.CompletedTask;
        return IntegrationSyncResult.Success(startedAt, DateTimeOffset.UtcNow,
            balancesImported: 0, tradesImported: 0, transfersImported: 0);
    }
}
```

Rules every plugin follows:

- **Idempotency.** Derive `IntegrationImportEvent.IdempotencyKey` deterministically
  (provider + connection + external id + side). Re-running a window must produce
  the same keys; the host imports each key at most once.
- **Checkpoints.** Return the new cursors in `IntegrationSyncResult.Checkpoints`;
  the host passes them back as `PreviousCheckpoints` on the next run.
- **Failures.** Return `IntegrationSyncResult.Failure(...)` with an
  `IntegrationErrorKind` for expected failures (bad credentials, throttling,
  upstream errors) instead of throwing.
- **Secrets.** Credentials arrive already resolved in the request. Never log
  them, and never put them or personal data in `ErrorMessage` or
  `ProviderDiagnosticsJson`.
- **Provider data.** Structured detail goes in `ProviderData` as JSON, with its
  schema declared through `IIntegrationImportSchemaManifestProvider`.

## Versioning

SemVer, from git tags. Within a major version the contract only grows; see
[`docs/versioning.md`](https://github.com/CRX-Money/platform-integrations/blob/main/docs/versioning.md)
for the breaking-change policy.

## Licence

Apache-2.0. "CRX•Money" is the name of the CRX•Money platform; the licence does
not grant rights to the name or logos.
