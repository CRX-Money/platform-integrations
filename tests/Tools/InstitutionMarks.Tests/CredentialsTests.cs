namespace CrxMoney.Integrations.Tools.InstitutionMarks.Tests;

public sealed class CredentialsTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), $"marks-secrets-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(file))
            File.Delete(file);
    }

    [Fact]
    public void A_value_is_read_from_the_user_secrets_file()
    {
        File.WriteAllText(file, """{"CRX_TEST_ONLY_MARKS_ID": " client-id "}""");

        Assert.Equal("client-id", Credentials.Read("CRX_TEST_ONLY_MARKS_ID", file));
        Assert.Null(Credentials.Read("CRX_TEST_ONLY_MARKS_MISSING", file));
    }

    [Fact]
    public void The_environment_wins_over_the_secrets_file()
    {
        File.WriteAllText(file, """{"CRX_TEST_ONLY_MARKS_BOTH": "from-file"}""");
        Environment.SetEnvironmentVariable("CRX_TEST_ONLY_MARKS_BOTH", "from-environment");
        try
        {
            Assert.Equal("from-environment", Credentials.Read("CRX_TEST_ONLY_MARKS_BOTH", file));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CRX_TEST_ONLY_MARKS_BOTH", null);
        }
    }

    [Fact]
    public void A_missing_or_broken_file_is_no_credential()
    {
        Assert.Null(Credentials.Read("CRX_TEST_ONLY_MARKS_ID", Path.Combine(Path.GetTempPath(), "does-not-exist.json")));

        File.WriteAllText(file, "{ not json");
        Assert.Null(Credentials.Read("CRX_TEST_ONLY_MARKS_ID", file));
    }
}
