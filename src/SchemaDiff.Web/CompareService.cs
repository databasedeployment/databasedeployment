using System.Collections.Concurrent;
using System.Diagnostics;
using SchemaDiff.Core;
using SchemaDiff.Core.Connections;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Normalization;

namespace SchemaDiff.Web;

/// <summary>Runs one comparison. The real one connects to SQL Server, tests pass a fake.</summary>
public delegate Task<CompareResult> CompareRunner(
    string sourceConnectionString,
    string targetConnectionString,
    SnapshotOptions options,
    ExtractionGate gate,
    CompareProgress progress,
    CancellationToken ct);

/// <summary>
/// State of one comparison, owned by one browser tab. The SSE stream reads it, the background
/// task writes it, so every change goes through a lock and wakes up the waiting stream.
/// </summary>
public sealed class CompareSession
{
    private readonly object _gate = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Token handed to the comparison task. Extraction stops when it is cancelled.</summary>
    public CancellationToken CancellationToken => _cts.Token;

    public bool Cancelled { get; private set; }

    private readonly TimeProvider _time;

    public CompareSession(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        CreatedUtc = LastSeenUtc = _time.GetUtcNow();
    }

    public required string Id { get; init; }
    public required string SourceLabel { get; init; }
    public required string TargetLabel { get; init; }

    public DateTimeOffset CreatedUtc { get; }

    /// <summary>
    /// Last time the tab was heard from. An open tab refreshes it regularly through
    /// <c>/api/runs/{id}/touch</c>. When the browser crashes or is killed no close notification
    /// arrives, and this is the only signal that keeps such a session from living forever.
    /// </summary>
    public DateTimeOffset LastSeenUtc { get; private set; }

    public void Touch() => LastSeenUtc = _time.GetUtcNow();

    public CompareResult? Comparison { get; private set; }
    public CompareResultDto? Dto { get; private set; }
    public string? Error { get; private set; }
    public bool Finished { get; private set; }

    // Progress, sent to the browser in the SSE "progress" event.
    public int Percent { get; private set; }
    public int? EtaSeconds { get; private set; }
    public string Phase { get; private set; } = "connecting";

    /// <summary>Completes on the next change. The stream waits on it instead of polling.</summary>
    public Task Changed
    {
        get { lock (_gate) return _signal.Task; }
    }

    /// <summary>
    /// Updates the progress. It only signals when the percentage or the phase really changed,
    /// otherwise every finished query would trigger an event. The percentage never goes back.
    /// </summary>
    public void ReportProgress(int percent, int? etaSeconds, string phase)
    {
        lock (_gate)
        {
            if (Finished) return;
            percent = Math.Clamp(percent, 0, 100);
            if (percent < Percent) percent = Percent;
            var changed = percent != Percent || phase != Phase;
            Percent = percent;
            EtaSeconds = etaSeconds;
            Phase = phase;
            if (changed) Signal();
        }
    }

    public void Complete(CompareResult comparison, CompareResultDto dto)
    {
        lock (_gate)
        {
            Comparison = comparison;
            Dto = dto;
            Percent = 100;
            Phase = "done";
            Finished = true;
            Signal();
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            Error = error;
            Finished = true;
            Signal();
        }
    }

    /// <summary>
    /// Cancels a running comparison. The background task stops with an OperationCanceledException
    /// and finishes the session as cancelled. The token is cancelled outside the lock, so a
    /// synchronous cancellation callback cannot deadlock on it.
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

    /// <summary>
    /// Lets go of the heavy state (two full snapshots and the result tree). Removing the session
    /// from the store is usually enough, but a request that still holds the session object would
    /// keep the snapshots alive with it. Clearing the fields frees the memory in that case too.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            Comparison = null;
            Dto = null;
        }
    }

    private void Signal()
    {
        var previous = _signal;
        _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
}

/// <summary>
/// Per tab session store. Each comparison gets a session that lives until its tab closes. The
/// number of sessions is not limited on purpose: comparing several layers in several tabs at the
/// same time is normal use, and a cap would throw away the result of a tab that is still open.
/// </summary>
public sealed class CompareService(CompareRunner? runner = null, TimeProvider? time = null)
{
    /// <summary>
    /// Upper limit of changes sent to the browser. When it is exceeded the result says so,
    /// cutting the list silently would look like "that is all".
    /// </summary>
    public const int MaxChanges = 5000;

    /// <summary>
    /// How long a session without a close notification stays in memory. An open tab touches its
    /// session every minute, so this only applies after a browser crash or a forced close. It is
    /// long on purpose: cutting off a running comparison or a result under review is worse.
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly CompareRunner _runner = runner ?? RunOnServerAsync;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, CompareSession> _sessions = new();

    public int Count => _sessions.Count;

    public CompareSession? Get(string id) => _sessions.GetValueOrDefault(id);

    /// <summary>
    /// The tab was closed. Everything the session holds is released. The browser calls this with
    /// <c>sendBeacon</c> when the tab closes, and also on reload, because the page does not keep
    /// the run id and a reloaded page can never reach that session again.
    ///
    /// A comparison that is still running is cancelled. Nobody is left to read its result, and
    /// going on would keep both the memory and the SQL Server busy for nothing.
    /// </summary>
    public bool Dispose(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return false;
        if (!session.Finished) session.Cancel();
        session.Release();
        return true;
    }

