# Versioning and breaking-change policy

## Versions come from tags

Packages follow [Semantic Versioning 2.0](https://semver.org). Versions are
computed by [MinVer](https://github.com/adamralph/minver) from the nearest git
tag with the `v` prefix:

| Commit | Package version |
| --- | --- |
| tagged `v1.0.0-preview.1` | `1.0.0-preview.1` |
| 3 commits after that tag | `1.0.0-preview.1.3` (not published) |
| no tag reachable | `0.0.0-preview.0.<height>` (not published) |

The assembly version is `<major>.0.0.0`, so binding redirects are never needed
within a major version.

For now every package in the repository shares one version line and one tag. When
providers start to ship on their own schedules, each project will get its own
tag prefix (for example `abstractions-v`, `br-inter-v`); this document will say
so before it happens.

## The contract package

`CrxMoney.Integrations.Abstractions` is what every plugin compiles against and
what the CRX•Money platform loads plugins through. Its stability matters more
than any provider's.

### Pre-release (`1.0.0-preview.N`)

Previews may change the contract, including breaking changes, but each change
is listed in the release notes and the approved API file diff. The platform pins
an exact preview version.

### Stable (`1.0.0` and later)

Within a major version the contract only grows.

**Breaking (needs a new major version):**

- Removing or renaming a public type, member, parameter or enum value.
- Changing a member's type, a method signature, or a record's positional
  parameters (adding, removing or reordering them changes the constructor and
  `Deconstruct`, which breaks compiled callers even when the new parameter is
  optional).
- Adding an interface member without a default implementation.
- Changing the numeric value of an enum member (hosts persist them as integers).
- Changing documented behaviour hosts or plugins rely on: factory defaults in
  `IntegrationSyncResult.Success`/`Failure`, `IntegrationPluginDefaults.Default`,
  idempotency-key and checkpoint semantics.

**Additive (minor version):**

- New types, and new members on existing classes and records (add an
  `init` property in the record body rather than a positional parameter).
- New interface members **with** a default implementation.
- New enum members appended with new values. Plugins and hosts must treat unknown
  values as `Unknown`/`None` rather than failing.
- New optional companion interfaces (like `IIntegrationImportSchemaManifestProvider`).

**Patch:** documentation, bug fixes that do not change the public surface or
documented behaviour.

### How a breaking change happens

1. Mark the old member `[Obsolete("Use X instead; removed in 2.0.")]` in a minor
   release, with the replacement available in the same release.
2. Leave it for at least one minor release so the platform and third-party
   plugins can move.
3. Remove it in the next major release, with migration notes in the release.

### How the policy is enforced

- `PublicApiTests` compares the compiled surface with
  `tests/Abstractions/CrxMoney.Integrations.Abstractions.Tests/PublicApi/CrxMoney.Integrations.Abstractions.approved.txt`.
  Any change fails until the file is updated in the same pull request, so every
  contract change is visible in review. The pull request states the version
  impact (none, minor, major).
- Package validation (`EnablePackageValidation`) runs on every pack. After the
  first stable release, `PackageValidationBaselineVersion` is set to the last
  stable version so binary-breaking changes fail the build.
- `ContractBehaviourTests` pins the documented defaults and enum values.

## Provider packages

Providers follow SemVer for their own public types, but their real contract is
behaviour: which events they emit and how idempotency keys are built. Changing
the idempotency-key recipe of a released provider is breaking, because the
platform would import the same movement twice; it needs a major version and a
migration note.
