# CrxMoney.Integrations.Providers.Br.Pluggy

Brazilian Open Finance through [Pluggy](https://pluggy.ai): reads the bank
accounts and credit cards connected to a Pluggy item and hands them to the
aggregator-neutral Open Finance model in
[`CrxMoney.Integrations.Countries.Br`](../../../Countries/br/README.md). Mapping,
idempotency keys, account identity and provider data come from that package, so
replacing Pluggy with another aggregator later means a new adapter, not a new
pipeline.

```
dotnet add package CrxMoney.Integrations.Providers.Br.Pluggy --prerelease
```

```csharp
builder.Services.AddPluggyIntegration();   // registers PluggyIntegrationPlugin as an IIntegrationPlugin
```

## Credentials

| Contract field | Pluggy |
| --- | --- |
| `ApiKey` | client id of the Pluggy developer application |
| `ApiSecret` | client secret of that application |
| `Passphrase` | the item id (a UUID) the application can read |

The plugin exchanges the pair for an API key at `POST /auth` (valid two hours;
cached per credential pair and renewed early) and sends it as `X-API-KEY`.
`my-api.pluggy.ai` is the Meu Pluggy app's own backend and is not used.

**Plans and terms.** With the free *Meu Pluggy* plan, the holder connects their
own banks in Meu Pluggy, creates a developer application, links it through the
*MeuPluggy* connector and uses the resulting item id. That plan covers the
holder's own accounts only and excludes commercial use. Serving other people's
accounts needs Pluggy's commercial plan or another aggregator.

## What it reads

- `GET /items/{id}`: status and consent. `UPDATED`/`UPDATING` proceed;
  `LOGIN_ERROR` (Authentication), `WAITING_USER_INPUT`/`WAITING_USER_ACTION`
  (Configuration) and `OUTDATED` (Upstream) fail fast; an expired
  `consentExpiresAt` fails as Authentication. The connection test
  (`ConnectionId <= 0`) reads only the item and reports status, last refresh and
  consent expiry.
- `GET /accounts?itemId=`: `BANK` accounts are identified by
  `bankData.transferNumber` (`COMPE/branch/account-digit`), `CREDIT` cards by their
  last four digits and the COMPE of the item's bank account. An account that cannot
  be identified fails the sync instead of being guessed.
- `GET /v2/transactions?accountId=` page by page through the `next` cursor. The
  endpoint accepts no date filter, so every sync reads the full history and the
  shared plugin sends only recent lines. A cursor is only ever followed on the
  Pluggy host.

Normalisation into the Open Finance model:

| Pluggy | Open Finance model |
| --- | --- |
| `type` `CREDIT`/`DEBIT` | direction (Pluggy's card amounts carry the opposite sign to checking, so the sign is ignored) |
| `abs(amount)` | amount |
| `creditCardMetadata.transactionDateTime` or `date` | transaction instant |
| `operationType` (`PIX`, `BOLETO`, `OUTROS`, …); `CARD` for card lines | type |
| `operationTypeAdditionalInfo` | type detail (investment sweeps: `OUTROS` + `APL…/RES… CDB`) |
| `descriptionRaw` or `description` | description |
| `paymentData.payer` (credits) / `receiver` (debits): name, `documentNumber`, `routingNumberISPB`, branch, account; the `merchant` for cards or when the name is missing | counterparty (CPF/CNPJ only with valid check digits) |
| `status` `PENDING` | pending, never imported |
| `creditCardMetadata.billId` | card bill |

## Known gaps

- Pluggy's v2 lines carry no Pix end-to-end id or key, and `providerId` came back
  empty in October 2026, so neither is used.
- Payer names are often empty; the CPF/CNPJ is the reliable counterparty identity.
- Investment positions (`/investments`) and identity pre-fill (`/identity`) are not
  read yet.
- Pluggy categories are not imported; CRX•Money rules decide.
- `/v2/transactions` has no date filter, so every sync downloads the account's full
  history (the plugin sends only recent lines). Stopping early would need Pluggy to
  document its page order.

## Tests

`tests/Providers/br/Pluggy` covers item states, consent expiry, account
identification, cursor paging, API-key caching and renewal, sign normalisation,
counterparties, transfer hints, pending lines and failures, against an in-memory
fake of the API. The fixtures are **written from synthetic data** in the shape the
live API returns; valid CPF/CNPJ values are generated at run time.

Apache-2.0. Pluggy is a trademark of its owner; this package is not affiliated
with or endorsed by Pluggy.
