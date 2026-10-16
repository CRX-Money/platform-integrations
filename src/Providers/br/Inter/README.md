# CrxMoney.Integrations.Providers.Br.Inter

CRX•Money Platform integration for **Banco Inter** business accounts (Inter
Empresas / Conta Digital PJ). It reads the account's *enriched statement*
through the Inter Banking API and hands each entry to the platform as an
`IntegrationImportEvent`, with the counterparty's CPF/CNPJ and the Pix
end-to-end id when Inter provides them.

```
dotnet add package CrxMoney.Integrations.Providers.Br.Inter --prerelease
```

```csharp
builder.Services.AddInterIntegration();   // registers InterIntegrationPlugin as an IIntegrationPlugin
```

## What it imports

| Inter | Import event |
| --- | --- |
| `idTransacao` | `ExternalId`; idempotency key `inter:<connectionId>:<idTransacao>` |
| `dataTransacao` | `OccurredAtUtc` = that date at 00:00 in `America/Sao_Paulo` (`2026-03-10T00:00-03:00`), so the booking date is the same in the account's own calendar |
| `tipoOperacao` `C` / `D` | `Type` `deposit` / `withdrawal`, `Side` `Single` |
| `valor` | `Amount` (always positive), `AssetCode` `BRL` |
| configured account number | `ExternalAccountId` |
| everything else | `ProviderData` (schemas `inter.banking.statement.deposit` / `.withdrawal`, version 1) |

`ProviderData` keeps Inter's own fields (`tipoTransacao`, `tipoOperacao`,
`titulo`, `descricao`, `numeroDocumento`, `dataInclusao` as `inclusionDate`)
and the whole `detalhes` object verbatim, and adds:

- `counterparty`: `name`, `taxId` + `taxIdKind` (`CPF`/`CNPJ`) + `taxIdCountry`,
  `bank`, `branch`, `account`, for the payer of a credit or the payee of a debit;
- `pix`: `endToEndId`, `txId`, `key`;
- `transferHint: "investment"` on `INVESTIMENTO` entries (money moving between the
  account and its own investments, such as automatic CDB applications and
  redemptions), so the platform can treat them as transfers rather than income
  or expenses;
- `environment`: `sandbox` or `production`.

### How the counterparty is found

Inter's reference documents `detalhes` only through one Pix response sample
(`nomePagador`, `cpfCnpjPagador`, `nomeEmpresaPagador`, `nomeRecebedor`,
`cpfCnpjRecebedor`, `nomeEmpresaRecebedor`, `agenciaRecebedor`,
`contaBancariaRecebedor`, `chavePixRecebedor`, `endToEndId`, `txId`, `descricaoPix`,
`origemMovimentacao`, `codigoSolicitacao`, `tipoDetalhe`); its SDKs leave the
object untyped, and the boleto, TED and investment shapes are undocumented. The plugin therefore recognises fields by the words in their key
path instead of by a fixed list: `nome…` is a name (`nomeBanco…` and
`nomeEmpresa…`, which carries the institution such as "Banco Inter", are the bank), `cpf…`/`cnpj…` a tax id,
`agencia…`, `conta…`, `banco…`, `endToEnd…`, `txId`, `chave…`; `pagador`,
`remetente`, `origem` mark the payer and `recebedor`, `beneficiario`,
`favorecido`, `destinatario`, `destino` the payee (also as parent objects, such as
`recebedor.cpfCnpj`); `tipo…` keys describe a value and are skipped. Fields of the account holder's own side are ignored. A tax
id is only reported when its check digits are valid (the alphanumeric CNPJ of
2026 included), so a masked value such as `***.456.789-**` never becomes an
identifier. Nothing is lost when a key is not recognised: `detalhes` is always
kept whole. Run the sandbox probe (below) to see the key paths Inter returns.

## Credentials

Create an integration in Inter's Internet Banking: **Soluções para sua empresa
→ Nova Integração**, grant the statement and balance read permission (scope
`extrato.read`, the only scope this plugin requests), then download the key and certificate and
activate them. Each integration yields:

- a **client id** and **client secret**;
- a **certificate** (`.crt`) and **private key** (`.key`), used for mutual TLS on
  every call. A PFX/P12 bundle with a password works too.

The plugin contract carries three resolved credential strings per sync. Inter
uses them as:

| Contract field | Inter |
| --- | --- |
| `ApiKey` | client id |
| `ApiSecret` | client secret |
| `Passphrase` | a credential bundle: certificate and key, account number and environment |

Build the bundle with `InterCredentialBundle.CreateFromPem(...)` or
`CreateFromPfx(...)`. Both load the certificate first, so unusable material is
rejected before it is stored; `InterCredentialBundle.Inspect(...)` validates a
stored bundle and reports the certificate's subject, thumbprint and **expiry**
(`CertificateNotAfter`). Inter refuses expired certificates; the plugin fails a
sync with `Configuration` before calling Inter once the certificate has expired.
Store the bundle as a secret, exactly like the client secret.

The **account number** (digits including the check digit; dots, dashes and
leading zeros are dropped) is sent as `x-conta-corrente` on every banking call,
which Inter requires when one integration covers several accounts, and becomes
the import's `ExternalAccountId`.

**Environment**: `Sandbox` uses `https://cdpj-sandbox.partners.uatinter.co`
(fictitious data, credentials from a sandbox integration); `Production` uses
`https://cdpj.partners.bancointer.com.br`. Both addresses can be overridden in
`InterIntegrationOptions`.

