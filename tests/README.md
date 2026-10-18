# Tests

`tests/` mirrors `src/`: a provider in `src/Providers/br/Inter` is tested by
`tests/Providers/br/Inter/CrxMoney.Integrations.Providers.Br.Inter.Tests`. CI uses that
mapping to run only the tests a change affects (`build/ci/select-tests.sh`).

Every project here is an xUnit v3 project on Microsoft.Testing.Platform
(`tests/Directory.Build.props`):

```
dotnet test --solution CrxMoney.Integrations.slnx
dotnet test --project tests/Abstractions/CrxMoney.Integrations.Abstractions.Tests
```

Everything under `tests/` is scanned by the fixture guard, including test code.
Follow [`docs/fixture-policy.md`](../docs/fixture-policy.md); exceptions go in
[`fixture-allowlist.txt`](fixture-allowlist.txt) with a reason.
