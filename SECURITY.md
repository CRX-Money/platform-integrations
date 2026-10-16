# Security policy

## Reporting a vulnerability

Please report security problems privately to **admin@crx.money**. Do not open a
public issue, pull request or discussion.

Include what you found, how to reproduce it, and the affected package and
version. If the problem involves real credentials or personal data, describe it
without sending the data itself.

We acknowledge reports within five business days, keep you informed while we
fix the problem, and credit you in the release notes if you wish. Please give us
a reasonable time to release a fix before disclosing the issue publicly.

## Leaked data

If you find real personal or banking data in this repository (a real CPF, CNPJ,
account number, Pix key, credential or certificate), report it the same way.
Maintainers will remove it from the history, rotate anything affected and review
how it got past the [fixture guard](docs/fixture-policy.md).

## Supported versions

Security fixes go into the latest released version of each package. Preview
versions are superseded by the next preview or stable release.
