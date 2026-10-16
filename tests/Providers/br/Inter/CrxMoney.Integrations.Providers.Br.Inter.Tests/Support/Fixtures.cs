using System.Text.Json;
using CrxMoney.Integrations.Providers.Br.Inter.Api;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;

/// <summary>Synthetic Inter responses under <c>Fixtures/</c>, shaped after the portal reference.</summary>
internal static class Fixtures
{
    public static string Text(string name) =>
        SyntheticTaxIds.Fill(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    public static InterStatementPage StatementPage(int page) =>
        JsonSerializer.Deserialize(Text($"extrato-completo-page-{page}.json"), InterApiJsonContext.Default.InterStatementPage)!;

    public static IReadOnlyList<InterTransaction> AllTransactions() =>
        [.. StatementPage(0).Transacoes!, .. StatementPage(1).Transacoes!];

    public static InterTransaction Transaction(string id) => AllTransactions().Single(t => t.IdTransacao == id);
}
