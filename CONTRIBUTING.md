# Contributing

Thank you for helping CRX•Money read more banks and formats. This guide covers
adding a provider or a format, the tests we require, and the fixture rules that
keep real people's data out of a public repository.

By contributing you agree that your contribution is licensed under the
[Apache License 2.0](LICENSE), and you follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- Open an issue first for a new provider or format
  ([new provider request](../../issues/new?template=new-provider.yml)). It saves
  work if the provider is already planned or belongs in a different folder.
- Never use your own or anyone else's real statements, credentials or
  certificates in this repository, not even temporarily in a branch. Git history
  is permanent and the repository is public.

## Adding a provider

1. **Pick the folder.** `src/Providers/<cc>/<Provider>/`, where `<cc>` is the
   lowercase ISO 3166-1 alpha-2 code of the country whose regulator licenses the
   provider (`br`, `py`), or `global` for providers that work the same way across
   countries. `<Provider>` is the provider's short name in PascalCase (`Inter`).
2. **Create the project** `CrxMoney.Integrations.Providers.<Cc>.<Provider>.csproj`
   (`CrxMoney.Integrations.Providers.Br.Inter`, `CrxMoney.Integrations.Providers.Global.Binance`),
   referencing only `src/Abstractions/CrxMoney.Integrations.Abstractions`, shared
   helpers under `src/Countries/<cc>/` or `src/Formats/`, and well-maintained
   NuGet packages. Set `<IsPackable>true</IsPackable>` and a `README.md` packed
   with the project when it is ready to ship. Add it to `CrxMoney.Integrations.slnx`.
3. **Implement `IIntegrationPlugin`.** Follow the rules in the
   [contract README](src/Abstractions/CrxMoney.Integrations.Abstractions/README.md):
   deterministic idempotency keys, checkpoints for incremental syncs,
   `IntegrationSyncResult.Failure` for expected provider errors, and no secrets
   or personal data in logs, error messages or diagnostics.
4. **Write tests** in `tests/Providers/<cc>/<Provider>/CrxMoney.Integrations.Providers.<Cc>.<Provider>.Tests/`.
   CI refuses a provider change when that folder has no `*.Tests.csproj`.
5. **Document** the provider in its `README.md`: what it imports, the API or file
   version it targets, required credentials and scopes (described, never
   included), rate limits and known gaps.

## Adding a format

Formats (OFX, CNAB, CSV dialects) live in `src/Formats/<Format>/` with tests in
`tests/Formats/<Format>/`. A format adapter parses files into contract types and
knows nothing about any one bank; bank-specific quirks belong in the provider
that uses the format.

## Tests we require

- Mapping tests from sanitised fixtures to `IntegrationImportEvent`s, including
  idempotency keys (same input, same keys) and checkpoint round-trips.
- Failure mapping: authentication, rate limiting and upstream errors become the
  right `IntegrationErrorKind`.
- No test may call a real provider. Use recorded, sanitised responses and a fake
  `HttpMessageHandler`. Sandbox calls, if any, belong in an opt-in test category
  that CI does not run.
- `dotnet build` must stay warning-free: warnings are errors in this repository.

## Fixtures: sanitised by construction

Fixtures are written from synthetic data, not redacted from real data. Redaction
misses things; construction does not. The rules, the allowed synthetic forms
for CPF, CNPJ, Pix keys, phone numbers, e-mail addresses and account numbers,
and the allowlist process are in [`docs/fixture-policy.md`](docs/fixture-policy.md).

CI runs the fixture guard on everything under `tests/` and fails the build when
something looks real. Run it locally before you push:

```
dotnet run --project build/FixtureGuard -- tests
```

Prefer text fixtures (JSON, OFX, CSV). The guard cannot read binary files such
as PDFs; a maintainer must review those by hand, so expect a slower review.

## Changing the contract

`CrxMoney.Integrations.Abstractions` is a published package the platform depends
on. Any change to its public surface fails `PublicApiTests` until you update
`tests/Abstractions/CrxMoney.Integrations.Abstractions.Tests/PublicApi/CrxMoney.Integrations.Abstractions.approved.txt`
in the same pull request (the test writes a `.received.txt` next to it for you to
review and rename). Read [`docs/versioning.md`](docs/versioning.md) first: most
changes need to be additive.

## Pull requests

- Keep one provider, format or concern per pull request.
- Fill in the pull request template, including the sanitisation checklist.
- Commits: imperative subject lines; reference the GitHub issue (`#12`) when there
  is one.
- A code owner reviews and merges. CI (`ci`) must pass.
