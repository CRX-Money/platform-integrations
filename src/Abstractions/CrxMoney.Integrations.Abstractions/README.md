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
| `IIntegrationConnectionSettingsProvider`, `IntegrationConnectionSettingsManifest`, `IntegrationConnectionAdapterSettings`, `IntegrationConnectionSettingsField`, `IntegrationCredentialSlot`, `IntegrationConnectionWalkthrough` | `.Plugins` | Optional: declare each adapter's connection settings as a JSON Schema, with walkthrough text. |
| `IIntegrationConnectionTester`, `IntegrationConnectionTestResult`, `IntegrationAccountOperator` | `.Plugins` | Optional: check settings before they are saved and describe the result in provider-neutral terms. |
| `IntegrationSyncRequest` | `.Sync` | Input to one run: connection id, window, previous checkpoints, resolved credentials, and the optional `AdapterId`. |
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

## Connection settings

A plugin may describe the form its connections need instead of leaving it to the
host. `IIntegrationConnectionSettingsProvider.GetConnectionSettings(culture)`
returns one `IntegrationConnectionAdapterSettings` per adapter (the code for one
aggregator or channel inside the plugin):

- `AdapterId`: stable, unique in the plugin and compared exactly; by
  convention the aggregator's name (`Pluggy`). The host stores it with the
  connection and passes it back on every run in `IntegrationSyncRequest.AdapterId`
  (optional and init-only; `null` lets a plugin with one adapter use that one).
- `JsonSchema`: a JSON Schema (draft 2020-12) for the settings object. Each
  property has `type`, `title`, `description`, and `format`/`pattern` where useful;
  secrets carry `writeOnly: true`; `required` lists the mandatory fields. No
  custom keywords, so any JSON Schema form generator can render it.
- `Fields`: the same properties as the host stores them. `Slot` says where the
  value travels in `IntegrationSyncRequest` (`ApiKey`, `ApiSecret`, `Passphrase`).
  `IsSecret` means the value is never shown again once saved. `IsRequired`
  matches `required`.
- `Walkthrough`: optional steps (with links) and a closing note.

Texts follow the requested BCP 47 culture when the plugin has it;
`IntegrationConnectionSettingsManifest.Culture` says which culture was used.

`IIntegrationConnectionTester.TestConnectionAsync(adapterId, request, culture)`
checks the settings without importing anything. It returns
`IntegrationConnectionTestResult`:

- `Succeeded`, plus a `Summary` with no secrets and no aggregator branding;
- `ErrorKind` on failure;
- `CredentialExpiresAtUtc`, `LastRefreshedAtUtc` and `IsSandbox`;
- `AccountOperators` (name and official website), which the host can match to the
  account operators it knows.

Both interfaces are optional. A plugin without them keeps working, and the host
falls back to its own form.

Example: the settings schema of the Open Finance Brasil plugin's Pluggy adapter
(`en`). Its fields are `clientId` (slot `ApiKey`), `clientSecret` (slot `ApiSecret`,
secret) and `itemId` (slot `Passphrase`):

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "Pluggy",
  "description": "A Pluggy developer application and the item of one bank connected through Meu Pluggy.",
  "type": "object",
  "additionalProperties": false,
  "required": ["clientId", "clientSecret", "itemId"],
  "properties": {
    "clientId": {
      "type": "string",
      "format": "uuid",
      "title": "Client ID",
      "description": "The Client ID of your application in the Pluggy Dashboard (Applications)."
    },
    "clientSecret": {
      "type": "string",
      "minLength": 1,
      "writeOnly": true,
      "title": "Client Secret",
      "description": "The application's Client Secret. Pluggy shows it only once; generate a new one if you did not keep it."
    },
    "itemId": {
      "type": "string",
      "format": "uuid",
      "title": "Item ID",
      "description": "The id of the bank's item under Connected Items, on the application's Demo page."
    }
  }
}
```

## Versioning

SemVer, from git tags. Within a major version the contract only grows; see
[`docs/versioning.md`](https://github.com/CRX-Money/platform-integrations/blob/main/docs/versioning.md)
for the breaking-change policy.

## Licence

Apache-2.0. "CRX•Money" is the name of the CRX•Money platform; the licence does
not grant rights to the name or logos.
