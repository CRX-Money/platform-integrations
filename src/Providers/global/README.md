# Providers: global

Plugins for providers that work the same way across countries, such as crypto
exchanges and international brokers. One folder per provider:

```
src/Providers/global/<Provider>/CrxMoney.Integrations.Global.<Provider>.csproj
tests/Providers/global/<Provider>/CrxMoney.Integrations.Global.<Provider>.Tests/
```

Planned: **Binance**, moved from the CRX•Money platform once the contract
package has proven itself with a first provider.

If a provider has country-specific products (a local bank account next to a
global exchange), keep the global part here and put the local part under that
country.
