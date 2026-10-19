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
- `GET /v2/transactions?accountId=&dateFrom=` page by page (500 lines, Pluggy's
  default). `dateFrom` is one day before the first booking date the shared plugin
  sends (seven days before the host's window start); the extra day covers Pluggy
  filtering on its own UTC `date` while the plugin dates lines in Brazil's
  calendar. A full-history sync (no window start) sends no `dateFrom`. Each
  response's `next` (`?accountId=…&after=…`) is reduced to its `after` cursor and
  the next page is requested with the same account and `dateFrom`, always on the
  Pluggy host.

  **Why `dateFrom` and not `createdAtFrom`.** `createdAtFrom` (when Pluggy stored
  the line) would also catch a line Pluggy detects late with an older date, but it
  can return only the later of two identical lines (same second, direction and
  amount). That line would then get ordinal 0, the first line's key, and the host
  would drop it as a duplicate. A `dateFrom` window always holds every line of a
  booking date, so ordinals and keys match a full read. Lines booked up to seven
  days before the window start are re-read on every sync. A line Pluggy reports
  later than that is recovered by a full-history sync; the keys make it safe.
- The deposit account's `balance` is reported in the run diagnostics with
  `balanceAtUtc`, the item's `lastUpdatedAt` (Pluggy's last refresh from the bank).
  Card balances are not reported: Pluggy's card balance includes pending purchases.

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
- Paging past 500 lines has been tested against the in-memory fake only: the lab
  accounts hold fewer lines than one page.

## Tests

`tests/Providers/br/Pluggy` covers item states, consent expiry, account
identification, cursor paging, API-key caching and renewal, sign normalisation,
counterparties, transfer hints, pending lines and failures, against an in-memory
fake of the API. The fixtures are **written from synthetic data** in the shape the
live API returns; valid CPF/CNPJ values are generated at run time.

Apache-2.0. Pluggy is a trademark of its owner; this package is not affiliated
with or endorsed by Pluggy.
