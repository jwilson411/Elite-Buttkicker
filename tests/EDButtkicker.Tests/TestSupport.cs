using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EDButtkicker.Tests;

/// <summary>
/// A throwaway directory under the system temp path. Every test that touches the filesystem owns
/// one of these, so nothing is read from - or written to - the developer's real profile.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "edbk-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException) { /* best effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best effort cleanup */ }
    }
}

/// <summary>
/// Clock the test drives by hand. Nothing that depends on elapsed time may wait on the wall clock,
/// otherwise the rate limit tests would be slow on a good day and flaky on a bad one.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _utcNow = start ?? new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}

internal static class TestLoggers
{
    public static ILogger<T> For<T>() => NullLogger<T>.Instance;
}
