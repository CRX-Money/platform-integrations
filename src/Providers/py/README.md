# Providers: Paraguay (`py`)

Plugins for banks and financial institutions licensed by the Banco Central del
Paraguay. One folder per provider:

```
src/Providers/py/<Provider>/CrxMoney.Integrations.Providers.Py.<Provider>.csproj
tests/Providers/py/<Provider>/CrxMoney.Integrations.Providers.Py.<Provider>.Tests/
```

No provider yet. Paraguayan identifiers (RUC, cédula, account numbers) are real
personal data too: fixtures follow [`docs/fixture-policy.md`](../../../docs/fixture-policy.md),
and synthetic forms for them will be added to the fixture guard with the first
provider.
