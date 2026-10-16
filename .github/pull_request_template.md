## What and why

<!-- What this changes and why. Link the issue: Fixes #123 -->

## Kind of change

- [ ] Provider (`src/Providers/<cc>/<Provider>`)
- [ ] Format (`src/Formats/<Format>`)
- [ ] Contract (`src/Abstractions`), see docs/versioning.md
- [ ] Build, CI or docs

## Sanitisation checklist

- [ ] Every fixture was **written from synthetic data**, not redacted from a real statement or API response.
- [ ] No real CPF/CNPJ, account or agency numbers, Pix keys, phone numbers, e-mail addresses, names, addresses or balances anywhere in the diff (code, fixtures, comments, commit messages).
- [ ] No credentials, tokens, certificates or private keys, not even expired or sandbox ones.
- [ ] Synthetic values use the allowed forms in docs/fixture-policy.md; any allowlist entry has a reason.
- [ ] Binary fixtures (PDF, images), if any, are listed here for manual review:
- [ ] `dotnet run --project build/FixtureGuard -- tests` passes locally.

## Tests

- [ ] New or changed behaviour has tests; `dotnet test --solution CrxMoney.Integrations.slnx` passes.
- [ ] Contract changes: `PublicApi/*.approved.txt` updated and the version impact is stated below.

<!-- Version impact for contract changes: none / minor (additive) / major (breaking, explain). -->
