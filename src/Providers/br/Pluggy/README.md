# CrxMoney.Integrations.Providers.Br.Pluggy (replaced)

This package ends at **1.0.0-preview.4**. Pluggy is an aggregator, not a
provider: it is now the Pluggy adapter of the Open Finance Brasil plugin,
[`CrxMoney.Integrations.Providers.Br.OpenFinance`](../OpenFinance/README.md),
from 1.0.0-preview.5 on.

```
dotnet remove package CrxMoney.Integrations.Providers.Br.Pluggy
dotnet add package CrxMoney.Integrations.Providers.Br.OpenFinance --prerelease
```

```csharp
// before: services.AddPluggyIntegration();
services.AddOpenFinanceBrasil().AddPluggy();
```

Idempotency keys, external account ids, provider data, schema ids and the
provider id `Pluggy` are unchanged, so nothing is imported twice. The full list
of renamed namespaces is in the
[new package's README](../OpenFinance/README.md#moving-from-crxmoneyintegrationsprovidersbrpluggy).
