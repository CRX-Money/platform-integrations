using System.Text;

namespace CrxMoney.Integrations.FixtureGuard;

internal static class FixtureFiles
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "TestResults", "node_modules", ".git", ".vs",
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Every file under <paramref name="root"/> (or the file itself), in ordinal path order.</summary>
    public static IEnumerable<string> Enumerate(string root)
    {
        if (File.Exists(root))
        {
            return [root];
        }

        return Directory
            .EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(file => !Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .SkipLast(1)
                .Any(SkippedDirectories.Contains))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// Decodes a file as text: UTF-8/UTF-16/UTF-32 when a byte-order mark says so, otherwise UTF-8,
    /// falling back to Latin-1 for legacy bank exports. Returns <see langword="null"/> for binary files
    /// (a NUL byte in the first 8 KB of a file without a UTF-16/32 byte-order mark).
    /// </summary>
    public static string? ReadText(byte[] bytes)
    {
        var bom = DetectBom(bytes);
        if (bom is not null)
        {
            return bom.GetString(bytes, bom.Preamble.Length, bytes.Length - bom.Preamble.Length);
        }

        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0))
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // UTF-32 LE comes first: its byte-order mark starts with the UTF-16 LE one.
    private static readonly Encoding[] BomEncodings =
        [new UTF32Encoding(false, true), new UTF32Encoding(true, true), Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode];

    private static Encoding? DetectBom(byte[] bytes) =>
        BomEncodings.FirstOrDefault(encoding => bytes.AsSpan().StartsWith(encoding.Preamble));
}
