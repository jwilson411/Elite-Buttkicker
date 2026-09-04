using EDButtkicker.Services;
using Xunit;

namespace EDButtkicker.Tests;

/// <summary>
/// Journal replay takes a file name off an HTTP request, so this guard is the only thing between
/// that name and File.ReadAllLines. Every traversal shape the request can carry is pinned here,
/// along with the rule that only a currently enumerated journal file resolves at all.
/// </summary>
public class JournalFileGuardTests
{
    private const string LegitName = "Journal.2026-01-01T000000.01.log";

    [Theory]
    [InlineData("../secret.log")]
    [InlineData("..\\secret.log")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("Journal.foo/../../etc/passwd")]
    [InlineData("..%2fsecret.log")]
    [InlineData("%2e%2e%2fsecret.log")]
    [InlineData("%2e%2e%5csecret.log")]
    [InlineData("%252e%252e%252fJournal.2026-01-01T000000.01.log")]
    [InlineData("subdir/Journal.2026-01-01T000000.01.log")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("notes.txt")]
    [InlineData("Journal.log")]
    [InlineData("status.json")]
    [InlineData("Journal.2026-01-01T000000.01.log.bak")]
    [InlineData(null)]
    public void SanitizeFileName_RejectsAnythingThatIsNotAPlainJournalName(string? name)
    {
        Assert.Null(JournalFileGuard.SanitizeFileName(name));
    }

    [Theory]
    [InlineData(LegitName)]
    [InlineData("Journal.220101120000.01.log")]
    [InlineData("Journal.2026-01-01T000000.01.LOG")]
    public void SanitizeFileName_AcceptsJournalShapedNames(string name)
    {
        Assert.Equal(name, JournalFileGuard.SanitizeFileName(name));
    }

    [Theory]
    [InlineData("../secret.log")]
    [InlineData("..\\secret.log")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("Journal.foo/../../etc/passwd")]
    [InlineData("..%2fsecret.log")]
    [InlineData("%2e%2e%2fsecret.log")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("notes.txt")]
    [InlineData("Journal.log")]
    [InlineData("status.json")]
    [InlineData(null)]
    public void Resolve_RejectsTraversalAndNonJournalNames(string? name)
    {
        using var journalDir = new TempDirectory("edbk-journal-guard");
        File.WriteAllText(journalDir.File(LegitName), "{}");

        Assert.Null(JournalFileGuard.Resolve(journalDir.Path, name));
    }

    [Fact]
    public void Resolve_AcceptsAnEnumeratedJournalFile()
    {
        using var journalDir = new TempDirectory("edbk-journal-guard");
        var path = journalDir.File(LegitName);
        File.WriteAllText(path, "{}");

        Assert.Equal(Path.GetFullPath(path), JournalFileGuard.Resolve(journalDir.Path, LegitName));
    }

    [Fact]
    public void Resolve_RejectsAJournalShapedNameThatIsNotInTheDirectory()
    {
        using var journalDir = new TempDirectory("edbk-journal-guard");
        File.WriteAllText(journalDir.File(LegitName), "{}");

        Assert.Null(JournalFileGuard.Resolve(journalDir.Path, "Journal.2026-02-02T000000.01.log"));
    }

    [Fact]
    public void Resolve_RejectsAJournalFileThatOnlyExistsOutsideTheDirectory()
    {
        using var parent = new TempDirectory("edbk-journal-guard");
        var journalDir = Path.Combine(parent.Path, "journals");
        Directory.CreateDirectory(journalDir);
        File.WriteAllText(Path.Combine(parent.Path, LegitName), "{}");

        Assert.Null(JournalFileGuard.Resolve(journalDir, LegitName));
        Assert.Null(JournalFileGuard.Resolve(journalDir, $"..{Path.DirectorySeparatorChar}{LegitName}"));
    }

    [Fact]
    public void Resolve_DoesNotTreatASiblingPrefixDirectoryAsInside()
    {
        using var parent = new TempDirectory("edbk-journal-guard");
        var journalDir = Path.Combine(parent.Path, "journals");
        var sibling = Path.Combine(parent.Path, "journals-evil");
        Directory.CreateDirectory(journalDir);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, LegitName), "{}");

        Assert.Null(JournalFileGuard.Resolve(journalDir, LegitName));
    }

    [Fact]
    public void Resolve_RejectsAnUnconfiguredOrMissingJournalDirectory()
    {
        Assert.Null(JournalFileGuard.Resolve(null, LegitName));
        Assert.Null(JournalFileGuard.Resolve("", LegitName));
        Assert.Null(JournalFileGuard.Resolve(
            Path.Combine(Path.GetTempPath(), $"edbk-journal-missing-{Guid.NewGuid():N}"), LegitName));
    }
}
