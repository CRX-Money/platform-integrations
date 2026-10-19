using System.Text;
using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Plugins;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;

/// <summary>
/// The Pluggy adapter's connection settings: the client id and secret of a Pluggy developer application and the
/// item id of one connected bank, with the Meu Pluggy walkthrough (Pluggy's "Personal use" guide), in English and
/// Brazilian Portuguese.
/// </summary>
internal static class PluggyConnectionSettings
{
    public const short SchemaVersion = 1;

    public const string ClientId = "clientId";

    public const string ClientSecret = "clientSecret";

    public const string ItemId = "itemId";

    private static readonly Uri MeuPluggy = new("https://meu.pluggy.ai/");
    private static readonly Uri Dashboard = new("https://dashboard.pluggy.ai/");

    public static IntegrationConnectionAdapterSettings Adapter(string? culture)
    {
        var texts = OpenFinanceTexts.Resolve(culture) == OpenFinanceTexts.BrazilianPortuguese ? Portuguese : English;
        IntegrationConnectionSettingsField[] fields =
        [
            new(ClientId, IntegrationCredentialSlot.ApiKey, IsSecret: false, IsRequired: true),
            new(ClientSecret, IntegrationCredentialSlot.ApiSecret, IsSecret: true, IsRequired: true),
            new(ItemId, IntegrationCredentialSlot.Passphrase, IsSecret: false, IsRequired: true),
        ];
        return new IntegrationConnectionAdapterSettings(PluggyAdapter.Name, "Pluggy", SchemaVersion, JsonSchema(texts), fields)
        {
            Walkthrough = new IntegrationConnectionWalkthrough(texts.WalkthroughTitle,
            [
                new(texts.Steps[0]) { Link = MeuPluggy },
                new(texts.Steps[1]) { Link = Dashboard },
                new(texts.Steps[2]),
                new(texts.Steps[3]),
                new(texts.Steps[4]),
            ])
            {
                Note = texts.PlanNote,
            },
        };
    }

    private static string JsonSchema(Texts texts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
            writer.WriteString("title", "Pluggy");
            writer.WriteString("description", texts.SchemaDescription);
            writer.WriteString("type", "object");
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteStartArray("required");
            writer.WriteStringValue(ClientId);
            writer.WriteStringValue(ClientSecret);
            writer.WriteStringValue(ItemId);
            writer.WriteEndArray();
            writer.WriteStartObject("properties");

            writer.WriteStartObject(ClientId);
            writer.WriteString("type", "string");
            writer.WriteString("format", "uuid");
            writer.WriteString("title", "Client ID");
            writer.WriteString("description", texts.ClientIdDescription);
            writer.WriteEndObject();

            writer.WriteStartObject(ClientSecret);
            writer.WriteString("type", "string");
            writer.WriteNumber("minLength", 1);
            writer.WriteBoolean("writeOnly", true);
            writer.WriteString("title", "Client Secret");
            writer.WriteString("description", texts.ClientSecretDescription);
            writer.WriteEndObject();

            writer.WriteStartObject(ItemId);
            writer.WriteString("type", "string");
            writer.WriteString("format", "uuid");
            writer.WriteString("title", "Item ID");
            writer.WriteString("description", texts.ItemIdDescription);
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record Texts(
        string SchemaDescription,
        string ClientIdDescription,
        string ClientSecretDescription,
        string ItemIdDescription,
        string WalkthroughTitle,
        string[] Steps,
        string PlanNote);

    private static readonly Texts English = new(
        "A Pluggy developer application and the item of one bank connected through Meu Pluggy.",
        "The Client ID of your application in the Pluggy Dashboard (Applications).",
        "The application's Client Secret. Pluggy shows it only once; generate a new one if you did not keep it.",
        "The id of the bank's item under Connected Items, on the application's Demo page.",
        "Connect a bank through Pluggy (Meu Pluggy)",
        [
            "Connect your banks in Meu Pluggy. Each bank is authorised on its own.",
            "Sign in to the Pluggy Dashboard with the same account, open Applications and copy your application's Client ID and Client Secret. The secret is shown only once; generate a new one if you did not keep it.",
            "On the application's Demo page, click Connect Account, choose MeuPluggy (not your bank or the Sandbox) and sign in with your Meu Pluggy account. Each bank becomes an item.",
            "Under Connected Items, open the bank's item and copy its id into Item ID.",
            "Test the connection, then save it. The authorisation lasts one year; repeat step 3 to renew it.",
        ],
        "The free Meu Pluggy plan covers your own accounts only (up to 5 connections) and excludes commercial use. Other people's accounts need Pluggy's commercial plan.");

    private static readonly Texts Portuguese = new(
        "Uma aplicação de desenvolvedor da Pluggy e o item de um banco conectado pelo Meu Pluggy.",
        "O Client ID da sua aplicação no Pluggy Dashboard (Applications).",
        "O Client Secret da aplicação. A Pluggy o mostra uma única vez; gere um novo se não o guardou.",
        "O id do item do banco em Connected Items, na página Demo da aplicação.",
        "Conectar um banco pela Pluggy (Meu Pluggy)",
        [
            "Conecte seus bancos no Meu Pluggy. Cada banco é autorizado separadamente.",
            "Entre no Pluggy Dashboard com a mesma conta, abra Applications e copie o Client ID e o Client Secret da sua aplicação. O secret aparece uma única vez; gere um novo se não o guardou.",
            "Na página Demo da aplicação, clique em Connect Account, escolha MeuPluggy (não o seu banco nem o Sandbox) e entre com a sua conta do Meu Pluggy. Cada banco vira um item.",
            "Em Connected Items, abra o item do banco e copie o id dele para Item ID.",
            "Teste a conexão e depois salve. A autorização vale um ano; repita o passo 3 para renová-la.",
        ],
        "O plano gratuito Meu Pluggy cobre só as suas próprias contas (até 5 conexões) e não permite uso comercial. Contas de terceiros exigem o plano comercial da Pluggy.");
}
