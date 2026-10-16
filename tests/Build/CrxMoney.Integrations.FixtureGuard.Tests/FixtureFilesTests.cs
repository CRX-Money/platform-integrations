using System.Text;

namespace CrxMoney.Integrations.FixtureGuard.Tests;

public sealed class FixtureFilesTests
{
    [Fact]
    public void Reads_utf16_text_with_a_byte_order_mark_instead_of_skipping_it()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Agência 0001")).ToArray();

        Assert.Equal("Agência 0001", FixtureFiles.ReadText(bytes));
    }

    [Fact]
    public void Falls_back_to_latin1_for_legacy_bank_exports()
    {
        var bytes = Encoding.Latin1.GetBytes("Agência 0001");

        Assert.Equal("Agência 0001", FixtureFiles.ReadText(bytes));
    }

    [Fact]
    public void Treats_files_with_nul_bytes_and_no_byte_order_mark_as_binary()
    {
        Assert.Null(FixtureFiles.ReadText([0x25, 0x50, 0x44, 0x46, 0x00, 0x01]));
    }

    [Fact]
    public void Enumerates_files_in_ordinal_order_and_skips_build_output()
    {
        var root = Directory.CreateTempSubdirectory("fixture-guard-").FullName;
        try
        {
            foreach (var path in new[] { "b/2.txt", "a/1.txt", "a/bin/x.txt", "obj/y.txt" })
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "x");
            }

            var files = FixtureFiles.Enumerate(root).Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'));

            Assert.Equal(["a/1.txt", "b/2.txt"], files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
