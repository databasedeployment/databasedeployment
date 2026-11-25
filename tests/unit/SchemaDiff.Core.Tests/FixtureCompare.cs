using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Builds a comparison result from two fixture snapshots, so the script generators can be tested
/// without the diff engine. An object that only exists in the source is added, one that only exists
/// in the target is removed, and an object whose hash differs is changed. The changed parts are the
/// sub-parts whose hash differs or that exist on one side only.
/// </summary>
internal static class FixtureCompare
{
    public static CompareResult Compare(DatabaseSnapshot source, DatabaseSnapshot target)
    {
        var differences = new List<ObjectDiff>();
        var equal = 0;

        foreach (var (key, left) in source.Objects)
        {
            if (!target.Objects.TryGetValue(key, out var right))
                differences.Add(new ObjectDiff(key, DiffKind.Added, []));
            else if (left.Incomparable || right.Incomparable)
                differences.Add(new ObjectDiff(key, DiffKind.Indeterminate, []));
            else if (left.Hash != right.Hash)
                differences.Add(new ObjectDiff(key, DiffKind.Changed, ChangedParts(left, right)));
            else
                equal++;
        }

        foreach (var key in target.Objects.Keys)
            if (!source.Objects.ContainsKey(key))
                differences.Add(new ObjectDiff(key, DiffKind.Removed, []));

        return new CompareResult
        {
            Source = source,
            Target = target,
            Differences = differences,
            EqualCount = equal,
        };
    }

    private static List<string> ChangedParts(ObjectSnapshot left, ObjectSnapshot right)
    {
        var parts = new List<string>();
        foreach (var name in left.Parts.Keys.Union(right.Parts.Keys).Order(StringComparer.Ordinal))
        {
            var inLeft = left.Parts.TryGetValue(name, out var a);
            var inRight = right.Parts.TryGetValue(name, out var b);
            if (!inLeft || !inRight || a != b) parts.Add(name);
        }
        return parts;
    }
}
