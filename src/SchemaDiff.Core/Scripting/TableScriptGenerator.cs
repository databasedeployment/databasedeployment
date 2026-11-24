using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record TableScriptOptions
{
    public static readonly TableScriptOptions Default = new();
}

/// <summary>
/// A step that can lose data or fail on a table that has rows. The generator writes the step
/// into the script and also lists it here, so the risk analyzer and safe mode can hold it back
/// or ask for confirmation before the script runs.
/// </summary>
/// <param name="Column">Affected column, or "(table)" when the whole table is affected.</param>
public sealed record GatedAction(ObjectKey Table, string Column, string Description, string Sql);

public sealed record TableScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<SkippedObject> Skipped,
    IReadOnlyList<GatedAction> DataLossActions,
    bool HadDependencyCycle)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>
/// Produces the deployment script for table differences. New tables are created from their
/// display script in foreign key order, so a referenced table always exists before the table
/// that points at it.
/// </summary>
public static class TableScriptGenerator
{
    public static TableScriptResult Generate(CompareResult result, TableScriptOptions? options = null)
    {
        options ??= TableScriptOptions.Default;
        var comparer = ObjectKeyComparer.CaseInsensitive;

        var included = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();
        var gated = new List<GatedAction>();

        var creates = new List<ObjectKey>();

        foreach (var diff in result.Differences)
        {
            if (diff.Key.Kind != ObjectKind.Table) continue;

            switch (diff.Kind)
            {
                case DiffKind.Indeterminate:
                    skipped.Add(new SkippedObject(diff.Key, "could not be compared because its definition is not readable"));
                    break;
                case DiffKind.Added:
                    creates.Add(diff.Key);
                    break;
            }
        }

        var sb = new StringBuilder(8192);

        var orderedCreates = DependencyOrder.Order(creates, result.Source.References, comparer, out var hadCycle);
        foreach (var key in orderedCreates)
            EmitCreate(sb, key, result, included, skipped);

        return new TableScriptResult(sb.ToString(), included, skipped, gated, hadCycle);
    }

    // CREATE

    private static void EmitCreate(
        StringBuilder sb, ObjectKey key, CompareResult result,
        List<ObjectKey> included, List<SkippedObject> skipped)
    {
        if (!result.Source.Objects.TryGetValue(key, out var snapshot) ||
            string.IsNullOrWhiteSpace(snapshot.DisplayScript))
        {
            skipped.Add(new SkippedObject(key, "no CREATE text, the snapshot was taken without a display script"));
            return;
        }

        // The display script can hold several batches (CREATE TABLE, GO, CREATE INDEX), so it
        // cannot be wrapped in IF ... BEGIN ... END. GO is a batch separator and is not valid
        // inside a block. A new table should not exist on the target, and if it does the
        // CREATE fails and the deployment stops there.
        sb.AppendLine(snapshot.DisplayScript!.TrimEnd());
        sb.AppendLine("GO");
        sb.AppendLine();
        included.Add(key);
    }
}
