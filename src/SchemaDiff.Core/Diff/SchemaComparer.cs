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

        // Keys always match case-insensitively. A difference in name casing ([GetCustomer] and
        // [getcustomer]) only counts when the case-sensitive names option is on, and then it shows
        // up as a single "name" change. With the option off the two spellings are equal.
        var reportNameCase = source.CaseSensitiveNames;

        foreach (var (key, sourceObject) in source.Objects)
        {
            if (!target.Objects.TryGetValue(key, out var targetObject))
            {
                differences.Add(new ObjectDiff(key, DiffKind.Added, []));
                continue;
            }

            // An object whose definition could not be read is never equal, even when the hashes
            // match. Both sides may hash the same placeholder text, and a silent "equal" is the
            // most dangerous result a comparison can give.
            if (sourceObject.Incomparable || targetObject.Incomparable)
            {
                differences.Add(new ObjectDiff(key, DiffKind.Indeterminate,
                    [sourceObject.IncomparableReason ?? targetObject.IncomparableReason ?? "unknown"]));
                continue;
            }

            var nameCaseDiff = reportNameCase &&
                (!string.Equals(key.Schema, targetObject.Key.Schema, StringComparison.Ordinal) ||
                 !string.Equals(key.Name, targetObject.Key.Name, StringComparison.Ordinal));

            if (sourceObject.Hash == targetObject.Hash)
            {
                if (!nameCaseDiff) { equalCount++; continue; }
                differences.Add(new ObjectDiff(key, DiffKind.Changed, ["name"]));
                continue;
            }

            var parts = ChangedParts(sourceObject, targetObject);
            if (nameCaseDiff && !parts.Contains("name")) parts.Add("name");
            differences.Add(new ObjectDiff(key, DiffKind.Changed, parts));
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

    /// <summary>
    /// Reads which parts of an object changed from the sub-part hashes, so "only the indexes of
    /// this table changed" is known without running a text diff. A part that exists on one side
    /// only counts as changed.
    /// </summary>
    private static List<string> ChangedParts(ObjectSnapshot source, ObjectSnapshot target)
    {
        var changed = new List<string>();

        foreach (var (part, hash) in source.Parts)
        {
            if (!target.Parts.TryGetValue(part, out var targetHash)) changed.Add(part);
            else if (hash != targetHash) changed.Add(part);
        }

        foreach (var (part, _) in target.Parts)
            if (!source.Parts.ContainsKey(part)) changed.Add(part);

        changed.Sort(StringComparer.Ordinal);
        return changed;
    }
}