## Syncing

- **Connection test.** A request with `ConnectionId <= 0` obtains a token and reads
  `GET /banking/v2/saldo`, importing nothing. The diagnostics say whether the
  balance was read and when the certificate expires; the balance itself is not
  reported.
- **Windows.** `GET /banking/v2/extrato/completo` accepts at most 90 days per
  request, so the sync range is read in consecutive windows of up to 90 calendar
  days, dated in Brazil's calendar and never beyond today.
- **Pagination.** Traditional pagination (`pagina`, `tamanhoPagina`, default page
  size 1,000). Inter serves at most 10,000 entries per window this way; a window
  that reports more is split in half until it fits. Scroll pagination is not used:
  Inter allows one active scroll per account, and it expires after six minutes.
- **Checkpoint.** The result carries the checkpoint `statement` = the last date
  read (`yyyy-MM-dd`). The next sync starts three days before it (overlap,
  configurable), or at the host's window start if that is earlier, so entries
  Inter books late are still picked up; the idempotency keys make re-reading
  harmless. A replay of an older window never moves the checkpoint back. With
  neither a checkpoint nor a window start (full-history backfill) it starts at
  `Defaults.SuggestedBackfillFromDate` (2025-01-01) and says so in the diagnostics
  (`startsAtBackfillAnchor`); to import earlier history, run the backfill with an
  explicit start date.
- **Failures.** Every expected problem returns `IntegrationSyncResult.Failure`:
  bad or missing credentials and certificates are `Configuration` or
  `Authentication`; a missing scope is `Permission`; persistent HTTP 429 is
  `RateLimited`; 5xx, timeouts and unreadable responses are `Upstream` or
  `Network`. An entry without `idTransacao`, a valid `dataTransacao`, `tipoOperacao`
  `C`/`D` or a numeric `valor` fails the sync rather than being dropped.
  Messages never contain credentials, and Inter's per-field `valor` echoes are
  not repeated.

## Limits and resilience

| Inter limit | How the plugin keeps to it |
| --- | --- |
| Token: 5 calls/minute, valid 60 minutes | Tokens are cached per connection and scope and renewed 60 s before expiry; concurrent callers share one request. Token calls are paced at 5 per minute per integration. |
| Statement and balance: 10 calls/minute | Banking calls are paced at 10 per minute per integration (client id + environment), shared by every connection using that integration. Callers wait for a slot rather than draw 429s. |
| HTTP 429 / 5xx | Retried up to 3 times with exponential back-off and jitter; `Retry-After` is honoured (capped at 90 s), and a 429 without it waits 30 s, then 60 s. TLS failures (a refused certificate) are not retried. |
| Timeouts | 30 s per attempt, 4 minutes per call including retries. |

The client certificate is loaded once per set of credentials and kept with its
`SocketsHttpHandler`; clients idle for two hours are disposed. On Windows the PEM
pair is loaded into a non-persisted key container, because SChannel cannot
present an ephemeral key as a TLS client certificate.

All limits and timeouts are in `InterIntegrationOptions`:

```csharp
builder.Services.AddInterIntegration(options => options.StatementOverlapDays = 5);
```

## Tests and fixtures

`tests/Providers/br/Inter` covers mapping (every `tipoTransacao` the feature
lists, both operation types), pagination, window splitting, token caching and
renewal, call pacing, `Retry-After`, error mapping and credential handling,
against an in-memory fake of the API. The fixtures are **written from synthetic
data** in the documented response shape; valid CPF/CNPJ numbers are generated at
run time, and client certificates are generated per test run.

`InterSandboxProbeTests` is an opt-in check against Inter's sandbox, skipped
unless these variables are set (CI never sets them):

```
INTER_SANDBOX_CLIENT_ID, INTER_SANDBOX_CLIENT_SECRET,
INTER_SANDBOX_CERTIFICATE_PEM, INTER_SANDBOX_KEY_PEM   (file paths),
INTER_SANDBOX_ACCOUNT, INTER_SANDBOX_KEY_PASSWORD     (optional)
```

It runs the connection test and a 90-day sync, and prints only the `detalhes`
key paths per `tipoTransacao`, never values, so the counterparty key table can be
checked against Inter without copying a response into this repository.

## Known gaps

- The `detalhes` keys are recognised by pattern, because Inter documents neither
  their names nor their shape. Confirm them with the sandbox probe when Inter's
  sandbox is available, and extend the word lists if a key is missed.
- `BrazilianTaxId` (CPF/CNPJ check digits) is internal to this provider. Move it to
  `src/Countries/br` when a second Brazilian provider needs it.
- Balances are read only by the connection test; webhooks (Pix, boletos) and the
  PDF statement export are not used.

## Reference

API facts come from Inter's developer portal:
[banking reference](https://developers.inter.co/references/banking) (Extrato,
Saldo) and [token reference](https://developers.inter.co/references/token). Inter
also publishes a C# SDK, [`inter-co/pj-sdk-csharp`](https://github.com/inter-co/pj-sdk-csharp).
That repository has no licence, so it was used only as a map of endpoints and
field names; **no code was copied from it**, and this package does not depend on it.

Apache-2.0. "Inter" and "Banco Inter" are trademarks of Banco Inter S.A.; this
package is not affiliated with or endorsed by Banco Inter.
