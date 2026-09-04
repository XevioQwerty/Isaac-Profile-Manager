namespace IsaacProfileManager.Core.Services;

/// <summary>
/// Notices the game starting and, more usefully, stopping.
///
/// The game writes its save on exit, so the moment after it closes is the
/// moment the live folder holds the run you just played — and until now that
/// progress stayed stranded there, while the set on disk kept the bytes from
/// when it was captured. There is no exit event to subscribe to for a process
/// we did not start (Steam starts it), so this polls the process list.
///
/// Events fire on a thread-pool thread. <see cref="Poll"/> is public so the
/// transitions can be tested without a timer.
/// </summary>
public sealed class GameSessionWatcher : IDisposable
{
    private readonly IGameProcessService _process;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();

    private Timer? _timer;
    private bool? _lastKnown;

    public GameSessionWatcher(IGameProcessService process, TimeSpan? interval = null)
    {
        _process = process;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>The game was not running and now is.</summary>
    public event Action? Started;

    /// <summary>The game was running and now is not. The save has just been written.</summary>
    public event Action? Exited;

    /// <summary>What the last poll saw, or null before the first.</summary>
    public bool? LastKnownRunning => _lastKnown;

    public void Start()
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, _interval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>One observation. The first establishes a baseline and fires nothing.</summary>
    public void Poll()
    {
        bool running;
        try { running = _process.IsIsaacRunning(); }
        catch (Exception) { return; }

        bool? previous;
        lock (_gate)
        {
            previous = _lastKnown;
            _lastKnown = running;
        }

        if (previous is null || previous == running) return;

        if (running) Started?.Invoke();
        else Exited?.Invoke();
    }

    public void Dispose() => Stop();
}
