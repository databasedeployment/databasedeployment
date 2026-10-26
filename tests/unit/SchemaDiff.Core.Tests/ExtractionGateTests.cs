using SchemaDiff.Core.Extraction;

namespace SchemaDiff.Core.Tests;

public class ExtractionGateTests
{
    [Fact]
    public async Task Never_runs_more_queries_than_the_limit_at_once()
    {
        using var gate = new ExtractionGate(3);
        var running = 0;
        var peak = 0;

        var tasks = Enumerable.Range(0, 20).Select(_ => gate.RunAsync(async () =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(10);
            Interlocked.Decrement(ref running);
            return now;
        }, CancellationToken.None));

        await Task.WhenAll(tasks);

        Assert.InRange(peak, 1, 3);
    }

    [Fact]
    public async Task A_failing_query_releases_its_slot()
    {
        using var gate = new ExtractionGate(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync<int>(() => throw new InvalidOperationException("lost connection"), CancellationToken.None));

        // If the slot leaked, this call would wait forever.
        var value = await gate.RunAsync(() => Task.FromResult(42), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task Waiting_for_a_slot_can_be_cancelled()
    {
        using var gate = new ExtractionGate(1);
        var release = new TaskCompletionSource();
        var busy = gate.RunAsync(async () => { await release.Task; return 0; }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.RunAsync(() => Task.FromResult(1), cts.Token));

        release.SetResult();
        await busy;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current) return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
