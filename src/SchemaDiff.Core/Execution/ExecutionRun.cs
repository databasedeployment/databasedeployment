namespace SchemaDiff.Core.Execution;

/// <summary>
/// One execution of a script. Messages are collected here and streamed to the browser over SSE.
/// The run finishes in the background even if the client disconnects, so a deployment is never
/// left half done because a tab was closed.
/// </summary>
public sealed class ExecutionRun
{
    private readonly object _gate = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ExecutionMessage> _messages = [];
    private readonly CancellationTokenSource _cts = new();

    public int TotalBatches { get; private set; }
    public int DoneBatches { get; private set; }
    public bool Finished { get; private set; }
    public bool Success { get; private set; }
    public bool Cancelled { get; private set; }

    /// <summary>Passed to the executor. Commands listen to it and stop the current batch when it fires.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Stops the run. The running batch is cancelled and the open transaction is rolled back.
    /// The token is cancelled outside the lock so a synchronous cancellation callback cannot deadlock on it.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (Finished) return;
            Cancelled = true;
        }
        _cts.Cancel();
    }

    /// <summary>Completes when anything changes. Listeners await it and then read the new messages.</summary>
    public Task Changed
    {
        get { lock (_gate) return _signal.Task; }
    }

    public void SetTotal(int batches)
    {
        lock (_gate) { TotalBatches = batches; Signal(); }
    }

    /// <summary>Messages from index <paramref name="from"/> onwards.</summary>
    public List<ExecutionMessage> MessagesFrom(int from)
    {
        lock (_gate) return from >= _messages.Count ? [] : _messages[from..];
    }

    public void Push(ExecutionMessage message)
    {
        lock (_gate) { _messages.Add(message); Signal(); }
    }

    public void BatchDone()
    {
        lock (_gate) { DoneBatches++; Signal(); }
    }

    public void Complete(bool success)
    {
        lock (_gate) { Finished = true; Success = success; Signal(); }
    }

    private void Signal()
    {
        var previous = _signal;
        _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
}
