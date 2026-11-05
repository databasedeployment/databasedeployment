namespace SchemaDiff.Core.Extraction;

/// <summary>
/// Limits the number of concurrent catalog queries across every comparison that shares it.
/// A single database comparison opens one query per object class on both sides. Comparing
/// all layers of a data warehouse in parallel would open hundreds of connections at once and
/// overload the server.
///
/// The scope of a gate is chosen by the caller:
///
/// <list type="bullet">
/// <item>CLI: every job in one run shares a single gate, so running seven layers in parallel
/// still stays within <c>--max-queries</c> (16 by default).</item>
/// <item>Web: each comparison session opens its own gate. Users compare several layers in
/// several tabs at the same time and a shared gate would make the tabs wait for each other.
/// The cost is that the limit is multiplied by the number of tabs, so MaxQueries per tab is
/// lowered on servers that cannot take it.</item>
/// </list>
/// </summary>
public sealed class ExtractionGate(int maxConcurrent) : IDisposable
{
    public static readonly ExtractionGate Unbounded = new(int.MaxValue);

    private readonly SemaphoreSlim _semaphore = new(maxConcurrent, maxConcurrent);

    public int MaxConcurrent { get; } = maxConcurrent;

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
