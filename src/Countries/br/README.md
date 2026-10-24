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
| `BrazilianInstitutionMarks` | The marks (logos) that identify those institutions in a connect list: `TryOpen(key, out stream, out mediaType)`; `BrazilianInstitution.MarkResourceName` names the embedded resource (below). `TryOpenConnector(connectorId, ...)` opens a business connector's own mark. |
| `BrazilianInstitutionConnectors` | The aggregator's connectors per institution: the main one (its icon is the institution's mark) and the business (PJ) variant (Inter Empresas next to Inter): `Find(connectorId)`, `For(compeOrIspb, isBusiness)`. |

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
and brokers), plus the earlier entries. The table's Group column gives each
curated institution's group (– for the earlier entries outside the curation), the
same grouping as the test `Every_curated_business_institution_is_listed`. Pluggy's
small "Beta" banking-as-a-service accounts are not covered yet.

| COMPE | Institution | Website | Group |
| --- | --- | --- | --- |
| 001 | Banco do Brasil | https://www.bb.com.br/ | 1 |
| 004 | Banco do Nordeste | https://www.bnb.gov.br/ | 1 |
| 021 | Banestes | https://www.banestes.com.br/ | – |
| 033 | Santander | https://www.santander.com.br/ | 1 |
| 041 | Banrisul | https://www.banrisul.com.br/ | 1 |
| 069 | Crefisa | https://www.crefisa.com.br/ | 1 |
| 070 | BRB | https://www.brb.com.br/ | 1 |
| 077 | Inter | https://inter.co/ | 2 |
| 102 | XP Investimentos | https://www.xpi.com.br/ | 4 |
| 104 | Caixa | https://www.caixa.gov.br/ | 1 |
| 121 | Agibank | https://agibank.com.br/ | – |
| 136 | Unicred | https://www.unicred.com.br/ | 3 |
| 197 | Stone | https://www.stone.com.br/ | 2 |
| 208 | BTG Pactual | https://www.btgpactual.com/ | 4 |
| 212 | Banco Original | https://www.original.com.br/ | – |
| 237 | Bradesco | https://banco.bradesco/ | 1 |
| 260 | Nubank | https://nubank.com.br/ | 2 |
| 290 | PagBank | https://pagbank.com.br/ | 2 |
| 318 | Banco BMG | https://www.bancobmg.com.br/ | 1 |
| 323 | Mercado Pago | https://www.mercadopago.com.br/ | 2 |
| 329 | QI Tech | https://qitech.com.br/ | 2 |
| 336 | C6 Bank | https://www.c6bank.com.br/ | 2 |
| 341 | Itaú | https://www.itau.com.br/ | 1 |
| 352 | Toro (Santander Corretora) | https://www.toroinvestimentos.com.br/ | 4 |
| 362 | Cielo | https://www.cielo.com.br/ | 2 |
| 380 | PicPay | https://picpay.com/ | 2 |
| 389 | Banco Mercantil | https://www.bancomercantil.com.br/ | 1 |
| 403 | Cora | https://www.cora.com.br/ | – |
| 413 | Banco BV | https://www.bv.com.br/ | 1 |
| 422 | Safra | https://www.safra.com.br/ | 1 |
| 468 | Porto Bank | https://www.portoseguro.com.br/ | 1 |
| 477 | Citi | https://www.citibank.com.br/ | 1 |
| 509 | Celcoin | https://www.celcoin.com.br/ | 2 |
| 517 | PagueVeloz | https://www.pagueveloz.com.br/ | 2 |
| 542 | InfinitePay | https://www.infinitepay.io/ | 2 |
| 565 | Ágora | https://www.agorainvestimentos.com.br/ | 4 |
| 594 | ASA | https://www.asa.com.br/ | 2 |
| 612 | Banco Guanabara | https://www.bancoguanabara.com.br/ | 1 |
| 623 | Banco Pan | https://www.bancopan.com.br/ | – |
| 637 | Sofisa | https://www.sofisa.com.br/ | 1 |
| 655 | Banco BV | https://www.bv.com.br/ | 1 |
| 707 | Daycoval | https://www.daycoval.com.br/ | – |
| 745 | Citi | https://www.citibank.com.br/ | 1 |
| 748 | Sicredi | https://www.sicredi.com.br/ | 3 |
| 756 | Sicoob | https://www.sicoob.com.br/ | 3 |

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
  Citibank N.A. (477). A test pins exactly these shared websites.
