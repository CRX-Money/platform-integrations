using System.Text.Json;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Tools.InstitutionMarks.Tests;

public sealed class PluggyStatusPageTests
{
    // The page ships its data as JSON strings inside Next.js script pushes; this is the same shape, with the data
    // JSON-escaped once more exactly as the page does.
    private static string Page(string data)
        => $"<html><script>self.__next_f.push([1,\"{JsonSerializer.Serialize(data)[1..^1]}\"])</script></html>";

    private const string Data = """
        7:["$","$L14",null,{"initialData":{"institutions":[
        {"id":"601","pluggyId":"601","name":"Itaú","type":"regulado","isOpenFinance":true,"logoUrl":"https://cdn.pluggy.ai/assets/connector-icons/201.svg","products":[{"key":"dados"}]},
        {"id":"618","pluggyId":"618","name":"Itaú Empresas","type":"business","isOpenFinance":true,"logoUrl":"https://cdn.pluggy.ai/assets/connector-icons/618.svg","products":[]},
        {"id":"804","pluggyId":"804","name":"99Pay","type":"regulado","isOpenFinance":true,"logoUrl":"https://cdn.pluggy.ai/assets/connector-icons/804.png","products":[]},
        {"id":"200","pluggyId":"200","name":"MeuPluggy","type":"pluggy","isOpenFinance":false,"logoUrl":"https://cdn.pluggy.ai/assets/connector-icons/sandbox.svg","products":[]},
        {"id":"601","pluggyId":"601","name":"Itaú","type":"regulado","isOpenFinance":true,"logoUrl":"https://cdn.pluggy.ai/assets/connector-icons/201.svg","products":[]}
        ]}}]
        """;

    [Fact]
    public void The_connectors_are_read_from_the_pages_data()
    {
        var connectors = PluggyStatusPage.Parse(Page(Data.ReplaceLineEndings(string.Empty)));

        Assert.Equal([601, 618, 804], connectors.Select(c => c.Id));
        var itau = connectors[0];
        Assert.Equal("Itaú", itau.Name);
        Assert.Equal("regulado", itau.Type);
        Assert.Equal("https://cdn.pluggy.ai/assets/connector-icons/201.svg", itau.ImageUrl);
        Assert.Null(itau.InstitutionUrl);
        Assert.Equal("business", connectors[1].Type);
    }

    [Fact]
    public void A_page_without_connectors_reads_as_empty()
    {
        Assert.Empty(PluggyStatusPage.Parse("<html>nothing here</html>"));
        Assert.Empty(PluggyStatusPage.Parse(Page("{\"unrelated\":true}")));
    }
}
