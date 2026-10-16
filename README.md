# CRX•Money Platform integrations

Open-source provider plugins and file-format adapters for the
[CRX•Money](https://crx.money) financial-operations platform, organised by
country.

CRX•Money keeps a workspace's financial truth: it imports bank statements and
exchange activity, matches them to documents, and posts them to a journal. This
repository holds the part that talks to the outside world: one plugin per bank,
exchange or statement format, each implementing a small public contract. The
platform itself is not open source; the contract and the adapters are, so that
banks, contributors and auditors can see exactly how data is read.

## Layout

```
src/
  Abstractions/          CrxMoney.Integrations.Abstractions: the plugin contract (NuGet package)
  Formats/Ofx/           File-format adapters shared by many banks (OFX first)
  Countries/br/          Shared helpers for one country (tax ids, Pix, bank codes)
  Providers/
    br/                  Providers operating in Brazil, one folder each (e.g. br/Inter)
    py/                  Providers operating in Paraguay
    global/              Providers not tied to one country (e.g. exchanges)
tests/                   Mirrors src/; sanitised fixtures only
build/                   Shared MSBuild settings, CI scripts, the fixture guard
docs/                    Policies: versioning, releasing, fixtures, repository settings
```

Countries use lowercase [ISO 3166-1 alpha-2](https://www.iso.org/iso-3166-country-codes.html)
codes. A provider lives under the country whose regulator licenses it; providers
that serve many countries the same way go under `global/`.

## How a provider relates to the platform

```
 bank / exchange API, statement file
              │
   src/Providers/<cc>/<Provider>   implements IIntegrationPlugin
              │
   CrxMoney.Integrations.Abstractions   (this repo, NuGet)
              │
   CRX•Money platform (private)   schedules syncs, resolves credentials,
                                  stores checkpoints, imports events
```

A plugin receives an `IntegrationSyncRequest` (connection, window, previous
checkpoints, credentials the platform has already resolved) and returns an
`IntegrationSyncResult` with canonical, idempotent `IntegrationImportEvent`s.
Plugins never touch the platform's database, secret store or tenant model; that
boundary is what makes them safe to publish. The contract is documented in
[`src/Abstractions/CrxMoney.Integrations.Abstractions/README.md`](src/Abstractions/CrxMoney.Integrations.Abstractions/README.md).

## Adding a bank

1. Open a [new provider request](../../issues/new?template=new-provider.yml) so
   maintainers can confirm the scope and the country folder.
2. Create `src/Providers/<cc>/<Provider>/CrxMoney.Integrations.<Cc>.<Provider>.csproj`
   referencing the Abstractions project, and a matching test project under
   `tests/Providers/<cc>/<Provider>/`.
3. Build fixtures from synthetic data only (see [`docs/fixture-policy.md`](docs/fixture-policy.md)).
4. Open a pull request; CI builds everything and runs your provider's tests.

[`CONTRIBUTING.md`](CONTRIBUTING.md) has the full checklist.

## Building

Requires the .NET 10 SDK (see `global.json`).

```
dotnet build CrxMoney.Integrations.slnx
dotnet test --solution CrxMoney.Integrations.slnx
dotnet run --project build/FixtureGuard -- tests
```

## Packages and versions

Packages are published to nuget.org from git tags (`v1.0.0-preview.1`), with
SourceLink and symbol packages. Versioning and the breaking-change policy:
[`docs/versioning.md`](docs/versioning.md). Releasing: [`docs/releasing.md`](docs/releasing.md).

## Issues and planning

GitHub issues are welcome for bugs and provider requests. The maintainers plan
work in their own tracker and mirror accepted GitHub issues there by hand, so an
issue may be closed with a link once it is scheduled or delivered.

Security problems: please do not open an issue; see [`SECURITY.md`](SECURITY.md).

## Licence and brand

The code in this repository is licensed under the [Apache License 2.0](LICENSE).

"CRX•Money" and its logo identify the CRX•Money platform. As section 6 of the
licence says, the licence grants no rights to them. Write the name as
**CRX•Money** (with the bullet) when referring to the platform, and do not use it
or the logo to suggest that a fork or derived work is endorsed by CRX•Money. This
repository contains no logo files and its packages carry no icon; approved brand
assets are published only through the asset section (§18) of the
[brand guidelines](https://crx.money/brand), and should be taken from there,
never copied from this repository, a screenshot or a slide.
