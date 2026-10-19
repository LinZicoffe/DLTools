namespace 烧录上位机程序.Services;

public enum BackgroundOperationKind
{
    Flashing,
    Packaging,
    Authorization,
    IpScanning
}

/// <summary>Application-wide exclusive ownership, held until the actual operation task terminates.</summary>
public sealed class BackgroundTaskHost
{
    public static BackgroundTaskHost Shared { get; } = new();

    private readonly object _gate = new();
    private Task? _currentTask;
    private CancellationState? _cancellation;
    private BackgroundOperationKind? _currentKind;

    public bool IsBusy { get { lock (_gate) return _currentTask is { IsCompleted: false }; } }
    public Task? CurrentTask { get { lock (_gate) return _currentTask; } }
    public BackgroundOperationKind? CurrentKind
    {
        get { lock (_gate) return _currentTask is { IsCompleted: false } ? _currentKind : null; }
    }

    public bool TryStart(BackgroundOperationKind kind, Func<CancellationToken, Task> operation, out Task task)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationTokenSource? dispose = null;
        lock (_gate)
        {
            if (_currentTask is { IsCompleted: false })
            {
                task = _currentTask;
                return false;
            }

            // Retire only after terminal state, and defer disposal until all concurrent Cancel calls return.
            if (_cancellation is { } previous)
            {
                previous.Retired = true;
                if (previous.Requests == 0) dispose = previous.Source;
            }
            _cancellation = new CancellationState();
            var token = _cancellation.Source.Token;
            _currentKind = kind;
            task = _currentTask = Task.Run(() => operation(token));
        }
        dispose?.Dispose();
        return true;
    }

    public void RequestCancellation()
    {
        CancellationState? state;
        lock (_gate)
        {
            if (_currentTask is not { IsCompleted: false } || _cancellation is null) return;
            state = _cancellation;
            state.Requests++;
        }

        // Do not invoke arbitrary cancellation callbacks while holding the host gate.
        try { state.Source.Cancel(); }
        finally
        {
            bool dispose;
            lock (_gate)
            {
                state.Requests--;
                dispose = state.Retired && state.Requests == 0;
            }
            if (dispose) state.Source.Dispose();
        }
    }

    private sealed class CancellationState
    {
        public CancellationTokenSource Source { get; } = new();
        public int Requests;
        public bool Retired;
    }
}
