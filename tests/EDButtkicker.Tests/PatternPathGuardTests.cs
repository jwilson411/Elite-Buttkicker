using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// The path guard is the only thing standing between a caller-supplied pattern file name and a
/// filesystem path, so every traversal shape the HTTP surface can carry is pinned here.
/// </summary>
public class PatternPathGuardTests
{
    [Theory]
    [InlineData("../secret.json")]
    [InlineData("..\\secret.json")]
    [InlineData("foo/../../secret.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("\\\\server\\share\\file.json")]
    [InlineData("foo.json/../../x")]
    [InlineData("%2e%2e/%2e%2e/secret.json")]
    [InlineData("%2e%2e%2fsecret.json")]
    [InlineData("%2e%2e%5csecret.json")]
    [InlineData("%2fetc%2fpasswd")]
    [InlineData("%252e%252e%252fsecret.json")]
    [InlineData("..%252f..%252fsecret.json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("....//....")]
    [InlineData("C:pack.json")]
    [InlineData("pack.json:stream")]
    [InlineData("patterns\\Custom\\pack.json")]
    [InlineData(null)]
    public void SanitizeFileName_RejectsTraversalShapes(string? name)
    {
        Assert.Null(PatternPathGuard.SanitizeFileName(name));
    }

    [Theory]
    [InlineData("my-pack.json")]
    [InlineData("Author_Pack_20260101_010101.json")]
    [InlineData("pack.with.dots.json")]
    public void SanitizeFileName_AcceptsPlainFileNames(string name)
    {
        Assert.Equal(name, PatternPathGuard.SanitizeFileName(name));
    }

    [Fact]
    public void SanitizeFileName_DecodesBeforeReturning()
    {
        Assert.Equal("my pack.json", PatternPathGuard.SanitizeFileName("my%20pack.json"));
    }

    [Fact]
    public void SanitizeFileName_CanRequireJsonExtension()
    {
        Assert.Null(PatternPathGuard.SanitizeFileName("pack.exe", requireJsonExtension: true));
        Assert.Equal("pack.JSON", PatternPathGuard.SanitizeFileName("pack.JSON", requireJsonExtension: true));
    }

    [Theory]
    [InlineData("../secret.json")]
    [InlineData("..\\secret.json")]
    [InlineData("foo/../../secret.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("\\\\server\\share\\file.json")]
    [InlineData("foo.json/../../x")]
    [InlineData("%2e%2e/%2e%2e/secret.json")]
    [InlineData("%2e%2e%2fsecret.json")]
    [InlineData("%2e%2e%5csecret.json")]
    [InlineData("%2fetc%2fpasswd")]
    [InlineData("%252e%252e%252fsecret.json")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("....//....")]
    [InlineData(null)]
    public void ResolveUnderRoot_RejectsTraversalShapes(string? name)
    {
        using var root = new TempDirectory();

        Assert.Null(PatternPathGuard.ResolveUnderRoot(root.Path, name));
        Assert.Null(PatternPathGuard.ResolveUnderRoot(root.Path, name, "Custom"));
    }

    [Theory]
    [InlineData("my-pack.json")]
    [InlineData("Author_Pack_20260101_010101.json")]
    public void ResolveUnderRoot_PlacesAcceptedNamesUnderTheRoot(string name)
    {
        using var root = new TempDirectory();

        var resolved = PatternPathGuard.ResolveUnderRoot(root.Path, name);

        Assert.Equal(Path.Combine(Path.GetFullPath(root.Path), name), resolved);
    }

    [Fact]
    public void ResolveUnderRoot_PlacesAcceptedNamesUnderTheAllowedSubdirectory()
    {
        using var root = new TempDirectory();

        var resolved = PatternPathGuard.ResolveUnderRoot(root.Path, "my-pack.json", "imports");

        Assert.Equal(Path.Combine(Path.GetFullPath(root.Path), "imports", "my-pack.json"), resolved);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("Custom/../..")]
    [InlineData("unknown")]
    [InlineData("")]
    public void ResolveUnderRoot_RejectsSubdirectoriesOutsideTheAllowList(string subdirectory)
    {
        using var root = new TempDirectory();

        Assert.Null(PatternPathGuard.ResolveUnderRoot(root.Path, "my-pack.json", subdirectory));
    }

    [Fact]
    public void ResolveUnderRoot_DoesNotTreatASiblingPrefixDirectoryAsInside()
    {
        using var parent = new TempDirectory();
        var root = Path.Combine(parent.Path, "patterns");
        var sibling = Path.Combine(parent.Path, "patterns-evil");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);

        var resolved = PatternPathGuard.ResolveUnderRoot(root, "my-pack.json");

        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, resolved);
        Assert.False(resolved!.StartsWith(Path.GetFullPath(sibling), StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveUnderRoot_RejectsAnEmptyRoot()
    {
        Assert.Null(PatternPathGuard.ResolveUnderRoot("", "my-pack.json"));
    }
}

/// <summary>A scratch directory that removes itself, so no test writes into the repo tree.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "edbk-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
