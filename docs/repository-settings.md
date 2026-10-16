# Repository settings

Settings that live in GitHub rather than in files. Only an organisation owner
can change them; this page records what they should be so they can be checked
and restored.

## Ruleset for `main`

`main` is protected by a repository ruleset: changes arrive only through pull
requests approved by a code owner, with the `ci` check green; no force-pushes,
no deletion.

The ruleset is in [`rulesets/main.json`](rulesets/main.json). To apply it:

**Option A, import in the browser.**
<https://github.com/CRX-Money/platform-integrations/settings/rules> → **New
ruleset** → **Import a ruleset** → choose `docs/rulesets/main.json` → review →
**Create**.

**Option B, GitHub CLI as an owner.**

```
gh api --method POST repos/CRX-Money/platform-integrations/rulesets --input docs/rulesets/main.json
```

What it enforces:

| Rule | Setting |
| --- | --- |
| Target | Default branch (`main`) |
| Restrict deletions | on |
| Block force pushes | on |
| Require a pull request | 1 approval, code owner review required, stale approvals dismissed on new pushes, conversations resolved |
| Required status checks | `ci` from GitHub Actions |
| Bypass | Organisation owners, **in pull requests only** (never direct pushes) |

Why the owner bypass: GitHub never lets authors approve their own pull requests,
and the only code owner today is `@crx-lc`. Without it, a pull request the
maintainer opens could never merge. The bypass is limited to pull requests, is
recorded in the audit log, and should be used only for the maintainer's own pull
requests once `ci` is green. Pull requests from automation accounts and external
contributors always need the code owner's approval. Remove the bypass when a
second human maintainer joins `CODEOWNERS`.

The `ci` check is the job in `.github/workflows/ci.yml`. It always reports (it
narrows its own scope instead of being skipped by path filters), so a docs-only
pull request can still merge.

## Release tags (optional)

Pushing a `v*` tag publishes to nuget.org. To keep releases a maintainer
decision, add a tag ruleset: target tags matching `v*`, rules **Restrict
creations**, **Restrict updates** and **Restrict deletions**, bypass list
**Organization admin**. Automation accounts then cannot publish; maintainers tag
releases as described in [releasing.md](releasing.md).

## Other settings

- **Actions → General:** allow GitHub-created and verified-creator actions only;
  workflow permissions **Read repository contents**; do not allow Actions to
  approve pull requests.
- **Secrets → Actions:** `NUGET_API_KEY` (see [releasing.md](releasing.md)).
- **General → Pull requests:** allow squash merging; automatically delete head
  branches.
- **Code security:** enable Dependabot alerts, Dependabot security updates,
  secret scanning and push protection.
- **Organisation:** two-factor authentication required, base permission Read,
  repository creation by owners only, the `Automation` team with Write on this
  repository only and never listed in `CODEOWNERS`.
