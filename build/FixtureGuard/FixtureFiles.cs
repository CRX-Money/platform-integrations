namespace CrxMoney.Integrations.FixtureGuard;

internal static class FixtureFiles
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "TestResults", "node_modules", ".git", ".vs",
    };

    public static IEnumerable<string> Enumerate(string root)
    {
        if (File.Exists(root))
        {
            yield return root;
            yield break;
        }

        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>Treats a file as binary when its first 8 KB contain a NUL byte.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> bytes) => bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0);
}
