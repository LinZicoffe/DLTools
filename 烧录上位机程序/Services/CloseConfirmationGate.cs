namespace 烧录上位机程序.Services;

/// <summary>Serializes native close questions with asynchronous task confirmation prompts.</summary>
public sealed class CloseConfirmationGate
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool>? _pending;
    private bool _closing;

    public bool IsPending { get { lock (_sync) return _pending is not null; } }

    public bool TryBegin()
    {
        lock (_sync)
        {
            if (_pending is not null || _closing) return false;
            _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <param name="allowFurtherPrompts">False once exit has been accepted and cancellation requested.</param>
    public void Complete(bool allowFurtherPrompts)
    {
        TaskCompletionSource<bool>? pending;
        lock (_sync)
        {
            _closing = !allowFurtherPrompts;
            pending = _pending;
            _pending = null;
        }
        pending?.TrySetResult(allowFurtherPrompts);
    }

    public Task<bool> WaitAsync(CancellationToken cancellationToken = default)
    {
        Task<bool> decision;
        lock (_sync) decision = _pending?.Task ?? Task.FromResult(!_closing);
        return decision.WaitAsync(cancellationToken);
    }
}
