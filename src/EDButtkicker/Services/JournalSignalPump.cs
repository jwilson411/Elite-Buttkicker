using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EDButtkicker.Services;

/// <summary>
/// Single-reader queue for journal wake-up signals.
///
/// FileSystemWatcher raises Changed on thread-pool threads and can fire several times for one
/// write; periodic rotation checks add more. Every producer just calls <see cref="Signal"/>, and
/// one consumer loop runs the handler - handlers never overlap, so the read cursor is never raced.
/// Signals are coalesced (a bounded capacity-1 channel): pending signals collapse into a single
/// follow-up pass, and a signal raised while the handler is running always triggers another pass.
/// </summary>
public sealed class JournalSignalPump : IDisposable
{
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Func<CancellationToken, Task> _handler;
    private readonly ILogger _logger;

    public JournalSignalPump(Func<CancellationToken, Task> handler, ILogger? logger = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Queues a wake-up. Never blocks; extra signals coalesce into the pending one.</summary>
    public void Signal() => _signals.Writer.TryWrite(0);

    /// <summary>Runs the single consumer loop until <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _signals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Collapse everything queued so far into this single pass.
                while (_signals.Reader.TryRead(out _))
                {
                }

                try
                {
                    await _handler(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling journal signal");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _signals.Writer.TryComplete();
        }
    }

    public void Dispose() => _signals.Writer.TryComplete();
}
