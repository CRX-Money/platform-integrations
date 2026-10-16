# Countries: Brazil (`br`)

Shared, provider-independent code for Brazil, used by every provider under
[`src/Providers/br`](../../Providers/br/):

- CPF and CNPJ parsing and validation, including the alphanumeric CNPJ.
- Pix key types (CPF/CNPJ, e-mail, phone, random/EVP) and end-to-end id parsing.
- Bank codes (COMPE and ISPB) and agency/account formatting.

```
src/Countries/br/CrxMoney.Integrations.Countries.Br.csproj
tests/Countries/br/CrxMoney.Integrations.Countries.Br.Tests/
```

Nothing here yet; it is created with the first provider that needs it. Changes
here run the tests of every Brazilian provider in CI.
