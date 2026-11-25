using System.Diagnostics;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Diff;

/// <summary>
/// Compares two snapshots by hash. This is where the speed comes from: the text of objects
/// with equal hashes is never looked at. Between dev and prod usually more than 95% of the
/// objects are equal, so the expensive work only applies to the rest.
///
/// Direction: the source is what the schema should be, the target is what gets deployed to.
/// Added   = in the source, not in the target, so it will be created on the target.
/// Removed = in the target, not in the source, so it will be dropped from the target.
/// </summary>
public static class SchemaComparer
{
    public static CompareResult Compare(DatabaseSnapshot source, DatabaseSnapshot target)
    {
        var sw = Stopwatch.StartNew();
        var differences = new List<ObjectDiff>();
        var equalCount = 0;

        foreach (var (key, sourceObject) in source.Objects)
        {
            if (!target.Objects.TryGetValue(key, out var targetObject))
            {
                differences.Add(new ObjectDiff(key, DiffKind.Added, []));
                continue;
            }

            if (sourceObject.Hash == targetObject.Hash)
            {
                equalCount++;
                continue;
            }

            differences.Add(new ObjectDiff(key, DiffKind.Changed, []));
        }

        foreach (var (key, _) in target.Objects)
        {
            if (!source.Objects.ContainsKey(key))
                differences.Add(new ObjectDiff(key, DiffKind.Removed, []));
        }

        differences.Sort(static (a, b) =>
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            if (byKind != 0) return byKind;
            var byType = a.Key.Kind.CompareTo(b.Key.Kind);
            if (byType != 0) return byType;
            var bySchema = string.Compare(a.Key.Schema, b.Key.Schema, StringComparison.OrdinalIgnoreCase);
            return bySchema != 0 ? bySchema
                : string.Compare(a.Key.Name, b.Key.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new CompareResult
        {
            Source = source,
            Target = target,
            Differences = differences,
            EqualCount = equalCount,
            CompareTime = sw.Elapsed,
        };
    }
}