    /// <summary>
    /// Removes sessions that never sent a close notification and were not heard from for
    /// <see cref="IdleTimeout"/>, the safety net for crashed or killed browsers. It runs when a new
    /// comparison starts instead of on a timer: whoever opens a new session is also the user
    /// whose old sessions should go.
    /// </summary>
    private void SweepIdleSessions()
    {
        var deadline = _time.GetUtcNow() - IdleTimeout;
        foreach (var session in _sessions.Values)
            if (session.LastSeenUtc < deadline)
                Dispose(session.Id);
    }

    public CompareSession Start(SqlConnectionInfo source, SqlConnectionInfo target, CompareOptionsDto options)
    {
        SweepIdleSessions();

        var session = new CompareSession(_time)
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            SourceLabel = source.Label,
            TargetLabel = target.Label,
        };
        _sessions[session.Id] = session;

        var snapshotOptions = ToSnapshotOptions(options);
        var sourceConnectionString = source.ToConnectionString();
        var targetConnectionString = target.ToConnectionString();

        // Runs in the background and completes even if no client listens to the stream.
        _ = Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var progress = new CompareProgress(snapshot =>
            {
                var (percent, eta) = ProgressMath.Compute(snapshot, stopwatch.Elapsed);
                session.ReportProgress(percent, eta, snapshot.Phase.ToString().ToLowerInvariant());
            });

            try
            {
                using var gate = new ExtractionGate(Math.Max(1, options.MaxQueries));
                var comparison = await _runner(
                    sourceConnectionString, targetConnectionString, snapshotOptions, gate, progress,
                    session.CancellationToken);

                session.Complete(comparison, Map(session, comparison, stopwatch.Elapsed));
            }
            catch (Exception ex)
            {
                // A cancelled query may also throw a SqlException instead of an OperationCanceledException.
                // The token is reliable across threads, so it decides whether this was a cancellation.
                session.Fail(session.CancellationToken.IsCancellationRequested
                    ? "The comparison was cancelled."
                    : ex.Message.Split('\n')[0].Trim());
            }
        });

        return session;
    }

    private static SnapshotOptions ToSnapshotOptions(CompareOptionsDto options) => new()
    {
        Normalization = new NormalizationOptions(
            IgnoreWhitespace: options.IgnoreWhitespace,
            IgnoreComments: options.IgnoreComments,
            IgnoreSemicolons: options.IgnoreSemicolons,
            IgnoreKeywordCasing: options.IgnoreKeywordCasing),
        IgnoreSystemNamedConstraints = options.IgnoreSystemNamedConstraints,
        IgnoreIndexPhysicalOptions = options.IgnoreIndexPhysical,
        IgnoreFillFactor = options.IgnoreFillFactor,
        IgnoreIndexPadding = options.IgnoreIndexPadding,
        IgnoreDataCompression = options.IgnoreDataCompression,
        IgnoreColumnOrder = options.IgnoreColumnOrder,
        IgnoreCollation = options.IgnoreCollation,
        IgnoreIdentitySeed = options.IgnoreIdentitySeed,
        IgnoreIdentityIncrement = options.IgnoreIdentityIncrement,
        IgnoreAnsiNulls = options.IgnoreAnsiNulls,
        IgnoreQuotedIdentifiers = options.IgnoreQuotedIdentifiers,
        IgnoreDmlTriggerState = options.IgnoreDmlTriggerState,
        CaseSensitiveNames = options.CaseSensitiveNames,
        CaseSensitiveColumnNames = options.CaseSensitiveColumnNames,
        // The compare view shows the full text of a changed object next to the other side.
        KeepDisplayScripts = true,
    };

    private static CompareResultDto Map(CompareSession session, CompareResult comparison, TimeSpan duration)
    {
        var changes = comparison.Differences
            .Take(MaxChanges)
            .Select(d => new ObjectDiffDto(
                Action(d.Kind), ObjectKindLabels.Display(d.Key.Kind), d.Key.Schema, d.Key.Name, [.. d.ChangedParts]))
            .ToArray();

        var warnings = comparison.Source.Report.Warnings
            .Select(w => $"{comparison.Source.Database}: {w}")
            .Concat(comparison.Target.Report.Warnings.Select(w => $"{comparison.Target.Database}: {w}"))
            .ToArray();

        return new CompareResultDto(
            session.Id, session.SourceLabel, session.TargetLabel,
            comparison.Source.Count, comparison.Target.Count, comparison.EqualCount,
            comparison.AddedCount, comparison.ChangedCount, comparison.RemovedCount, comparison.IndeterminateCount,
            duration.TotalMilliseconds,
            warnings,
            changes,
            comparison.Differences.Count > MaxChanges);
    }

    private static string Action(DiffKind kind) => kind switch
    {
        DiffKind.Added => "Add",
        DiffKind.Removed => "Delete",
        DiffKind.Changed => "Change",
        DiffKind.Indeterminate => "Indeterminate",
        _ => kind.ToString(),
    };

    private static Task<CompareResult> RunOnServerAsync(
        string sourceConnectionString, string targetConnectionString, SnapshotOptions options,
        ExtractionGate gate, CompareProgress progress, CancellationToken ct) =>
        SchemaDiffService.CompareAsync(sourceConnectionString, targetConnectionString, options, gate, ct, progress);
}
