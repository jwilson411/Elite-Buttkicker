namespace EDButtkicker.Services;

/// <summary>
/// Every filesystem operation the journal tail reader performs. Behind an interface so rotation,
/// truncation, a half-written trailing line and a locked file are all reproducible in a test
/// without depending on how the OS happens to schedule the game's writes.
/// </summary>
public interface IJournalStorage
{
    bool DirectoryExists(string directory);

    /// <summary>Journal files in <paramref name="directory"/>, unordered; the reader sorts them.</summary>
    IReadOnlyList<string> ListFiles(string directory, string searchPattern);

    /// <summary>
    /// Opens a journal file for shared reading. The stream must be seekable and report the length
    /// the writer has committed so far, because that is how a partial trailing line is detected.
    /// </summary>
    Stream OpenRead(string path);
}

/// <summary>The production adapter: System.IO, opened for shared read so the game keeps writing.</summary>
public sealed class FileSystemJournalStorage : IJournalStorage
{
    private const int ReadBufferSize = 8192;

    /// <summary>Shared instance for the readers that are not resolved from the container.</summary>
    public static FileSystemJournalStorage Instance { get; } = new();

    public bool DirectoryExists(string directory) => Directory.Exists(directory);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) =>
        Directory.GetFiles(directory, searchPattern);

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBufferSize, useAsync: true);
}
