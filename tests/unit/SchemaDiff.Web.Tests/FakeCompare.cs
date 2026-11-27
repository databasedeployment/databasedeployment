using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Web.Tests;

/// <summary>
/// Stands in for the real comparison. The source server name decides what happens:
/// "fails" throws, a name starting with "slow" reports some progress and then waits until it is
/// cancelled, anything else returns a small result right away.
/// </summary>
public sealed class FakeCompare
{
    public ManualClock Clock { get; } = new();

    public ConcurrentQueue<(string Source, string Target, SnapshotOptions Options, int MaxQueries)> Calls { get; } = new();

    private readonly ConcurrentDictionary<string, TaskCompletionSource> _cancelled = new(StringComparer.Ordinal);

    /// <summary>Completes when the slow comparison of the given source server noticed that it was cancelled.</summary>
    public Task Cancelled(string server) => CancelSignal(server).Task;

    private TaskCompletionSource CancelSignal(string server) =>
        _cancelled.GetOrAdd(server, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public async Task<CompareResult> RunAsync(
        string source, string target, SnapshotOptions options, ExtractionGate gate, CompareProgress progress,
        CancellationToken ct)
    {
        Calls.Enqueue((source, target, options, gate.MaxConcurrent));

        var server = new SqlConnectionStringBuilder(source).DataSource;
        switch (server)
        {
            case "fails":
                throw new InvalidOperationException("Login failed for user 'deployer'.\nSecond line of the error.");

            case not null when server.StartsWith("slow", StringComparison.Ordinal):
                progress.EnterPhase(ComparePhase.Extracting);
                progress.AddQueries(4);
                progress.QueryCompleted();
                progress.QueryCompleted();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    CancelSignal(server).TrySetResult();
                    throw;
                }
                break;
        }

        progress.EnterPhase(ComparePhase.Done);
        return Result();
    }

    private static CompareResult Result()
    {
        static ObjectSnapshot Obj(string name, ObjectKind kind, UInt128 hash) =>
            new() { Key = new ObjectKey("dbo", name, kind), Hash = hash };

        static DatabaseSnapshot Db(string name, params ObjectSnapshot[] objects)
        {
            var dict = new Dictionary<ObjectKey, ObjectSnapshot>(ObjectKeyComparer.CaseInsensitive);
            foreach (var o in objects) dict[o.Key] = o;
            return new DatabaseSnapshot { Server = "srv", Database = name, Objects = dict };
        }

        var changed = Obj("Customer", ObjectKind.Table, 2);
        changed.Parts["columns"] = 20;
        var before = Obj("Customer", ObjectKind.Table, 1);
        before.Parts["columns"] = 10;

        var source = Db("Sales", changed, Obj("NewView", ObjectKind.View, 3), Obj("Same", ObjectKind.Procedure, 4));
        var target = Db("Sales", before, Obj("OldProc", ObjectKind.Procedure, 5), Obj("Same", ObjectKind.Procedure, 4));
        target.Report.Warnings.Add("row counts could not be read");

        return SchemaComparer.Compare(source, target);
    }
}

/// <summary>A clock that only moves when a test moves it.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
