# Build

| Path | Purpose |
| --- | --- |
| `Common.props` | Shared MSBuild settings, imported by the root `Directory.Build.props`: .NET 10, nullable, warnings as errors, deterministic builds, package metadata (Apache-2.0, authors, repository), SourceLink, symbol packages, MinVer versioning. |
| `FixtureGuard/` | The CI check that fails on fixtures resembling real CPF/CNPJ, Pix keys, phone numbers, e-mail addresses or bank accounts. See [`docs/fixture-policy.md`](../docs/fixture-policy.md). |
| `ci/select-tests.sh` | Maps a change set to the test projects CI must run. |

Package versions are managed centrally in the root `Directory.Packages.props`.
