# CrxMoney.Integrations.Countries.Br

The country package for Brazil: jurisdiction rules shared by the Brazilian plugins under
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
| `BrazilianInstitutions` | The account operators: COMPE code to display name and official website (below). |

Pix key types and ISPB formatting will be added here when a provider needs them.

The Open Finance Brasil model that used to live here (`CrxMoney.Integrations.Countries.Br.OpenFinance`)
moved to its plugin, [`CrxMoney.Integrations.Providers.Br.OpenFinance`](../../Providers/br/OpenFinance/README.md):
it is the rules of one provider, not of the country.

## Account operators (institution list)

`Data/br-institutions.json` ships inside the package as an embedded
resource and is read through `BrazilianInstitutions.Find(compe)` /
`WebsiteOf(compe)` (namespace `CrxMoney.Integrations.Countries.Br`). Each entry holds `compe`, `ispb`, `name` (display name),
`legalName` and `website` (a canonical `https://host/` URL). COMPE codes, ISPB
and legal names come from the Banco Central do Brasil's list of STR participants,
[ParticipantesSTR.csv](https://www.bcb.gov.br/content/estabilidadefinanceira/str1/ParticipantesSTR.csv)
(retrieved 2026-10-19). Each website was opened on the institution's own site.
An institution is listed only when both checks passed.

| COMPE | Institution | Website |
| --- | --- | --- |
| 001 | Banco do Brasil | https://www.bb.com.br/ |
| 021 | Banestes | https://www.banestes.com.br/ |
| 033 | Santander | https://www.santander.com.br/ |
| 041 | Banrisul | https://www.banrisul.com.br/ |
| 070 | BRB | https://www.brb.com.br/ |
| 077 | Inter | https://inter.co/ |
| 102 | XP Investimentos | https://www.xpi.com.br/ |
| 104 | Caixa | https://www.caixa.gov.br/ |
| 121 | Agibank | https://agibank.com.br/ |
| 136 | Unicred | https://www.unicred.com.br/ |
| 197 | Stone | https://www.stone.com.br/ |
| 208 | BTG Pactual | https://www.btgpactual.com/ |
| 212 | Banco Original | https://www.original.com.br/ |
| 237 | Bradesco | https://banco.bradesco/ |
| 260 | Nubank | https://nubank.com.br/ |
| 290 | PagBank | https://pagbank.com.br/ |
| 318 | Banco BMG | https://www.bancobmg.com.br/ |
| 323 | Mercado Pago | https://www.mercadopago.com.br/ |
| 336 | C6 Bank | https://www.c6bank.com.br/ |
| 341 | Itaú | https://www.itau.com.br/ |
| 380 | PicPay | https://picpay.com/ |
| 403 | Cora | https://www.cora.com.br/ |
| 422 | Safra | https://www.safra.com.br/ |
| 623 | Banco Pan | https://www.bancopan.com.br/ |
| 637 | Sofisa | https://www.sofisa.com.br/ |
| 707 | Daycoval | https://www.daycoval.com.br/ |
| 748 | Sicredi | https://www.sicredi.com.br/ |
| 756 | Sicoob | https://www.sicoob.com.br/ |

Notes:

- Sicredi (748) and Sicoob (756) are the cooperative banks; a local
  cooperative's own code is not listed and gets no website.
- Nubank accounts are held by Nu Pagamentos (260).
- XP is listed by its brokerage, XP Investimentos (102). Banco XP (348) is left
  out because no site of its own could be confirmed.
- To add an entry, take the code, ISPB and legal name from the Banco Central file,
  open the institution's own site, and keep the list sorted by code. A test checks
  every entry's format.
