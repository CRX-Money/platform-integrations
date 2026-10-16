using CrxMoney.Integrations.FixtureGuard;

// Usage: dotnet run --project build/FixtureGuard -- [--allowlist <file>] [path ...]
// Paths are relative to the current directory (run it from the repository root). Default: tests.
// Exit codes: 0 clean, 1 findings, 2 usage or allowlist error.

var allowlistPath = "tests/fixture-allowlist.txt";
var roots = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--allowlist" && i + 1 < args.Length)
    {
        allowlistPath = args[++i];
    }
    else if (args[i].StartsWith('-'))
    {
        Console.Error.WriteLine($"Unknown option {args[i]}.");
        return 2;
    }
    else
    {
        roots.Add(args[i]);
    }
}

if (roots.Count == 0)
{
    roots.Add("tests");
}

var allowlist = Allowlist.Empty;
if (File.Exists(allowlistPath))
{
    allowlist = Allowlist.Parse(File.ReadLines(allowlistPath), out var errors);
    if (errors.Count > 0)
    {
        foreach (var error in errors)
        {
            Console.Error.WriteLine($"{allowlistPath}: {error}");
        }

        return 2;
    }
}

var annotate = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
var allowlistFullPath = Path.GetFullPath(allowlistPath);
var findings = new List<Finding>();
var skippedBinary = new List<string>();
var scanned = 0;

foreach (var file in roots.SelectMany(FixtureFiles.Enumerate).Distinct())
{
    if (Path.GetFullPath(file) == allowlistFullPath)
    {
        continue;
    }

    var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), file).Replace('\\', '/');
    var bytes = File.ReadAllBytes(file);
    if (FixtureFiles.IsBinary(bytes))
    {
        skippedBinary.Add(relative);
        continue;
    }

    scanned++;
    var text = System.Text.Encoding.UTF8.GetString(bytes);
    findings.AddRange(FixtureScanner.Scan(relative, text).Where(finding => !allowlist.Allows(finding.Value)));
}

foreach (var finding in findings)
{
    // Values are masked: if a real identifier slipped in, the CI log must not republish it.
    var message = $"{finding.Rule} {finding.Description} ({finding.Masked}). See docs/fixture-policy.md.";
    Console.WriteLine(annotate
        ? $"::error file={finding.Path},line={finding.Line},title=Fixture guard {finding.Rule}::{message}"
        : $"{finding.Path}({finding.Line}): {message}");
}

foreach (var path in skippedBinary)
{
    var message = "binary file not scanned; a maintainer must confirm it holds no real data.";
    Console.WriteLine(annotate ? $"::warning file={path},title=Fixture guard::{message}" : $"{path}: {message}");
}

Console.WriteLine($"Fixture guard: {scanned} text file(s) scanned, {skippedBinary.Count} binary skipped, {findings.Count} finding(s).");
return findings.Count == 0 ? 0 : 1;
