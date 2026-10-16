using CrxMoney.Integrations.Abstractions.Plugins;

namespace CrxMoney.Integrations.Providers.Br.Inter.Mapping;

/// <summary>The <c>ProviderData</c> schemas the plugin declares to the host.</summary>
internal static class InterImportSchemas
{
    public const string DepositSchemaId = "inter.banking.statement.deposit";
    public const string WithdrawalSchemaId = "inter.banking.statement.withdrawal";
    public const short Version1 = 1;

    public static IntegrationImportSchemaManifest Manifest { get; } = new(
    [
        new IntegrationImportSchemaManifestItem(
            DepositSchemaId,
            Version1,
            "Inter statement credit",
            "A credit (tipoOperacao C) from the Inter Empresas enriched statement.",
            JsonSchemaFor("fiat.deposit")),
        new IntegrationImportSchemaManifestItem(
            WithdrawalSchemaId,
            Version1,
            "Inter statement debit",
            "A debit (tipoOperacao D) from the Inter Empresas enriched statement.",
            JsonSchemaFor("fiat.withdrawal")),
    ]);

    private static string JsonSchemaFor(string kind) => $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["provider", "kind", "status", "environment", "transactionId", "transactionDate", "tipoOperacao"],
          "properties": {
            "provider": { "const": "Inter" },
            "kind": { "const": "{{kind}}" },
            "status": { "const": "posted" },
            "environment": { "enum": ["sandbox", "production"] },
            "transactionId": { "type": "string", "description": "Inter idTransacao" },
            "transactionDate": { "type": "string", "format": "date", "description": "Booking date (dataTransacao), Brazilian calendar" },
            "inclusionDate": { "type": "string", "description": "dataInclusao as returned by Inter" },
            "tipoOperacao": { "enum": ["C", "D"] },
            "tipoTransacao": { "type": "string", "description": "PIX, TRANSFERENCIA, PAGAMENTO, BOLETO_COBRANCA, INVESTIMENTO, CAMBIO, ESTORNO, TARIFA, OUTROS, ..." },
            "titulo": { "type": "string" },
            "descricao": { "type": "string" },
            "numeroDocumento": { "type": "string" },
            "transferHint": { "enum": ["investment"], "description": "Set for INVESTIMENTO entries: a move between the account and its own investments" },
            "counterparty": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "taxId": { "type": "string", "description": "CPF (11) or CNPJ (14) with valid check digits, unformatted" },
                "taxIdKind": { "enum": ["CPF", "CNPJ"] },
                "taxIdCountry": { "const": "BR" },
                "bank": { "type": "string" },
                "branch": { "type": "string" },
                "account": { "type": "string" }
              }
            },
            "pix": {
              "type": "object",
              "properties": {
                "endToEndId": { "type": "string" },
                "txId": { "type": "string" },
                "key": { "type": "string" }
              }
            },
            "detalhes": { "description": "Inter's detalhes object, verbatim" }
          }
        }
        """;
}
