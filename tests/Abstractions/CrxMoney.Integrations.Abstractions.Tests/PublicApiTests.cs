using CrxMoney.Integrations.Abstractions.Plugins;
using PublicApiGenerator;

namespace CrxMoney.Integrations.Abstractions.Tests;

/// <summary>
/// Locks the public surface of the contract package. Any change to a public type or member fails
/// this test until PublicApi/CrxMoney.Integrations.Abstractions.approved.txt is updated in the same
/// pull request, so contract changes are always visible in review (see docs/versioning.md).
/// </summary>
public sealed class PublicApiTests
{
    private const string ApprovedFileName = "CrxMoney.Integrations.Abstractions.approved.txt";
    private const string ReceivedFileName = "CrxMoney.Integrations.Abstractions.received.txt";

    [Fact]
    public void Public_api_matches_the_approved_surface()
    {
        var actual = Normalize(typeof(IIntegrationPlugin).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            ExcludeAttributes =
            [
                "System.Runtime.Versioning.TargetFrameworkAttribute",
                "System.Reflection.AssemblyMetadataAttribute",
            ],
        }));

        var directory = Path.Combine(FindRepositoryRoot(), "tests", "Abstractions", "CrxMoney.Integrations.Abstractions.Tests", "PublicApi");
        var approvedPath = Path.Combine(directory, ApprovedFileName);
        var receivedPath = Path.Combine(directory, ReceivedFileName);
        var approved = File.Exists(approvedPath) ? Normalize(File.ReadAllText(approvedPath)) : string.Empty;

        if (actual == approved)
        {
            File.Delete(receivedPath);
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(receivedPath, actual);
        Assert.Fail(
            $"The public API of CrxMoney.Integrations.Abstractions changed. Review {receivedPath}; if the change is " +
            $"intended and follows docs/versioning.md, replace {ApprovedFileName} with it.");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim() + "\n";

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (global.json) above " + AppContext.BaseDirectory);
    }
}
