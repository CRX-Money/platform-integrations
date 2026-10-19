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
| `BrazilianBankAccount` | A deposit account as the banks identify it: COMPE + branch + account with check digit. `ExternalAccountId` is `br:{compe}:{branch}:{number}`; cards are `br:{compe}:card:{last4}`; investment positions are `br:{compe}:inv:{basis}:{key}`. |
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
               OpenFinanceInvestment / OpenFinanceInvestmentMovement
                       │  OpenFinanceIntegrationPlugin (shared)
                       ▼
               IntegrationImportEvent (keys, dates, provider data, pairs)
```

- **Account identity** is the bank's own (`br:{compe}:{branch}:{number}`), never
  the aggregator's account id. Imports of the same account through another
  aggregator, Inter's own API or an OFX file land on the same account map.
- **Idempotency** is built only from what the bank reports:
  `brof:` + SHA-256(account, instant to the second, credit/debit, amount,
  ordinal among identical lines). Counterparty data is left out on purpose:
  aggregators enrich it later and unevenly. The aggregator's id
  becomes the import's `ExternalId` and is kept in provider data, but never in the
  key. Switching aggregators on an existing connection therefore imports nothing
  twice. The recipe is part of the package's released behaviour (`docs/versioning.md`).
- **Dates**: booking date in Brazil's calendar at midnight São Paulo.
- **Provider data** (schemas `br.openfinance.transaction.deposit`/`.withdrawal` v1):
  `provider` (the aggregator), `channel: "openfinance"`, type and detail,
  description, `counterparty` (name, `taxId` + `taxIdKind` + `taxIdCountry`,
  ISPB, branch, account; replaced by `counterpartyIsHolder: true` when the other
  side is the account holder itself, as in sweeps), `pix` when present, `card`
  (last four, bill, `issuerCompe`), a `transferHint` (`investment` for automatic
  CDB sweeps and similar, `card_bill_payment`), and the aggregator's raw line as
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
  when the host gives no start) and sends exactly those. A source must return every
  line of each booking date it covers, so lines that differ only by ordinal are
  numbered as in a full read and keep their keys. Investment movements follow the
  same rule: the source returns all of them and the plugin sends those booked from
  the same date. The checkpoint `transactions` holds the latest booking date of an
  account line read, and never moves back when a window reads nothing new.
- **Run diagnostics** (the provider block the host shows per run): `source`,
  `events`, `pendingSkipped`, `sentFrom` (absent on a full read),
  `credentialExpiresAtUtc`, `accounts`, `transferPairs` and `sourceDiagnostics`.
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
  | `creditCard` | **settled** amount owed: the source's figure minus the pending lines when it includes them (`OpenFinanceCardBalance.IncludesPending`) | `pendingAmount` (pending debits minus pending refunds), `pendingLines`, `balanceWithPending`, and when reported `creditLimit`, `availableCreditLimit`, `minimumPayment`, `billCloseDate`, `billDueDate` |
  | `investment` | the position's net balance, as of `balanceAtUtc` (its reference date) | `externalAccountIdBasis`, `type`, `subtype`, `name`, `issuer`, `status`, `currencyCode`, and when reported `grossAmount`, `amountInvested`, `profit`, `incomeTax`, `financialTax`, `issueDate`, `dueDate` |

  A window of lines proves nothing about a balance, so comparing `balance` with
  the ledger of `externalAccountId` as of `balanceAtUtc` is the host's job. For a
  card the settled balance is the one posted lines can prove; for a fixed-income
  position the ledger lacks the accrued income (no movements), which `profit` and
  `grossAmount` describe.

  `transferPairs` counts the pairs over the lines sent:
  `investment` (`lines`: checking lines with the investment hint, `paired`,
  `ambiguous`, `unmatched`, `positionSideUnpaired`: cash-moving investment events
  with no checking line) and `cardBillPayment` (`lines`: checking-side bill
  payments, `paired`, `ambiguous`, `unmatched`, `cardSideUnpaired`).

### Adding an aggregator

1. Implement `IOpenFinanceSource` in `src/Providers/br/<Aggregator>`: authenticate,
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
2. Subclass `OpenFinanceIntegrationPlugin` with the provider id and register it.
3. Test the normalisation against synthetic fixtures in the aggregator's shape, and
   check that the keys for the shared fixture lines match the existing aggregator's.

Nothing else changes: mapping, keys, schemas, review rules and participant matching
are shared.
