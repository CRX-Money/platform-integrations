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
(retrieved 2026-10-19 for the first 28 entries and again on 2026-10-20, when the
17 added entries were taken from it and the first 28 were re-checked against it).
Each website was opened on the institution's own site; Pluggy's
`institutionUrl` is never used, because it is usually a logo CDN, not a website.
An institution is listed only when both checks passed.

The list covers the business institutions the owner curated from Pluggy's Open
Finance coverage on 2026-10-19 (groups 1 to 4: large and traditional banks;
digital banks, fintechs and payments; credit cooperatives; investment platforms
and brokers), plus the earlier entries. Pluggy's small "Beta" banking-as-a-service
accounts are not covered yet.

| COMPE | Institution | Website |
| --- | --- | --- |
| 001 | Banco do Brasil | https://www.bb.com.br/ |
| 004 | Banco do Nordeste | https://www.bnb.gov.br/ |
| 021 | Banestes | https://www.banestes.com.br/ |
| 033 | Santander | https://www.santander.com.br/ |
| 041 | Banrisul | https://www.banrisul.com.br/ |
| 069 | Crefisa | https://www.crefisa.com.br/ |
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
| 329 | QI Tech | https://qitech.com.br/ |
| 336 | C6 Bank | https://www.c6bank.com.br/ |
| 341 | Itaú | https://www.itau.com.br/ |
| 352 | Toro | https://www.toroinvestimentos.com.br/ |
| 362 | Cielo | https://www.cielo.com.br/ |
| 380 | PicPay | https://picpay.com/ |
| 389 | Banco Mercantil | https://www.bancomercantil.com.br/ |
| 403 | Cora | https://www.cora.com.br/ |
| 413 | Banco BV | https://www.bv.com.br/ |
| 422 | Safra | https://www.safra.com.br/ |
| 468 | Porto Bank | https://www.portoseguro.com.br/ |
| 477 | Citi | https://www.citibank.com.br/ |
| 509 | Celcoin | https://www.celcoin.com.br/ |
| 517 | PagueVeloz | https://www.pagueveloz.com.br/ |
| 542 | InfinitePay | https://www.infinitepay.io/ |
| 565 | Ágora | https://www.agorainvestimentos.com.br/ |
| 594 | ASA | https://www.asa.com.br/ |
| 612 | Banco Guanabara | https://www.bancoguanabara.com.br/ |
| 623 | Banco Pan | https://www.bancopan.com.br/ |
| 637 | Sofisa | https://www.sofisa.com.br/ |
| 655 | Banco BV | https://www.bv.com.br/ |
| 707 | Daycoval | https://www.daycoval.com.br/ |
| 745 | Citi | https://www.citibank.com.br/ |
| 748 | Sicredi | https://www.sicredi.com.br/ |
| 756 | Sicoob | https://www.sicoob.com.br/ |

Notes:

- Credit cooperatives are listed by their system's own code: the cooperative
  banks Sicredi (748) and Sicoob (756), and Unicred's central, Unicred do Brasil
  (136). Each system groups many local cooperatives; a local cooperative that
  has a code of its own in the Banco Central file (for example a Sicoob
  cooperative) is not listed and gets no website. An account reported under the
  system's code gets the system's website.
- A website stands for one account operator, so two codes share a website only
  when both belong to that operator: Banco BV is Banco Votorantim (655) and
  Banco BV S.A. (413, which holds the Conta BV); Citi is Banco Citibank (745) and
  Citibank N.A. (477). https://www.citibank.com.br/ is Citi's Brazilian domain; it
  opens Citi Brasil's corporate portal. A test pins exactly these shared websites.
- Nubank accounts are held by Nu Pagamentos (260). InfinitePay accounts are held
  by CloudWalk (542). Porto Bank is Portoseg (468), the Porto Seguro company
  that issues its cards. Cielo is Cielo's payment institution (362).
- Toro is listed by its broker, which the Banco Central file now names Santander
  Corretora de Títulos e Valores Mobiliários (352); Toro's site names that
  company. Santander's other broker, Santander Corretora de Câmbio e Valores
  Mobiliários, has no code in the file and is not listed.
- XP is listed by its brokerage, XP Investimentos (102). Banco XP (348) is left
  out because no site of its own could be confirmed.
- Left out because the Banco Central file has no code for them (payment
  institutions, brokers and group companies often have only an ISPB, or none):
  Itaú BBA (accounts under Itaú's 341 get Itaú's website), Next (a Bradesco
  account under 237), Safra Financeira, RecargaPay and Santander Corretora de
  Câmbio e Valores Mobiliários.
- To add an entry, take the code, ISPB and legal name from the Banco Central file,
  open the institution's own site, and keep the list sorted by code. A test checks
  every entry's format.
