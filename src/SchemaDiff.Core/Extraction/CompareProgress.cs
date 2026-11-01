namespace SchemaDiff.Core.Extraction;

/// <summary>Coarse phases of a comparison. They only move forward.</summary>
public enum ComparePhase
{
    Connecting = 0,
    Extracting = 1,
    Building = 2,
    Comparing = 3,
    Done = 4,
}

/// <summary>
/// Point in time view of the progress. Percentage and remaining time are left to the caller.
/// There are two kinds of work: extraction queries and modules normalized while building the
/// model. Against a remote database the queries dominate, locally the module parsing does.
/// </summary>
public sealed record ProgressSnapshot(
    ComparePhase Phase, int QueriesDone, int QueriesTotal, int BuildDone, int BuildTotal);

/// <summary>
/// Collects comparison progress. Source and target run their queries in parallel, so all
/// counters live in one instance and are updated lock-free with Interlocked.
///
/// The total number of queries is known when they are queued, and each finished query moves
/// the counter. Percentage and remaining time are derived from these raw numbers in the web
/// layer, which keeps Core deterministic and independent of the UI.
/// </summary>
public sealed class CompareProgress
{
    private readonly Action<ProgressSnapshot>? _onChange;
    private int _queriesTotal;
    private int _queriesDone;
    private int _buildTotal;
    private int _buildDone;
    private int _phase; // ComparePhase, only ever moved forward

    public CompareProgress(Action<ProgressSnapshot>? onChange = null) => _onChange = onChange;

    /// <summary>Instance that reports nothing, for callers that do not need progress.</summary>
    public static CompareProgress None { get; } = new();

    /// <summary>Adds queued queries. Source and target each add their own share.</summary>
    public void AddQueries(int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _queriesTotal, count);
        Notify();
    }

    /// <summary>A query finished, whether it succeeded or an optional query failed.</summary>
    public void QueryCompleted()
    {
        Interlocked.Increment(ref _queriesDone);
        Notify();
    }

    /// <summary>Adds modules that will be normalized while building the model.</summary>
    public void AddBuildItems(int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _buildTotal, count);
        Notify();
    }

    /// <summary>One module was normalized.</summary>
    public void BuildItemCompleted()
    {
        Interlocked.Increment(ref _buildDone);
        Notify();
    }

    /// <summary>Moves to a later phase. If one side gets ahead of the other, the furthest
    /// phase is kept and the phase never moves back.</summary>
    public void EnterPhase(ComparePhase phase)
    {
        var target = (int)phase;
        int current;
        do
        {
            current = Volatile.Read(ref _phase);
            if (target <= current) return;
        }
        while (Interlocked.CompareExchange(ref _phase, target, current) != current);
        Notify();
    }

    public ProgressSnapshot Current => new(
        (ComparePhase)Volatile.Read(ref _phase),
        Volatile.Read(ref _queriesDone),
        Volatile.Read(ref _queriesTotal),
        Volatile.Read(ref _buildDone),
        Volatile.Read(ref _buildTotal));

    private void Notify() => _onChange?.Invoke(Current);
}
