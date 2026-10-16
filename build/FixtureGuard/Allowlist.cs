namespace CrxMoney.Integrations.FixtureGuard;

/// <summary>
/// Values a maintainer has confirmed as synthetic even though they trip a rule. One value per line,
/// followed by <c># reason</c>; the reason is mandatory so every exception is explained in review.
/// </summary>
internal sealed class Allowlist
{
    private readonly HashSet<string> values;

    private Allowlist(HashSet<string> values) => this.values = values;

    public static Allowlist Empty { get; } = new([]);

    public static Allowlist Parse(IEnumerable<string> lines, out IReadOnlyList<string> errors)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        var number = 0;

        foreach (var raw in lines)
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash < 0 || line[(hash + 1)..].Trim().Length == 0)
            {
                problems.Add($"line {number}: add '# reason' after the value");
                continue;
            }

            values.Add(Normalize(line[..hash]));
        }

        errors = problems;
        return new Allowlist(values);
    }

    public bool Allows(string value) => values.Contains(Normalize(value));

    /// <summary>E-mail addresses compare case-insensitively; numbers ignore punctuation (123.456.789-09 equals 12345678909).</summary>
    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Contains('@')
            ? trimmed.ToLowerInvariant()
            : new string(trimmed.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
    }
}