- Citi's website is its Brazilian domain, https://www.citibank.com.br/, kept as
  the canonical website because it is the address users recognize, although it
  redirects (301) to https://corporateportal.brazil.citibank.com/.
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
- Group finance, broker and SCD companies are not listed, on purpose, although
  the Banco Central file gives them codes: Celcoin SCD (668), QI CTVM (363),
  Stone CFI (672), Mercantil Financeira (567), and Bradesco's Financiamentos
  (394), BBI (036) and BERJ (122). The business accounts the list is for are
  held by the listed institution (Celcoin's by its payment institution, 509;
  QI's by its SCD, 329), so these codes are not expected on an
  account, and each would add a shared website. An account that does carry one
  gets no website, so the host leaves its operator unmatched instead of guessing.
- To add an entry, take the code, ISPB and legal name from the Banco Central file,
  open the institution's own site, and keep the list sorted by code. A test checks
  every entry's format.

## Institution marks

`Marks/` holds the mark (logo) of each listed institution, embedded in the
package next to `marks-manifest.json`. A connect list draws the mark so a person
sees which bank is which; an institution without one is drawn as a monogram from
its name and colours.

```csharp
var institution = BrazilianInstitutions.Find("077");
if (BrazilianInstitutionMarks.TryOpen(institution!.Ispb, out var stream, out var mediaType))
{
    using (stream) { /* image/svg+xml or image/png */ }
}
```

- The key is the institution's ISPB (`00416968`); a COMPE code is accepted too.
  `BrazilianInstitution.MarkResourceName` is the resource name, or `null` for a
  monogram. The files are `{ispb}.svg` (preferred) or `{ispb}.png`, as downloaded
  and then normalised (below), at most 256 KB each.
- SVG files must carry no script, event handler, `foreignObject` or external
  reference. A host should still serve them as images (never inline) with a
  sandboxing `Content-Security-Policy`.

### Where the marks come from

Each file is the icon of the matching Brazilian connector of [Pluggy](https://pluggy.ai), the aggregator the
Open Finance plugin uses, downloaded from Pluggy's public CDN (`cdn.pluggy.ai/assets/connector-icons/`). No
key is needed: the connector list (id, name, type, icon address) is the public status page
<https://status.pluggy.ai/>, which is also where each mapping is recorded as coming from.
`Marks/marks-manifest.json` records, per file: the institution (ISPB and COMPE), the connector id and name,
how it matched (`matchedBy`), the page the mapping came from (`mappedFrom`), the file's source URL, the media
type, the size and SHA-256 of the file as shipped, how it was normalised (`normalised`, below) and the UTC capture
time. Its `monogram` list names every institution without a
mark and why. A test checks that the manifest, the embedded files and the institution list agree, so an
institution is never in both lists, and never in neither.

Its `connectors` list names the business (PJ) connector next to an institution's main one (Inter Empresas, 225,
next to Inter, 215): a connector typed business whose name starts with the main connector's or the institution's
name; one whose name says it serves companies (Empresas, Negócios, PJ, Business) wins over another business product
of the bank (Itaú Empresas over Itaú BBA, Bradesco Empresas over Bradesco API), then the shortest name. A main
connector named for companies (Citi's only connector, Citi Empresas) counts as a business one. A mark whose bytes did
not change keeps the capture time the manifest already records. Each entry has the connector id and name, `kind: business`, the main connector's id, the institution, and
either its own mark in `Marks/connectors/{connectorId}.svg` (with media type, size, SHA-256 and source URL) or the
`reason` it has none. A plugin uses this list when the aggregator does not say which connector an item reads: through
Meu Pluggy every item names the MeuPluggy connector, so the bank of its accounts and whether they belong to a company
(a CNPJ) pick it (`BrazilianInstitutionConnectors.For`).

The status page has no institution websites, so institutions are matched to connectors by name: exactly
(without accents, case or punctuation), ignoring spaces (`BTGPactual`), or as the start of the connector's name
(`Toro` is `Toro Investimentos`); a personal connector wins over a business one. A short reviewed alias table in
the tool (`ConnectorMatcher.Aliases`) pins the few that name rules cannot find or would get wrong (Caixa, Agibank,
QI Tech, Banco BV, Sofisa). With credentials, `--source api` reads `GET /connectors` instead, which also
carries each connector's `institutionUrl` and so matches by website first.

`tools/InstitutionMarks` refreshes all of it and prints a match report, with no credentials needed:

```
dotnet run --project tools/InstitutionMarks                    # public listing, or the API when credentials are set
dotnet run --project tools/InstitutionMarks -- --dry-run       # report only, writes nothing
dotnet run --project tools/InstitutionMarks -- --source api    # needs CRX_PLUGGY_CLIENT_ID and CRX_PLUGGY_CLIENT_SECRET
dotnet user-secrets set CRX_PLUGGY_CLIENT_ID "..." --project tools/InstitutionMarks   # instead of environment variables
```

A downloaded image is kept only when it is SVG or PNG, at most 256 KB, and (for SVG) free of script,
event handlers, `foreignObject` and external references; otherwise the institution stays a monogram and the
report says why.

### Normalisation of the canvas

The icons come with whatever canvas their makers drew: some sit in a white or coloured square with wide
margins, so on a card they look smaller than the others and show a white box. After the download the tool
cleans the **canvas only**; it never changes a shape, a path, a fill, a gradient or a text:

1. **Background.** A full-canvas background (the bottom layer, a `<rect>` or `<path>` that paints at least 90% of
   the canvas in one flat colour, white or semi-transparent white included) is dropped when the mark still
   reads on a white card **and** on a dark one without it (at least 70% of what is drawn has a contrast of 1.6
   or more against each). A coloured tile that **is** the mark (Inter's orange square, Nubank's purple, a white
   glyph on a brand colour) stays: without it the glyph reads on neither. A gradient tile is never dropped
   silently.
2. **viewBox.** The `viewBox` is tightened to the bounds of what is drawn plus a 2% margin (of the longer side),
   keeping the mark's own aspect ratio and never reaching outside the old viewBox, so nothing that was clipped
   becomes visible. The bounds come from drawing the SVG (Svg.Skia, SkiaSharp) and taking the non-transparent
   pixels, mapped back to viewBox units; the file's `width` and `height` follow the new viewBox. A host that
   draws a mark contained in a square box then sees every mark fill it on its longer side.
3. **Manual review.** What the tool cannot decide safely is left exactly as downloaded and listed in
   `tools/InstitutionMarks/review/br-marks-normalisation.md`: several candidate backgrounds, a tile that is not
   one flat colour but whose mark reads without it, a white tile the mark cannot do without (it reads on white
   only), text (its bounds depend on the machine's fonts), or a result that does not draw what the source drew.

The manifest records, per mark, `normalised`: `viewBoxBefore`, `viewBoxAfter`, `backgroundRemoved`, `method`
(`viewbox-tightened`, `background-removed+viewbox-tightened`, `unchanged` when the file is already tight or its
tile is the mark, `manual-review`, `not-applicable` for a PNG), the size and SHA-256 of the file as downloaded
(`originalBytes`, `originalSha256`), the colour of a dropped tile (`backgroundFill`) and a `note`. A test checks
the record against the shipped files. `tools/InstitutionMarks/review/` also holds
`br-marks-contact-sheet.png`, every mark before and after in 44 px boxes on a light and a dark card, so the
optical size can be judged in one look; the tool rewrites both files on every run (`--review <dir>` moves them,
`--no-normalise` ships the files as downloaded). Marks the contact sheet shows reading on one card only (a
dark mark on the dark card) are listed in the report; the marks are the institutions' and are not recoloured.

### Trademarks and removal

The marks are trademarks of their owners and are **not** covered by this
repository's Apache-2.0 licence. They are included only to identify the
institution a person is choosing, which is how every aggregator's connect list
uses them; nothing here suggests that an institution endorses CRX•Money or this
package. If an institution would like its mark removed, open an issue or write to
`admin@crx.money`: the file is removed (the institution goes back to the monogram
list) in the next release.
