# Releasing packages

`.github/workflows/publish.yml` publishes every packable project to nuget.org
when a `v*` tag is pushed. It runs two jobs:

- **build** (read-only): refuses tags that are not on `main`, runs the fixture
  guard, build and all tests, checks that the package versions equal the tag,
  and uploads the packages as a workflow artifact.
- **publish**: downloads that artifact and pushes the `.nupkg` and `.snupkg`
  files. It checks out and runs no repository code, and its actions are pinned
  to commit SHAs.

Publishing authenticates with nuget.org [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing):
only the publish job may ask GitHub for a short-lived OIDC token
(`id-token: write`), the `NuGet/login` action exchanges it for a nuget.org API
key that is valid for one hour, and the push uses that key. No API key is stored
in GitHub, so there is no secret to create, rotate or leak. Keeping the token
permission out of the build job means restore, build and test code (including
third-party packages) can never mint a publishing token.

## One-time setup (maintainers)

1. **nuget.org organisation.** The packages are owned by the nuget.org
   organisation `crx-money` (administrator `crx-lc`, contact `admin@crx.money`).
2. **Trusted Publishing policy.** Signed in to <https://www.nuget.org> as
   `crx-lc`: user menu → **Trusted Publishing** → add a policy:

   | Field | Value |
   | --- | --- |
   | Package owner | `crx-money` |
   | Repository owner | `CRX-Money` |
   | Repository | `platform-integrations` |
   | Workflow file | `publish.yml` (file name only, without `.github/workflows/`) |
   | Environment | empty (the workflow uses no GitHub environment) |
   | Scopes (if offered) | push new packages and new versions, glob `CrxMoney.*` |

   The values are case-insensitive. Renaming `publish.yml`, the repository or
   the organisation breaks the match until the policy is updated.
3. **Policy activation window.** A new policy may start as *temporarily active*
   for 7 days, because nuget.org only learns the repository and owner IDs from
   the first successful publish. Tag the first release within that window. If
   it lapses, restart the 7-day window on the policy page (allowed at any time)
   and re-run the failed publish job. After the first successful publish the
   policy becomes permanently active and is locked to this repository.
4. **The `user` in the workflow.** `publish.yml` passes `user: crx-lc` to
   `NuGet/login`. nuget.org matches the token against the policies **created
   by** that account, so it must be the account that created the policy, not
   the `crx-money` organisation. If another maintainer recreates the policy,
   change `user` to their nuget.org profile name. That account must stay a
   member of `crx-money`; if it leaves, the policy becomes inactive.
5. **Reserve the prefix (recommended).** Ask nuget.org to reserve the `CrxMoney.`
   ID prefix for `crx-money`, following
   <https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation> (e-mail
   `account@nuget.org`). Reserved packages show a verified badge and nobody else
   can publish `CrxMoney.*` IDs. Publishing works before the reservation is
   granted.

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

If the **build** job fails, fix the cause on `main` and release under a new tag.
If only the **publish** job fails (for example a Trusted Publishing policy
mismatch or a lapsed activation window), fix the policy on nuget.org and use
**Re-run failed jobs**: it retries the push with the packages already built. If a
version reached nuget.org and is wrong, do not delete or re-tag it: unlist it on
nuget.org and release the next version.

## After a release

- Write release notes (GitHub → Releases → draft from the tag), listing contract
  changes and their version impact.
- The CRX•Money platform moves to the new version in its own repository by
  changing the pinned `PackageReference` version.
