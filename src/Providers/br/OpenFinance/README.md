# CrxMoney.Integrations.Providers.Br.OpenFinance

The CRX•Money plugin for the **Open Finance Brasil** provider. CRX•Money reaches it
through **aggregators**, licensed intermediaries, and each one has an **adapter**
inside this plugin: [Pluggy](https://pluggy.ai) first. The adapters share one
model, one mapping and one idempotency-key recipe, so replacing an aggregator
means a new adapter, not a new pipeline, and imports nothing twice. Rules that
hold for every Brazilian plugin (tax ids, bank accounts, the banking calendar,
account operators) come from the country package
[`CrxMoney.Integrations.Countries.Br`](../../../Countries/br/README.md).

```
dotnet add package CrxMoney.Integrations.Providers.Br.OpenFinance --prerelease
```

```csharp
using CrxMoney.Integrations.Providers.Br.OpenFinance;

builder.Services.AddOpenFinanceBrasil().AddPluggy();   // one IIntegrationPlugin, one adapter per aggregator
```

The plugin is `OpenFinanceBrasilIntegrationPlugin`, provider id
**`Open Finance Brasil`** (the platform's `Integration.Provider` name). Each
adapter is identified by the aggregator's name, compared exactly (the platform's
`Integration.Aggregator` name). A sync runs on the adapter named by
`IntegrationSyncRequest.AdapterId`; when the request names none and the plugin
has exactly one adapter, that adapter runs.

| Adapter | Adapter id | Registration | Code |
| --- | --- | --- | --- |
| Pluggy | `Pluggy` (`PluggyAdapter.Name`) | `.AddPluggy()` | `Pluggy/`, namespace `CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy` |

## Changes after 1.0.0-preview.8

- **Breaking for installment keys: an installment line gets its own identity (#1424).**
  Up to preview.8 every line's identity was account, instant, direction and amount,
  so the installments of an equal-amount series (one purchase: the same
  `transactionTime` and the same amount on every installment) shared one identity
  and were told apart by an ordinal numbered among the lines of each read. A
  windowed read numbered the installments it held from 0 and gave them keys the host
  already held for other installments of the series, so the host dropped them as
  duplicates; and installment 2 and later of an older purchase was windowed by its
  purchase date, so a daily sync read it and did not send it. Installments that
  arrived after the first full read were never imported. Preview.8 also imported
  some installment lines **twice**: ordinals follow the source's line ids within each
  read, so a read that held a different subset of a series could give a line an
  ordinal, and so a key, it had not had in an earlier read; the host inserted the line
  again under that key (and dropped the line whose key it took). Those duplicates may
  already be posted as movements.

  **The rule.** A card line that is an installment, by the same test as
  `card.installment` (the source's number and count, else the trailing `dd/dd` of
  the description; never a `SINGLE` line, never a deposit-account line), has the
  line identity

  ```
  {externalAccountId}|{transactionTime in UTC, yyyy-MM-ddTHH:mm:ssZ}|{C or D}|{amount, 0.00}|{number}/{count}
  ```

  with the number and count in decimal, without leading zeros (`|2/6`, `|10/12`).
  The key is built from it as before: `brof:` + the first 32 hex digits of
  SHA-256(identity + `|` + ordinal). Installment 02/06 of a purchase at
  2025-03-14T15:09:26Z, a 41.37 debit on card `br:077:card:1234`, has the identity
  `br:077:card:1234|2025-03-14T15:09:26Z|D|41.37|2/6` and ordinal 0. Every other line
  keeps the preview.8 identity and key byte for byte (the golden test of the move
  from the Pluggy package passes unchanged). The installments of one purchase never
  share an identity, so each one is ordinal 0 in any read that holds it, whatever
  else the read holds; ordinals above 0 are left to genuinely identical lines (the
  same installment of two identical purchases in the same second). The bill dates
  are left out of the identity on purpose: Open Finance sends its placeholder
  `billPostDate` (`0001-01-01`) on some posted lines, and the real date arriving
  later must not change the key.

  **A line keeps its key while it reads as the same installment.** The number and
  count come from the source's metadata when it reports both, and only otherwise from
  the description, so the key changes when what the plugin reads changes:
  - a source stops reporting the number and count on a line whose description has no
    `dd/dd` suffix, or starts calling the line `SINGLE`: it stops being an installment;
  - the mirror case: the metadata appears later on a line that had none and whose
    description has no suffix: it becomes one;
  - the metadata and the suffix disagree (metadata 3/6, description "…02/06"): the
    metadata wins, so a line read first without metadata (as 2/6) and later with it
    (as 3/6) changes key;
  - the metadata is out of bounds (a count below 2, a number past the count): the line
    is not an installment and the description is **not** used instead, so metadata
    appearing out of bounds on a line read before from its suffix changes its key.

  **Sending.** A line is sent when its `transactionDate` or the date its event carries
  (`OccurredAtUtc`: for an installment, the date of its bill by the dating rule below)
  is on or after the first date the sync sends, seven days before the host's window
  start. Before, the plugin went by `transactionDate` only, which for installment 2 and
  later is the purchase date; every line preview.8 sent is still sent. A line that is
  not an installment is dated at `transactionDate`, so nothing changes for it. The
  checkpoint keeps its documented meaning, the latest
  `transactionDate` of an account line read (an installment's bill date does not move
  it), and pairs still go by `transactionDate`.

  **Version impact.** This changes the idempotency-key recipe for installment
  lines, which [`docs/versioning.md`](../../../../docs/versioning.md) counts as
  breaking for a provider. Previews may break when the release notes list the
  change; this is that listing. A stable version would need a new major version.

  **Host migration.** Installment lines imported with preview.8 or earlier keep their
  old key in the host. After the upgrade the plugin sends the same lines with new
  keys, and a host whose only guard is the unique key (TenantUuid,
  IntegrationConnectionId, IdempotencyKey) inserts them again: a full re-read
  duplicates every installment line already imported, and each windowed read
  duplicates the installment lines in its window (the seven days of overlap
  included). Lines that are not installments are not affected.

  The host re-keys the existing items by the aggregator's line id, which it stores as
  `ExternalId` (Pluggy's transaction id, stable for a line while the Pluggy item
  lives). The step applies **only to events whose provider data carries
  `card.installment`**: a line that is not an installment and comes back with, say, a
  corrected amount is a new item today and must stay one. For such an event whose key
  the connection does not hold, one item of the same connection and external account
  with the same `ExternalId` (the lowest `Id`) gets the new key, and, if the host
  re-dates, the new date, instead of a new item being inserted. Pseudo-SQL against
  `Integration.ImportItem`, with the host's column names:

  ```sql
  -- for each incoming event @e of connection @c whose ProviderData has card.installment,
  -- in the transaction that would insert it
  WITH target AS (
      SELECT TOP (1) Id
        FROM Integration.ImportItem
       WHERE TenantUuid = @tenant
         AND IntegrationConnectionId = @c
         AND ExternalAccountId = @e.ExternalAccountId
         AND ExternalId = @e.ExternalId
         AND IdempotencyKey <> @e.IdempotencyKey
       ORDER BY Id)
  UPDATE i
     SET IdempotencyKey = @e.IdempotencyKey,
         UpdatedAt = SYSDATETIMEOFFSET()
    FROM Integration.ImportItem AS i
    JOIN target AS t ON t.Id = i.Id
   WHERE NOT EXISTS (SELECT 1
                       FROM Integration.ImportItem AS k
                      WHERE k.TenantUuid = @tenant
                        AND k.IntegrationConnectionId = @c
                        AND k.IdempotencyKey = @e.IdempotencyKey);  -- keeps IX_ImportItem_Idempotency unique
  -- then insert @e only when no item of @c holds @e.IdempotencyKey;
  -- any other item of @c with the same ExternalAccountId and ExternalId whose IdempotencyKey differs from
  -- @e's is a duplicate preview.8 imported: flag it for review (it may already be posted: MovementId),
  -- never delete it silently.
  ```

  Keep the step on every sync from the upgrade on: items outside every window keep
  their old key until a full read passes them through it, which is harmless. Until
  the host has the step (or de-duplicates by `ExternalId` some other way), a full
  re-read with this version inserts a duplicate of every installment line already
  imported: do not run one before. The re-key works only while the line keeps its
  `ExternalId`: lines read through another aggregator, or through a re-created Pluggy
  item (a reconnect issues new transaction ids), are not re-keyed and come in as new
  items.
  Code: `OpenFinanceTransactionMapper.LineIdentity`, and the plugin's send test,
  which uses `OpenFinanceTransactionMapper.EventDate`.

- **Installments on card lines (#1417).** A card line now says whether it is an
  installment (parcela) of a purchase or a fee, and an installment's event is dated
  at its bill. New keys inside `card`, after `last4`, `billId` and `issuerCompe`
  (which, like `description`, `transactionTime`, `transactionDate` and
  `sourcePayload`, are unchanged); each is present only when the source reports it:

  | Key | Value | Pluggy `creditCardMetadata` |
  | --- | --- | --- |
  | `card.paymentType` | `SINGLE` or `INSTALLMENT` (Open Finance `A_VISTA`/`A_PRAZO` read as these) | `paymentType` |
  | `card.billPostDate` | `yyyy-mm-dd`: the date the line was put on its bill; absent while Open Finance sends its placeholder `0001-01-01` (on no bill yet) | `billPostDate` |
  | `card.billForecastDate` | `yyyy-mm-dd`: the expected date of the line's bill | `billForecastDate` |
  | `card.payeeMcc` | the payee's merchant category code, four digits (`"0742"`) | `payeeMCC` (number or string) |
  | `card.installment.number` | the installment number, from 1 | `installmentNumber` |
  | `card.installment.count` | the number of installments, at least 2 | `totalInstallments` |
  | `card.installment.purchaseDate` | `yyyy-mm-dd`: the purchase date the source reports (its placeholder `0001-01-01` counts as missing), else the line's `transactionDate` | `purchaseDate` |
  | `card.installment.totalAmount` | the whole purchase's amount, when reported: **direct connectors only**, Open Finance has none. Information only: never book it as a second movement | `totalAmount` |
  | `card.installment.source` | `metadata` (the source's number and count) or `description` (the trailing `dd/dd` of the description) | — |
  | `card.installment.dateSource` | what the event's date came from: `billPostDate`, `bill`, `billForecastDate` or `purchase` (see the dating rule) | — |

  **What an installment is.** A line whose payment type is `SINGLE` is never one.
  Otherwise, when the source reports a number and a count, they decide: an
  installment when the count is at least 2 and the number is between 1 and the
  count. The payment type may be missing on any such line: Pluggy sends none on
  some card lines (it drops `operationType` for cards), and Open Finance requires
  it only on purchases, so a fee in installments (typed `TARIFA`, "ANUIDADE
  DIFERENCI04/10") often has none. When the source reports no number and count, the
  plugin reads the trailing `dd/dd` of the description, spaced or compact ("TINTAS
  EXEMPLO 01/02", "LOJA EXEMPLO*DECOR06/06", "EC *EXEMPLOINFORMAT10/12"; ASCII
  digits, never right after a digit), with the same bounds, and says
  `source: "description"`.

  **Dating rule.** An installment's event (`OccurredAtUtc`) is dated from its bill,
  so the books follow the invoice and a past month stops growing when a later
  installment arrives. The first of these that is **later than the purchase date**
  wins, and `dateSource` says which:

  1. `billPostDate`: the date the line was put on its bill (the placeholder
     `0001-01-01` counts as missing);
  2. `bill`: the bill the line names in `card.billId`, when the read listed it (the
     card's `bills` row): its **closing date**, or its **due date** when the bill
     has no closing date. This covers the tail of long series, whose `billPostDate`
     equals the purchase date;
  3. `billForecastDate`: the first day of the month of `card.billForecastDate`;
  4. `purchase`: the line keeps the date it had before (the date of
     `transactionTime`, which Open Finance issuers set to the purchase).

  An installment is therefore never dated before its purchase, whatever the source
  sends. A single purchase and every other line keep the date of `transactionTime`.
  `transactionTime` and `transactionDate` still say when the bank dated the line,
  and the checkpoint and pairs still go by `transactionDate`; a line is sent by its
  event's date (since #1424, above).

  **A new date does not move the key.** The idempotency key never depends on the
  event's date or on the bill dates, so dating an installment from its bill does not
  change its key (the installment's number and count are part of it since #1424,
  above). Whether a re-sync re-dates installment lines already imported is the host's
  decision: the plugin only reports the new date on the event, and a host that drops
  events whose key it already holds keeps the old date.
  Code: `OpenFinanceTransactionMapper.Installment` and `.EventDate`, from
  `OpenFinanceTransaction.CardDetails` (`OpenFinanceCardLineDetails`), which an
  adapter fills from its aggregator's card fields, and `OpenFinanceLineLinks.Bill`,
  which the shared plugin fills from the card's bills.

## Changes in 1.0.0-preview.8

- **Card bills (#1386).** The plugin reads a card's bills (faturas) and reports them
  on the card's `accounts` row, after the earlier keys, as `bills`: one object per
  bill, oldest due date first, with `billId` (the id card lines carry in
  `card.billId`), `dueDate`, `totalAmount`, and when reported `closeDate`,
  `minimumPayment`, `currencyCode`, plus `postedLinesRead` (the posted lines of this
  read that name the bill; a windowed read sees only part of them). A host sets its
  ledger against the invoice with them. The Pluggy adapter reads
  `GET /bills?accountId=` for each card, every bill Pluggy holds, best effort: a card
  whose bills cannot be read has no `bills` and the sync does not fail
  (`sourceDiagnostics.cardsWithoutBills` counts them, `billPages` the pages read). A
  source adapter returns them in `OpenFinanceAccountTransactions.Bills`. Events,
  keys and the other keys are unchanged.

## Changes in 1.0.0-preview.6

- **Card settled balance (fix).** A bill payment the card still shows pending after
  the paying account's debit has posted no longer raises the card's settled
  `balance`. Preview.5 subtracted every pending line from the used limit, so a
  pending payment credit was added back and the settled balance came out as large
  as the bill already paid; a host that books the payment from the checking debit
  then saw a difference it could not explain. Pending card credits that are bill
  payments (the same rule as the `card_bill_payment` hint) now count as applied;
  pending refunds are still left out. New card row keys after the preview.5 ones:
  `pendingBillPayments` and `pendingBillPaymentLines`.
- **Pending card-side bill payments pair.** The checking debit of a bill payment
  now pairs with the card's payment credit while that credit is still pending: the
  checking line gets the paid `card` (with `externalAccountId`) and counts as
  `paired`, but carries no `pair`, because the pending line has no key yet.
  Idempotency keys and amounts are unchanged.

## Moving from `CrxMoney.Integrations.Providers.Br.Pluggy`

This package replaces `CrxMoney.Integrations.Providers.Br.Pluggy`, which stays at
`1.0.0-preview.4` and receives no further versions. Idempotency keys, external
account ids, provider data (its `provider` is still `Pluggy`, the aggregator) and
schema ids are unchanged, so existing imports and account maps carry over. The
provider id changes from `Pluggy` (the aggregator) to `Open Finance Brasil` (the
provider). A host changes:

| Before | After |
| --- | --- |
| package `CrxMoney.Integrations.Providers.Br.Pluggy` | package `CrxMoney.Integrations.Providers.Br.OpenFinance` |
| `services.AddPluggyIntegration(baseAddress)` | `services.AddOpenFinanceBrasil().AddPluggy(baseAddress)` |
| `using CrxMoney.Integrations.Providers.Br.Pluggy;` | `using CrxMoney.Integrations.Providers.Br.OpenFinance;` (and `.Pluggy` for `PluggyAdapter`) |
| `PluggyIntegrationPlugin` / `PluggyIntegrationPlugin.Id` (`"Pluggy"`) | `OpenFinanceBrasilIntegrationPlugin` / `OpenFinanceBrasilIntegrationPlugin.Id` (`"Open Finance Brasil"`) |
| — | `IntegrationSyncRequest.AdapterId = "Pluggy"` (optional while Pluggy is the only adapter) |
| `CrxMoney.Integrations.Countries.Br.OpenFinance.*` | `CrxMoney.Integrations.Providers.Br.OpenFinance.*` |
| `PluggyServiceCollectionExtensions.DefaultBaseAddress` | `PluggyAdapter.DefaultBaseAddress` |
| `CrxMoney.Integrations.Countries.Br.OpenFinance.BrazilianInstitutions` | `CrxMoney.Integrations.Countries.Br.BrazilianInstitutions` |

## Connection settings

Every adapter declares its settings through the optional contract
`IIntegrationConnectionSettingsProvider` (see the
[Abstractions README](../../../Abstractions/CrxMoney.Integrations.Abstractions/README.md#connection-settings)),
in English (default) and Brazilian Portuguese (any `pt` culture), and the plugin
implements `IIntegrationConnectionTester`. The test runs the adapter's check (for
Pluggy: the item and its accounts) and answers without the aggregator's name:
refresh time, consent expiry, sandbox flag, and the account operators found by the
COMPE of the deposit accounts in the country package's list (`Inter`,
`https://inter.co/`).

The Pluggy adapter's fields:

| Field | Slot | Secret | Format |
| --- | --- | --- | --- |
| `clientId` | `ApiKey` | no | `uuid` |
| `clientSecret` | `ApiSecret` | yes (`writeOnly`) | non-empty |
| `itemId` | `Passphrase` | no | `uuid` |

Its walkthrough follows Pluggy's personal-use guide: connect banks in Meu Pluggy,
copy the application's Client ID and Secret in the Pluggy Dashboard, connect the
MeuPluggy connector on the application's Demo page, copy the item id, test and
save. The plan note says the free Meu Pluggy plan covers the holder's own
accounts only.

## The model: one for every aggregator

Brazilian Open Finance data reaches CRX•Money through aggregators (Pluggy first).
To keep the aggregator replaceable, everything that matters to the platform is
decided here, once:

```
aggregator API ──► IOpenFinanceSource (adapter, per aggregator)
                       │  normalises into
                       ▼
               OpenFinanceAccount / OpenFinanceTransaction
               OpenFinanceInvestment / OpenFinanceInvestmentMovement
                       │  OpenFinanceIntegrationPlugin (shared)
                       ▼
               IntegrationImportEvent (keys, dates, provider data, pairs)
```

- **Account identity** is the bank's own (`br:{compe}:{branch}:{number}`), never
  the aggregator's account id. Imports of the same account through another
  aggregator, Inter's own API or an OFX file land on the same account map.
- **Idempotency** is built only from what the bank reports:
  `brof:` + SHA-256(account, instant to the second, credit/debit, amount, for a
  card installment its number and count (since #1424, see the changes after
  preview.8), ordinal among identical lines). Counterparty data is left out on purpose:
  aggregators enrich it later and unevenly. The aggregator's id
  becomes the import's `ExternalId` and is kept in provider data, but never in the
  key. Switching aggregators on an existing connection therefore imports nothing
  twice. The recipe is part of the package's released behaviour (`docs/versioning.md`).
- **Dates**: booking date in Brazil's calendar at midnight São Paulo; a card
  installment is dated from its bill (see the changes after preview.8).
- **Provider data** (schemas `br.openfinance.transaction.deposit`/`.withdrawal` v1):
  `provider` (the aggregator), `channel: "openfinance"`, type and detail,
  description, `counterparty` (name, `taxId` + `taxIdKind` + `taxIdCountry`,
  ISPB, branch, account; replaced by `counterpartyIsHolder: true` when the other
  side is the account holder itself, as in sweeps), `pix` when present, `card`
  (last four, bill, `issuerCompe`; after preview.8 also `paymentType`,
  `billPostDate`, `billForecastDate`, `payeeMcc` and `installment`), a `transferHint` (`investment` for automatic
  CDB sweeps and similar, and for settlements with the bank's own brokerage such
  as "Saída COR ITAUCOR LIQUIDACAO" or "Entrada COR OPERACOES B3", which
  aggregators categorise as card payments or dividends; tax the brokerage
  withholds, "Saída COR IRRF ETF", gets none;
  `card_bill_payment`), and the aggregator's raw line as
  `sourcePayload`. Added in preview.5, all optional:
  - on a checking line with the `investment` hint, `investment` (`product`: the
    `CDB`/`LCA`/`LCI`/`POUPANCA` the line names; when paired, the position's
    `externalAccountId`, `externalAccountIdBasis`, `type`, `subtype`, `name`);
  - on a checking-side `card_bill_payment`, when paired, `card` with the paid
    card's `last4`, `issuerCompe`, `billId` and `externalAccountId`;
  - `pair` (`externalAccountId`, `idempotencyKey`, `basis: "amountAndDate"`): the
    import event on the other side, on both sides of a pair.
- **Pairs.** The bank reports a sweep or a bill payment on two accounts of the same
  connection and no aggregator links the two lines, so the plugin pairs them:
  same amount, booking dates within two days for investments (from one day
  before to three days after the checking debit for card payments), and a single
  candidate account at the nearest date. A line with candidates on two accounts
  stays unpaired. Pairs only add provider data; keys and amounts do not change.
  The card's payment credit pairs even while it is still pending (since preview.6;
  a posted one wins at the same date): the checking debit then gets the paid
  `card` but no `pair`, since a pending line has no key and is not sent.
- **Investments** (schemas `br.openfinance.investment.deposit`/`.withdrawal` v1).
  A position is an account of its own, `br:{compe}:inv:{basis}:{key}`, where the
  basis says what identifies it (`number`: the institution's reference for the
  holder's position, `isin`, `code`: a fund's CNPJ or a ticker, or the source's
  name when only the aggregator's id is known). Each movement gives events on the
  position whose amounts add up to its gross amount:

  | Operation | Events on the position |
  | --- | --- |
  | `application` | deposit of gross + expenses (what left the cash account), then expenses |
  | `redemption`, `amortization` | withdrawal of gross − expenses (what reached the cash account), then expenses |
  | `income` | deposit of the gross income, then expenses; when paid out, a `payout` withdrawal of the rest |
  | `tax` | one withdrawal, `taxKind` `incomeTax`, `iof` or `tax` |
  | `transferIn`, `transferOut` | one deposit or withdrawal of the gross amount |

  Expenses are one withdrawal each for `incomeTax`, `iof` and `fee` (all other
  fees summed), with `component` and `parentIdempotencyKey`. The event that moves
  cash (application, redemption, amortization, payout) carries
  `transferHint: "investment"`, `cashAmount` and, when paired, `pair`. Keys:
  `brof:` + SHA-256(`inv`, position id, instant to the second, operation, gross
  amount, ordinal among identical movements), plus `|component` for expenses and
  payouts; so they never collide with account-line keys and do not depend on the
  aggregator's ids. Provider data also holds the position's `type`, `subtype`,
  `name`, `issuer`, `issuerTaxId`, `isin`, the movement's `grossAmount`,
  `expenses`, `reportedNetAmount`, `settlementTime` and, on the main event, the raw
  movement.
- **Sync behaviour**: pending lines are never imported. The plugin asks the source
  for lines booked from seven days before the host's window start (the full history
  when the host gives no start) and sends those booked from then on, and a card
  installment also when its bill is dated from then on (#1424), so a later
  installment of an older purchase is sent when its bill arrives. A source must
  return every line of each booking date it covers, so lines that differ only by
  ordinal are numbered as in a full read and keep their keys. Investment movements follow the
  same rule: the source returns all of them and the plugin sends those booked from
  the same date. The checkpoint `transactions` holds the latest booking date of an
  account line read, and never moves back when a window reads nothing new.
- **Institution website.** Every account row, position row and import event
  carries `institutionWebsite` when the account's COMPE code (the bank of a
  deposit account, the issuer of a card, the institution holding a position) is in
  the institution list; otherwise the field is absent. The code always stays in
  the external id. The host matches the website's host to the workspace's account
  operator, so no Brazilian bank code has to live in the platform's database.
- **Run diagnostics** (the provider block the host shows per run): `source`,
  `events`, `pendingSkipped`, `sentFrom` (absent on a full read),
  `credentialExpiresAtUtc`, `accounts`, `transferPairs`, `connector` and `sourceDiagnostics`.

  `connector` (also on a connection test) is the connector the item reads through, when the plugin can tell:
  `id` (the aggregator's), `name`, `isBusiness`, `iconUrl` (when the aggregator gave one) and
  `institutionWebsite`. A directly connected Pluggy item names its connector (Inter or Inter Empresas, typed
  `BUSINESS_BANK` for a business one); an item through Meu Pluggy names MeuPluggy, so the bank of its accounts and
  their holder (a CNPJ means a company) pick the connector from the country package's list. A host names and marks
  the connection after it; `IntegrationConnectionTestResult.Connector` carries the same on a connection test.
  Every key is generic (no aggregator names), so a host can render the block as it
  comes.

  One `accounts` row per account and per investment position. Every row starts
  with the preview.4 keys `kind` (`deposit`, `creditCard`, `investment`),
  `externalAccountId`, `lines` (read; for a position, its movements), `posted`,
  `sent` (events sent: a position's movement can give several) and, when the
  source reports a balance, `balance` and `balanceAtUtc`; preview.5 adds
  `balanceSide` (`asset` or `liability`) after them and the kind's own fields:

  | Kind | `balance` | Added fields |
  | --- | --- | --- |
  | `deposit` | the balance the source reports | — |
  | `creditCard` | **settled** amount owed, as the host books it: the source's figure minus the pending lines when it includes them (`OpenFinanceCardBalance.IncludesPending`), minus the bill payments still pending on the card (applied: the host books them from the paying account) | `pendingAmount` (pending debits minus pending credits), `pendingLines`, `balanceWithPending`, and when reported `creditLimit`, `availableCreditLimit`, `minimumPayment`, `billCloseDate`, `billDueDate`; preview.6 adds `pendingBillPayments` (the pending card credits that are bill payments, counted as applied) and `pendingBillPaymentLines`; after preview.7, `bills` (see the changes) |
  | `investment` | the position's net balance, as of `balanceAtUtc` (its reference date) | `externalAccountIdBasis`, `type`, `subtype`, `name`, `issuer`, `status`, `currencyCode`, and when reported `grossAmount`, `amountInvested`, `profit`, `incomeTax`, `financialTax`, `issueDate`, `dueDate` |

  A window of lines proves nothing about a balance, so comparing `balance` with
  the ledger of `externalAccountId` as of `balanceAtUtc` is the host's job. For a
  card the settled balance is the one posted lines can prove, with the bill
  payments the host books from the paying account (`pendingBillPayments`) applied:
  the card's events minus `pendingBillPayments` add up to it; for a fixed-income
  position the ledger lacks the accrued income (no movements), which `profit` and
  `grossAmount` describe.

  `transferPairs` counts the pairs over the lines sent:
  `investment` (`lines`: checking lines with the investment hint, `paired`,
  `ambiguous`, `unmatched`, `positionSideUnpaired`: cash-moving investment events
  with no checking line) and `cardBillPayment` (`lines`: checking-side bill
  payments, `paired`, `ambiguous`, `unmatched`, `cardSideUnpaired`).

## Adding an aggregator

1. Implement `IOpenFinanceSource` in a folder of this plugin, `<Aggregator>/`: authenticate,
   check the consent in `CheckConnectionAsync`, and in `ReadAsync` return every
   account (with its bank identity, and its balance when the aggregator reports it:
   `Balance` for deposits, `Card` for cards, saying whether it includes pending
   purchases) and every line booked on or after `readFrom` (all lines when it is
   `null`; earlier lines are allowed and dropped), normalised into
   `OpenFinanceTransaction` (direction from the type, positive amount, the bank's
   transaction instant, counterparty CPF/CNPJ). A card whose `Card` balance
   includes pending purchases comes with all its pending lines, whatever their
   date. Return the investment positions in `OpenFinanceSnapshot.Investments` with
   every movement, the best institution-side identity available, and expenses
   split into income tax, IOF and other fees. Throw `OpenFinanceSourceException`
   with an `IntegrationErrorKind` for expected failures.
2. Name it after the aggregator (`SourceName`: the adapter id and the `provider` in provider data), return
   its settings schema from `GetConnectionSettings`, and add an `Add<Aggregator>()` extension on
   `OpenFinanceBrasilBuilder` that registers it as an `IOpenFinanceSource`. The one
   `OpenFinanceBrasilIntegrationPlugin` picks it up.
3. Test the normalisation against synthetic fixtures in the aggregator's shape, and
   check that the keys for the shared fixture lines match the existing aggregator's.

Nothing else changes: mapping, keys, schemas, review rules and participant matching
are shared.

## The Pluggy adapter

Reads the bank accounts, credit cards and investments connected to a Pluggy item
and hands them to the model above. Code: `Pluggy/` (namespace
`CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy`).

### Credentials

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

### What it reads

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
  open bill is in the read; see "Card balances" below. An installment line's `date`
  is usually its bill post date, so the window catches installment 2 and later of an
  older purchase when its bill arrives (the tail of a long series is the exception:
  see the known gaps).

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
  Rows and events also carry `institutionWebsite` from the country package's
  institution list (by the COMPE of `bankData.transferNumber`); the adapter adds
  nothing of its own for it.
- `GET /investments?itemId=&page=` (page numbers, 500 per page, Pluggy's default)
  when the item has the `INVESTMENTS` product, and
  `GET /investments/{id}/transactions?page=` for each position when it also has
  `INVESTMENTS_TRANSACTIONS`. Neither endpoint has a date filter, so every position
  (open or fully withdrawn) and every movement is read on each sync; the shared
  plugin applies the sync window to the movements exactly as to account lines. A
  403 on `/investments` (product not enabled for the application) skips the
  positions without failing the sync; the diagnostics say `investmentsSkipped`.

#### Card balances

Pluggy's `balance` of a `CREDIT` account is, on Open Finance connectors, **the used
limit** (`creditLimit - availableCreditLimit`): the posted purchases not yet paid
*and* the pending ones (Pluggy marks a card purchase `PENDING` until a bill closes
on it). None of the `creditData` fields separates the two: `minimumPayment`,
`balanceCloseDate` and `balanceDueDate` describe the current bill, and
`availableCreditLimit` is the same figure seen from the other side. So the adapter
reports `balance` as `Card.Owed` with `IncludesPending = true`, and the shared
plugin subtracts the card's pending lines it read (debits minus pending credits),
except the pending bill payments.

**Pending bill payments.** The card can keep the payment it received ("Pagamento
recebido") pending for days after the checking debit has posted. The used limit
already counts it as paid, and so does the host, which books a bill payment from
the paying account's debit. So a pending card credit that is a bill payment (a
credit whose description or type detail says `PAGAMENTO`, `PAGTO` or `FATURA`, the
rule of the `card_bill_payment` hint) is treated as applied, not added back;
pending refunds (`ESTORNO` and the like) are still left out. Example: a bill of 750
paid from checking, its card-side credit still pending, new purchases of 155.50
pending. Used limit 155.50, `pendingAmount` -594.50, `pendingBillPayments` 750,
`balance` 0 (preview.5 said 750), `balanceWithPending` 155.50. The card's posted
events (purchases of 750) less the checking payment the host books give the same 0.
The checking debit pairs with the pending credit (`paired`), names the paid card,
and has no `pair` until the card posts the credit.

| `accounts` row field | Meaning |
| --- | --- |
| `balance` | settled balance owed: Pluggy's balance minus the pending lines, with pending bill payments applied; what the host's ledger holds |
| `pendingAmount`, `pendingLines` | the pending lines, debits minus credits (bill payments included), and how many |
| `pendingBillPayments`, `pendingBillPaymentLines` | the pending card credits that are bill payments, counted as applied, and how many |
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
- The split does not use Pluggy's bills (`/bills`, read since preview.8 for the
  card's `bills` row, #1386): `creditData` and the card lines carry what it needs,
  and Pluggy builds the card-side "Pagamento recebido" line from the bill when the
  institution does not report it.

#### Investments

Each position becomes an account of kind `investment` in the diagnostics and the
target of its own import events (see "Investments" under the model above).

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
| `creditCardMetadata.paymentType`, `installmentNumber`, `totalInstallments`, `purchaseDate`, `totalAmount`, `billPostDate`, `billForecastDate`, `payeeMCC` | card line details (`CardDetails`): bill dates by their `yyyy-mm-dd` part; `purchaseDate` by its date part when it is a date or midnight UTC, else as an instant in Brazil's calendar, and the placeholder `0001-01-01` as no date; `payeeMCC` as four digits |
| `GET /bills?accountId=` (since preview.8) | the card's bills: its `bills` row, and the closing (else due) date that may date an installment naming the bill |
| card `balance` + `creditData` | `Card` balance (used limit, includes pending), limit, minimum payment, bill dates |

### Known gaps

- Pluggy's v2 lines carry no Pix end-to-end id or key, and `providerId` came back
  empty in October 2026, so neither is used.
- Payer names are often empty; the CPF/CNPJ is the reliable counterparty identity.
- Identity pre-fill (`/identity`) is not read. Bills (`/bills`) are read since
  preview.8 (#1386), best effort.
- **Installments of older purchases on incremental syncs.** Since #1424 an
  installment line has an identity of its own (its number and count join it) and is
  sent by its bill's date, so installment 2 and later of an older purchase is
  imported by the daily sync when Pluggy reports it with its bill post date as
  `date`, with the key any other read gives it. One shape is still not read
  incrementally: the tail of a long series, whose `billPostDate` equals the purchase
  date, keeps the purchase date as Pluggy's `date`, and `dateFrom` filters on
  `date`, so a windowed read does not receive it. A full-history sync imports it,
  and after the host migration (changes after preview.8) a full read imports nothing
  twice. Reading cards by `createdAtFrom` as well would cover it.
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

`tests/Providers/br/OpenFinance` covers the shared model (mapping, keys, windowing, card balances,
investments, pairs, institution websites) against a fake source, and, under `Pluggy/`, item states, consent expiry, account
identification, cursor paging, API-key caching and renewal, sign normalisation,
counterparties, transfer hints, pending lines, the card balance split, card installments and their dates
(`transactions-card-installments.json`), installment identities across reads of any subset and their windowed
sending (#1424), investment
positions, movements and their page-number paging, sweep and bill-payment pairs,
key stability and windowing, and failures, against an in-memory fake of the API
(`/investments` and `/investments/{id}/transactions` as documented, with paging). The fixtures are **written from synthetic data** in the shape the
live API returns; valid CPF/CNPJ values are generated at run time. `Pluggy/Fixtures/golden-events-82b8c61.json` holds
the keys, account ids and provider-data hashes the code produced before the move from the Pluggy package; a test
checks the plugin still produces them byte for byte.

Apache-2.0. Pluggy is a trademark of its owner; this package is not affiliated
with or endorsed by Pluggy.
