namespace DeskBox.Services;

/// <summary>
/// Coalesces successful automatic-organization notifications into one toast
/// every five seconds. Errors are still reported immediately by their caller.
/// </summary>
public sealed class DesktopOrganizationNotificationCoalescer : IDisposable
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly Action<IReadOnlyList<DesktopAutoOrganizationCompleted>> _flush;
    private readonly List<DesktopAutoOrganizationCompleted> _pending = [];
    private Timer? _timer;
    private bool _disposed;

    public DesktopOrganizationNotificationCoalescer(
        Action<IReadOnlyList<DesktopAutoOrganizationCompleted>> flush)
    {
        _flush = flush;
    }

    public void Enqueue(DesktopAutoOrganizationCompleted completed)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending.Add(completed);
            _timer ??= new Timer(
                static state => ((DesktopOrganizationNotificationCoalescer)state!).FlushNow(),
                this,
                FlushDelay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushNow()
    {
        DesktopAutoOrganizationCompleted[] batch;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            batch = _pending.ToArray();
            _pending.Clear();
            _timer?.Dispose();
            _timer = null;
        }

        try
        {
            _flush(batch);
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopAutoOrganization][Notification] Flush failed: {ex}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
            _timer?.Dispose();
            _timer = null;
        }
    }
}
