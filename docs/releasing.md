# Releasing packages

`.github/workflows/publish.yml` publishes every packable project to nuget.org
when a `v*` tag is pushed. It refuses to run without the `NUGET_API_KEY` secret,
refuses tags that are not on `main`, runs the fixture guard, build and all tests,
checks that the package versions equal the tag, uploads the packages as a
workflow artifact, and pushes the `.nupkg` and `.snupkg` files.

## One-time setup (maintainers)

1. **nuget.org account.** Sign in at <https://www.nuget.org> with the CRX•Money
   maintainer account and create (or use) the organisation that will own the
   packages, with `admin@crx.money` as contact.
2. **Reserve the prefix (recommended).** Ask nuget.org to reserve the `CrxMoney.`
   ID prefix for that organisation, following
   <https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation> (e-mail
   `account@nuget.org`). Reserved packages show a verified badge and nobody else
   can publish `CrxMoney.*` IDs. Publishing works before the reservation is
   granted.
3. **API key.** nuget.org → API Keys → Create: owner = the organisation, scope
   **Push new packages and package versions**, glob pattern `CrxMoney.*`, expiry
   365 days. Copy it once.
4. **GitHub secret.** <https://github.com/CRX-Money/platform-integrations/settings/secrets/actions>
   → New repository secret → name `NUGET_API_KEY`, value = the key. Record the
   expiry date and rotate the key before it lapses.

Later improvement: nuget.org [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
lets the workflow obtain a short-lived key through GitHub OIDC instead of a stored
secret. Switch to it once the package exists, then delete the secret.

## Cutting a release

From an up-to-date `main` that has passed CI:

```
git checkout main
git pull
git tag -a v1.0.0-preview.1 -m "CrxMoney.Integrations.Abstractions 1.0.0-preview.1"
git push origin v1.0.0-preview.1
```

Then watch the **Publish** workflow run. nuget.org validates and indexes new
packages within a few minutes; the package page is
<https://www.nuget.org/packages/CrxMoney.Integrations.Abstractions>.

If the run fails before the push step, fix the cause and re-run the workflow for
the same tag. If a version reached nuget.org and is wrong, do not delete or
re-tag it: unlist it on nuget.org and release the next version.

## After a release

- Write release notes (GitHub → Releases → draft from the tag), listing contract
  changes and their version impact.
- The CRX•Money platform moves to the new version in its own repository by
  changing the pinned `PackageReference` version.
