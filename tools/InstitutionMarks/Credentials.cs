using System.Text.Json.Nodes;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>
/// The Pluggy client id and secret: an environment variable of the same name wins, then the tool's
/// <c>dotnet user-secrets</c> store. They are never printed, logged or written into the repository.
/// </summary>
internal static class Credentials
{
    public const string ClientIdName = "CRX_PLUGGY_CLIENT_ID";
    public const string ClientSecretName = "CRX_PLUGGY_CLIENT_SECRET";
    public const string UserSecretsId = "crx-money-institution-marks";

    public static string? Read(string name, string? secretsFile = null)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();

        secretsFile ??= DefaultSecretsFile();
        if (secretsFile is null || !File.Exists(secretsFile))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(secretsFile))?[name]?.GetValue<string>() is { } value && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Where <c>dotnet user-secrets</c> keeps this tool's values on this machine.</summary>
    internal static string? DefaultSecretsFile()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData ? Path.Combine(appData, "Microsoft", "UserSecrets") : null
            : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? Path.Combine(home, ".microsoft", "usersecrets") : null;
        return root is null ? null : Path.Combine(root, UserSecretsId, "secrets.json");
    }
}
