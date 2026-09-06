namespace EDButtkicker.Services;

/// <summary>Size and last-write time of one file, i.e. what says whether a write has finished.</summary>
public readonly record struct PatternFileStamp(long Length, DateTime LastWriteUtc);

/// <summary>
/// Every filesystem operation the pattern catalog performs. Behind an interface so the catalog's
/// rules - what is loaded, what a partial write means, what a rename does - can be exercised
/// against an in-memory directory rather than against the developer's real profile and the OS's
/// own timing.
/// </summary>
public interface IPatternStorage
{
    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    bool FileExists(string path);

    /// <summary>Every *.json file under <paramref name="directory"/>, including subdirectories.</summary>
    IReadOnlyList<string> ListJsonFiles(string directory);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default);

    void Copy(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>Size and last-write time, or null when the file is not there (or cannot be read).</summary>
    PatternFileStamp? Stat(string path);
}

/// <summary>The production adapter: System.IO, and nothing else.</summary>
public sealed class FileSystemPatternStorage : IPatternStorage
{
    /// <summary>Shared instance for the constructors that are not resolved from the container.</summary>
    public static FileSystemPatternStorage Instance { get; } = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> ListJsonFiles(string directory) =>
        Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(path, contents, cancellationToken);

    public void Copy(string sourcePath, string destinationPath, bool overwrite) =>
        File.Copy(sourcePath, destinationPath, overwrite);

    public PatternFileStamp? Stat(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        try
        {
            return new PatternFileStamp(info.Length, info.LastWriteTimeUtc);
        }
        catch (IOException)
        {
            // Being written to right now: unknown is not "unchanged", so the probe repeats.
            return null;
        }
    }
}
