# CrxMoney.Integrations.Providers.Br.Pluggy

Brazilian Open Finance through [Pluggy](https://pluggy.ai): reads the bank
accounts, credit cards and investments connected to a Pluggy item and hands them to the
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
  Pluggy host. **Cards** are read from at least 62 days before today
  (`cardTransactionsDateFrom` in the diagnostics), so every pending purchase of the
  open bill is in the read; see "Card balances" below.

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
- `GET /investments?itemId=&page=` (page numbers, 500 per page, Pluggy's default)
  when the item has the `INVESTMENTS` product, and
  `GET /investments/{id}/transactions?page=` for each position when it also has
  `INVESTMENTS_TRANSACTIONS`. Neither endpoint has a date filter, so every position
  (open or fully withdrawn) and every movement is read on each sync; the shared
  plugin applies the sync window to the movements exactly as to account lines. A
  403 on `/investments` (product not enabled for the application) skips the
  positions without failing the sync; the diagnostics say `investmentsSkipped`.

### Card balances

Pluggy's `balance` of a `CREDIT` account is, on Open Finance connectors, **the used
limit** (`creditLimit - availableCreditLimit`): the posted purchases not yet paid
*and* the pending ones (Pluggy marks a card purchase `PENDING` until a bill closes
on it). None of the `creditData` fields separates the two: `minimumPayment`,
`balanceCloseDate` and `balanceDueDate` describe the current bill, and
`availableCreditLimit` is the same figure seen from the other side. So the adapter
reports `balance` as `Card.Owed` with `IncludesPending = true`, and the shared
plugin subtracts the card's pending lines it read (debits minus pending refunds):

| `accounts` row field | Meaning |
| --- | --- |
| `balance` | settled balance owed: Pluggy's balance minus the pending lines; what the posted lines prove |
| `pendingAmount`, `pendingLines` | the pending purchases (net of pending refunds) and how many |
| `balanceWithPending` | Pluggy's balance as reported |
| `balanceAtUtc` | the item's `lastUpdatedAt` |
| `balanceSide` | `liability` (deposit accounts and positions: `asset`) |
| `creditLimit`, `availableCreditLimit`, `minimumPayment`, `billCloseDate`, `billDueDate` | `creditData`, when Pluggy reports them |

Limits of the split:

- **Instalment purchases.** Open Finance issuers reserve the whole amount of a
  parcelado purchase against the limit, but post one instalment per bill. The
  future instalments are in the used limit and are neither posted nor pending
  lines, so `balance` is higher than the posted lines by those instalments.
- **Pending lines older than 62 days** (a bill that closes very late) would be
  missed and counted as settled.
- **Shared limits.** Additional cards and disaggregated credit lines
  (`creditData.disaggregatedCreditLimits`) can share one limit; the adapter uses the
  account's own `balance` and does not combine lines.
- Pluggy's bills (`/bills`) are not read: `creditData` and the card lines carry
  what the split needs, and Pluggy builds the card-side "Pagamento recebido" line
  from the bill when the institution does not report it.

### Investments

Each position becomes an account of kind `investment` in the diagnostics and the
target of its own import events (see the Countries.Br README for the event model).

| Pluggy | Open Finance model |
| --- | --- |
| `number`, else a 12-character `isin`, else `code` (fund CNPJ, ETF or stock ticker) for `MUTUAL_FUND`/`ETF`/`EQUITY`, else Pluggy's `id` | identity basis and key: `br:{compe}:inv:{number\|isin\|code\|pluggy}:{key}`, COMPE of the item's bank account. Positions that would share an id fall back to Pluggy's id |
| `type`, `subtype`, `name`, `issuer`, `issuerCNPJ`, `isin`, `status`, `rate`, `rateType` | position description |
| `balance` (net), `date` | balance and its date (`balanceAtUtc`) |
| `amount` (gross), `amountOriginal`, `amountProfit`, `taxes` (IR), `taxes2` (IOF), `issueDate`, `dueDate` | `grossAmount`, `amountInvested`, `profit`, `incomeTax`, `financialTax`, `issueDate`, `dueDate` |
| transaction `type` `BUY` / `SELL` / `AMORTIZATION` / `INTEREST` / `TAX` / `TRANSFER` (with `movementType` `CREDIT` = in) | operation application / redemption / amortization / income / tax / transferIn or transferOut |
| `amount` (gross), `expenses.incomeTax`, `expenses.iof`, the other `expenses` summed (`iofProvision`, accrued only, is ignored) | gross amount and expenses (`incomeTax`, `iof`, `fees`) |
| `INTEREST` with `movementType` `DEBIT` (Pluggy's default for it) | income paid out to cash: an extra `payout` event |
| `date`, `tradeDate`, `netAmount`, `description` | movement instant, settlement time, reported net amount (kept for reference: Pluggy describes it inconsistently), description |

A `TRANSFER` without amount (Pluggy allows it) or an undocumented type is skipped
and counted in `investmentMovementsSkipped`.

**Linking a sweep to its position.** Pluggy's account lines carry no investment id
and its investment movements carry no account line id, so Pluggy cannot link them.
The shared plugin pairs them by amount and date instead (a checking line with the
`investment` hint and the movement whose cash amount equals it, booked within two
days, on a single candidate position), and writes the position's id on the
checking line and the line's key on the movement (`pair`).

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
| `status` `PENDING` | pending, never imported (but counted in a card's pending amount) |
| `creditCardMetadata.billId` | card bill |
| card `balance` + `creditData` | `Card` balance (used limit, includes pending), limit, minimum payment, bill dates |

## Known gaps

- Pluggy's v2 lines carry no Pix end-to-end id or key, and `providerId` came back
  empty in October 2026, so neither is used.
- Payer names are often empty; the CPF/CNPJ is the reliable counterparty identity.
- Identity pre-fill (`/identity`) and bills (`/bills`) are not read.
- No link between account lines and investment movements, and no card id on the
  checking side of a bill payment: both are paired by the shared plugin by amount
  and date. A sweep with candidates on two positions stays unpaired.
- Investment ids: Pluggy exposes no institution investment id. `number` and `isin`
  survive a change of aggregator; a position known only by Pluggy's `id`
  (`externalAccountIdBasis: pluggy`) does not, and its stability across Pluggy's
  own re-syncs is not documented. The live probe reports the bases in use.
- Fixed income accrues without movements, so a position's events do not add up to
  its balance; `amountProfit`/`grossAmount` give the host the difference.
- The investment endpoints have no date filter: every sync reads every movement.
- Pluggy categories are not imported; CRX•Money rules decide.
- Paging past 500 lines has been tested against the in-memory fake only: the lab
  accounts hold fewer lines than one page.

## Tests

`tests/Providers/br/Pluggy` covers item states, consent expiry, account
identification, cursor paging, API-key caching and renewal, sign normalisation,
counterparties, transfer hints, pending lines, the card balance split, investment
positions, movements and their page-number paging, sweep and bill-payment pairs,
key stability and windowing, and failures, against an in-memory fake of the API
(`/investments` and `/investments/{id}/transactions` as documented, with paging). The fixtures are **written from synthetic data** in the shape the
live API returns; valid CPF/CNPJ values are generated at run time.

Apache-2.0. Pluggy is a trademark of its owner; this package is not affiliated
with or endorsed by Pluggy.
