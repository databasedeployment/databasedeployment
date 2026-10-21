namespace SchemaDiff.Core.Model;

public enum DiffKind { Equal, Added, Removed, Changed, Indeterminate }

/// <param name="ChangedParts">Names of the parts whose hash differs, for example "columns" or "body".</param>
public sealed record ObjectDiff(
    ObjectKey Key,
    DiffKind Kind,
    IReadOnlyList<string> ChangedParts);

public sealed class CompareResult
{
    public required DatabaseSnapshot Source { get; init; }
    public required DatabaseSnapshot Target { get; init; }
    public required List<ObjectDiff> Differences { get; init; }
    public int EqualCount { get; init; }
    public TimeSpan CompareTime { get; set; }

    public int AddedCount => Differences.Count(d => d.Kind == DiffKind.Added);
    public int RemovedCount => Differences.Count(d => d.Kind == DiffKind.Removed);
    public int ChangedCount => Differences.Count(d => d.Kind == DiffKind.Changed);
    public int IndeterminateCount => Differences.Count(d => d.Kind == DiffKind.Indeterminate);

    public int TotalCompared() => EqualCount + Differences.Count;
}
