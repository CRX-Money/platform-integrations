namespace CrxMoney.Integrations.Providers.Br.Inter;

/// <summary>The Inter Empresas API environment a connection talks to.</summary>
public enum InterEnvironment
{
    /// <summary>
    /// <c>cdpj-sandbox.partners.uatinter.co</c>: Inter's test environment. It returns fictitious data and
    /// needs credentials from a sandbox integration.
    /// </summary>
    Sandbox = 0,

    /// <summary><c>cdpj.partners.bancointer.com.br</c>: the real account.</summary>
    Production = 1
}
