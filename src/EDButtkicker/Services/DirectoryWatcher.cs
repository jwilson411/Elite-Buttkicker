using Microsoft.Extensions.Logging;

namespace EDButtkicker.Services;

/// <summary>What happened to one watched file.</summary>
public enum WatchedFileChange
{
    Created,
    Changed,
    Deleted,
    Renamed
}

/// <summary>One watcher notification, with the source path filled in for a rename.</summary>
public sealed record WatchedFileEvent(WatchedFileChange Change, string FullPath, string? OldFullPath = null);

/// <summary>
/// A directory being watched for file changes. Behind an interface because the only production
/// implementation is <see cref="FileSystemWatcher"/>, whose events arrive on OS threads at times no
/// test can control - and which does not raise them at all on some filesystems. Consumers get the
/// same event shape either way, so rotation, rename and overflow handling are all testable.
/// </summary>
public interface IDirectoryWatcher : IDisposable
{
    /// <summary>Raised on a watcher thread for every create, change, delete and rename.</summary>
    event Action<WatchedFileEvent>? Changed;

    /// <summary>Raised when the watcher itself failed, i.e. events were lost.</summary>
    event Action<Exception>? Error;

    /// <summary>Begins raising events. Separate from construction so handlers can subscribe first.</summary>
    void Start();
}

/// <summary>Creates a watcher for one directory. The composition root binds the production adapter.</summary>
public interface IDirectoryWatcherFactory
{
    IDirectoryWatcher Create(string directory, string filter, bool includeSubdirectories = false);
}

/// <summary>Production adapter: one <see cref="FileSystemWatcher"/> per watched directory.</summary>
public sealed class FileSystemDirectoryWatcherFactory : IDirectoryWatcherFactory
{
    private readonly ILogger<FileSystemDirectoryWatcherFactory> _logger;

    public FileSystemDirectoryWatcherFactory(ILogger<FileSystemDirectoryWatcherFactory> logger)
    {
        _logger = logger;
    }

    public IDirectoryWatcher Create(string directory, string filter, bool includeSubdirectories = false)
    {
        _logger.LogDebug("Watching {Directory} for {Filter} (subdirectories: {IncludeSubdirectories})",
            directory, filter, includeSubdirectories);

        return new FileSystemDirectoryWatcher(directory, filter, includeSubdirectories);
    }
}

/// <summary>
/// The <see cref="FileSystemWatcher"/> wrapper. It only translates events - no filtering, no
/// debouncing, no work of its own - so the ordering and coalescing rules stay in the consumers.
/// </summary>
internal sealed class FileSystemDirectoryWatcher : IDirectoryWatcher
{
    private readonly FileSystemWatcher _watcher;

    public FileSystemDirectoryWatcher(string directory, string filter, bool includeSubdirectories)
    {
        // The union of what the journal monitor and the pattern catalog each need: a size-only
        // append (journal writes) and a create/rename (pattern edits) both have to be noticed.
        _watcher = new FileSystemWatcher(directory, filter)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite |
                           NotifyFilters.FileName | NotifyFilters.Size
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    public event Action<WatchedFileEvent>? Changed;

    public event Action<Exception>? Error;

    public void Start() => _watcher.EnableRaisingEvents = true;

    private void OnCreated(object sender, FileSystemEventArgs e) =>
        Changed?.Invoke(new WatchedFileEvent(WatchedFileChange.Created, e.FullPath));

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        Changed?.Invoke(new WatchedFileEvent(WatchedFileChange.Changed, e.FullPath));

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        Changed?.Invoke(new WatchedFileEvent(WatchedFileChange.Deleted, e.FullPath));

    private void OnRenamed(object sender, RenamedEventArgs e) =>
        Changed?.Invoke(new WatchedFileEvent(WatchedFileChange.Renamed, e.FullPath, e.OldFullPath));

    private void OnError(object sender, ErrorEventArgs e) => Error?.Invoke(e.GetException());

    public void Dispose()
    {
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnCreated;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by the runtime.
        }

        _watcher.Dispose();

        Changed = null;
        Error = null;
    }
}
