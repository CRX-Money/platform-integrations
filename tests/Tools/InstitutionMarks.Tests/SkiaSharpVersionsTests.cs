using System.Xml.Linq;

namespace CrxMoney.Integrations.Tools.InstitutionMarks.Tests;

/// <summary>
/// SkiaSharp and its Linux native library must be the same version: Svg.Skia only sets a floor on SkiaSharp, and a
/// native library newer than the managed binding fails at start-up on Linux ("The version of the native libSkiaSharp
/// library ... is incompatible with this version of SkiaSharp"). Dependabot bumps both in one group PR; this test
/// fails the build when they drift apart (as PR #22 did).
/// </summary>
public sealed class SkiaSharpVersionsTests
{
    [Fact]
    public void SkiaSharp_and_its_Linux_native_library_share_one_version()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var versions = props.Descendants("PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!);

        Assert.True(versions.ContainsKey("SkiaSharp"), "Directory.Packages.props must pin SkiaSharp explicitly.");
        Assert.Equal(versions["SkiaSharp"], versions["SkiaSharp.NativeAssets.Linux.NoDependencies"]);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Directory.Packages.props not found above the test folder.");
    }
}
