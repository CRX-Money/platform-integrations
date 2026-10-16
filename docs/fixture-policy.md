# Fixture policy

This repository is public and permanent. Test fixtures must never contain data
about a real person, company or account, so they are **written from synthetic
data, not redacted from real data**. When you need the shape of a real response,
copy the structure from the provider's public documentation or sandbox and fill
every value yourself.

## What the fixture guard checks

`build/FixtureGuard` scans every text file under `tests/` (it skips `bin/`,
`obj/` and `TestResults/`) and fails CI when a value looks real. Files are read
as UTF-8, or as UTF-16/UTF-32 when they start with a byte-order mark, falling
back to Latin-1 for legacy bank exports. It prints the file, line and rule, with
the value masked so that a real identifier is not republished in the CI log.

| Rule | Flags | Allowed synthetic forms |
| --- | --- | --- |
| FG001 | CPF (`000.000.000-00` or 11 bare digits) with **valid** check digits | Wrong check digits (`123.456.789-00`), or one repeated digit (`111.111.111-11`) |
| FG002 | CNPJ, numeric or the 2026 alphanumeric form, with **valid** check digits. Bare alphanumeric values are checked only on lines mentioning `cnpj`; 14-digit timestamps (`yyyyMMddHHmmss`) are ignored | Wrong check digits (`12.345.678/0001-00`), or one repeated digit (`00.000.000/0000-00`) |
| FG003 | E-mail addresses (possible Pix keys) | Reserved domains only: `example.com`, `example.org`, `example.net` and their subdomains, or any domain ending `.example`, `.test`, `.invalid`, `.localhost` |
| FG004 | Brazilian phone numbers (possible Pix keys): `+55` followed by area code and number, or `(DD) 9XXXX-XXXX`; on lines that mention `pix` or `chave`, also national mobiles such as `11 91234-5678` | An area code ending in 0 (`+55 10 91234-5678`, `(20) 3123-4567`): no Brazilian area code ends in 0 |
| FG005 | UUIDs on lines that mention `pix` or `chave` (random Pix keys, EVP) | First group all zeros: `00000000-0000-4000-8000-000000000001` |
| FG006 | Account numbers (`conta`, `contaCorrente`, `numeroConta`, `account`, `c/c`) within three lines of an agency (`agencia`, `agência`, `ag`, `branch`) | See "synthetic account numbers" below |
| FG007 | OFX `<ACCTID>` values | See "synthetic account numbers" below |

**Synthetic account numbers** contain a marker (`TEST`, `SYNTH`, `FAKE`,
`EXAMPLE`, `DEMO` or `XXXX`), or have fewer than five digits, or repeat one digit
(`99999999-9`), or are an ascending run (`12345678-9`, `234567`).

Names, addresses, amounts and free-text descriptions cannot be checked
mechanically. Use obviously invented values ("Empresa Exemplo Ltda", "Fulano de
Tal", "Rua Exemplo, 100") and reviewers will check them.

## When code needs a valid number

Some code under test validates check digits, so a fixture with a wrong check
digit would be rejected. Prefer generating the number in the test (compute the
check digits from a base such as `123456789`) instead of committing it. If a
fixture must contain a valid number, add it to `tests/fixture-allowlist.txt`:

```
# value                 # reason (required)
123.456.789-09          # textbook example CPF used by the parser's validation tests
```

Punctuation is ignored when matching numbers. Every allowlist change is reviewed
by a code owner; a value is only acceptable if it is a documented example or was
generated, never one copied from a real document.

## Binary files

The guard cannot read binary files (PDF, images, archives); any file with a NUL
byte and no UTF-16/UTF-32 byte-order mark counts as binary, so save UTF-16 text
with its byte-order mark. Binary files are listed as warnings and a maintainer
reviews them by hand. Prefer text fixtures; when a PDF is unavoidable, generate
it from synthetic text and say so in the pull request.

## Running it

```
dotnet run --project build/FixtureGuard -- tests
dotnet run --project build/FixtureGuard -- tests/Providers/br/Inter   # one folder
```

Exit code 0 means clean, 1 means findings, 2 means a usage error, a path that
does not exist, or an invalid allowlist.

## If real data was committed

Do not just delete it in a new commit: it stays in the history. Report it
privately as described in [SECURITY.md](../SECURITY.md) so maintainers can
rewrite the history and rotate anything affected.
