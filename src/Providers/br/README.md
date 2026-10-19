# Providers: Brazil (`br`)

Plugins for banks, payment institutions and brokers licensed by the Banco
Central do Brasil or the CVM. One folder per provider:

```
src/Providers/br/<Provider>/CrxMoney.Integrations.Providers.Br.<Provider>.csproj
tests/Providers/br/<Provider>/CrxMoney.Integrations.Providers.Br.<Provider>.Tests/
```

| Provider | Package | What it imports |
| --- | --- | --- |
| [Inter](Inter/README.md) | `CrxMoney.Integrations.Providers.Br.Inter` | Inter Empresas Banking API: enriched statement with counterparty CPF/CNPJ, Pix end-to-end ids and transaction types |
| [Pluggy](Pluggy/README.md) | `CrxMoney.Integrations.Providers.Br.Pluggy` | Open Finance through the Pluggy aggregator: every connected bank account, card and investment position, through the aggregator-neutral model in [`src/Countries/br`](../../Countries/br/README.md) |

Brazilian identifiers (CPF, CNPJ, Pix keys, agency and account numbers) appear in
almost every response here. Fixtures must use the synthetic forms in
[`docs/fixture-policy.md`](../../../docs/fixture-policy.md); shared parsing and
validation for them belongs in [`src/Countries/br`](../../Countries/br/).
Banks without an API are served by file formats under
[`src/Formats`](../../Formats/).
