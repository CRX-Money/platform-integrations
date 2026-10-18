# CrxMoney.Integrations.Countries.Br

Shared, provider-independent code for Brazil, used by the providers under
[`src/Providers/br`](../../Providers/br/). Changes here run the tests of every
Brazilian provider in CI.

```
src/Countries/br/CrxMoney.Integrations.Countries.Br.csproj
tests/Countries/br/CrxMoney.Integrations.Countries.Br.Tests/
dotnet add package CrxMoney.Integrations.Countries.Br --prerelease
```

| Type | What it does |
| --- | --- |
| `BrazilianTaxId` | Parses a formatted or bare CPF/CNPJ (including the 2026 alphanumeric CNPJ) and accepts it only with valid check digits, so masked values never become identifiers. |
| `BrazilianBankAccount` | A deposit account as the banks identify it: COMPE + branch + account with check digit. `ExternalAccountId` is `br:{compe}:{branch}:{number}`; cards are `br:{compe}:card:{last4}`. |
| `BrazilBankingCalendar` | São Paulo booking dates; imports are dated at midnight of the booking date (`2026-03-10T00:00-03:00`). |
| `OpenFinance.*` | The aggregator-neutral Open Finance model and the plugin every aggregator shares (below). |

Pix key types and ISPB formatting will be added here when a provider needs them.

## Open Finance: one model, many aggregators

Brazilian Open Finance data reaches CRX•Money through aggregators (Pluggy first).
To keep the aggregator replaceable, everything that matters to the platform is
decided here, once:

```
aggregator API ──► IOpenFinanceSource (adapter, per aggregator)
                       │  normalises into
                       ▼
               OpenFinanceAccount / OpenFinanceTransaction
                       │  OpenFinanceIntegrationPlugin (shared)
                       ▼
               IntegrationImportEvent (keys, dates, provider data)
```

- **Account identity** is the bank's own (`br:{compe}:{branch}:{number}`), never
  the aggregator's account id. Imports of the same account through another
  aggregator, Inter's own API or an OFX file land on the same account map.
- **Idempotency** is built only from what the bank reports:
  `brof:` + SHA-256(account, instant to the second, credit/debit, amount,
  counterparty CPF/CNPJ, ordinal among identical lines). The aggregator's id
  becomes the import's `ExternalId` and is kept in provider data, but never in the
  key. Switching aggregators on an existing connection therefore imports nothing
  twice. The recipe is part of the package's released behaviour (`docs/versioning.md`).
- **Dates**: booking date in Brazil's calendar at midnight São Paulo.
- **Provider data** (schemas `br.openfinance.transaction.deposit`/`.withdrawal` v1):
  `provider` (the aggregator), `channel: "openfinance"`, type and detail,
  description, `counterparty` (name, `taxId` + `taxIdKind` + `taxIdCountry`,
  ISPB, branch, account), `pix` when present, `card` (last four, bill), a
  `transferHint` (`investment` for automatic CDB sweeps and similar,
  `card_bill_payment`), and the aggregator's raw line as `sourcePayload`.
- **Sync behaviour**: pending lines are never imported; sources read everything they
  know and the plugin sends lines from seven days before the host's window start;
  the checkpoint `transactions` holds the latest booking date; diagnostics include a
  per-account check that credits minus debits equal the reported balance.

### Adding an aggregator

1. Implement `IOpenFinanceSource` in `src/Providers/br/<Aggregator>`: authenticate,
   check the consent in `CheckConnectionAsync`, and in `ReadAsync` return every
   account (with its bank identity) and its lines normalised into
   `OpenFinanceTransaction` (direction from the type, positive amount, the bank's
   transaction instant, counterparty CPF/CNPJ). Throw `OpenFinanceSourceException`
   with an `IntegrationErrorKind` for expected failures.
2. Subclass `OpenFinanceIntegrationPlugin` with the provider id and register it.
3. Test the normalisation against synthetic fixtures in the aggregator's shape, and
   check that the keys for the shared fixture lines match the existing aggregator's.

Nothing else changes: mapping, keys, schemas, review rules and participant matching
are shared.
