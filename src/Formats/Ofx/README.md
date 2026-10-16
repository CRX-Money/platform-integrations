# Formats: OFX

Adapter for Open Financial Exchange statement files (OFX 1.x SGML and 2.x XML),
the export most Brazilian banks offer when they have no API (for example Nubank
PJ and C6).

Planned scope: parse `STMTRS`/`CCSTMTRS` statements into contract types, keep the
`FITID` as the external id, and expose bank-specific quirks (encoding, missing
closing tags, timezone suffixes such as `[-3:BRT]`) as options rather than
special cases.

```
src/Formats/Ofx/CrxMoney.Integrations.Formats.Ofx.csproj
tests/Formats/Ofx/CrxMoney.Integrations.Formats.Ofx.Tests/
```

OFX fixtures are compared byte for byte (`.gitattributes` keeps their line
endings). `<ACCTID>` values must be synthetic; see
[`docs/fixture-policy.md`](../../../docs/fixture-policy.md).
