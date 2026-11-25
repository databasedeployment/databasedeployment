using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record TableScriptOptions
{
    /// <summary>
    /// Drop tables that exist on the target but not in the source. A dropped table loses its
    /// rows, so every drop is also listed in <see cref="TableScriptResult.DataLossActions"/>.
    /// </summary>
    public bool IncludeDrops { get; init; } = true;

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
/// that points at it. Removed tables are dropped in the reverse order of the target, so the
/// owner of a foreign key goes before the table it references.
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
        var drops = new List<ObjectKey>();

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
                case DiffKind.Removed:
                    drops.Add(diff.Key);
                    break;
            }
        }

        var sb = new StringBuilder(8192);

        var orderedCreates = DependencyOrder.Order(creates, result.Source.References, comparer, out var hadCycle);
        foreach (var key in orderedCreates)
            EmitCreate(sb, key, result, included, skipped);

        // Removed tables exist on the target, so the target's reference graph decides the order.
        var orderedDrops = DependencyOrder.Order(drops, result.Target.References, comparer, out var dropCycle);
        orderedDrops.Reverse();
        hadCycle |= dropCycle;
        foreach (var key in orderedDrops)
            EmitDrop(sb, key, result, options, included, skipped, gated);

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

    // DROP

    private static void EmitDrop(
        StringBuilder sb, ObjectKey key, CompareResult result, TableScriptOptions options,
        List<ObjectKey> included, List<SkippedObject> skipped, List<GatedAction> gated)
    {
        if (!options.IncludeDrops)
        {
            skipped.Add(new SkippedObject(key, "drops are turned off, the table stays on the target"));
            return;
        }

        var rows = result.Target.Objects.TryGetValue(key, out var target) ? target.RowCount : null;
        var qualified = Qualified(key);
        var sql = $"DROP TABLE {qualified};";

        gated.Add(new GatedAction(key, "(table)",
            rows is > 0 ? $"Table is dropped, {rows} rows are lost" : "Table is dropped", sql));

        sb.AppendLine($"IF OBJECT_ID(N'{Literal(qualified)}', N'U') IS NOT NULL");
        sb.AppendLine($"    {sql}");
        sb.AppendLine("GO");
        sb.AppendLine();
        included.Add(key);
    }

    // Helpers

    /// <summary>Bracketed identifier. A closing bracket inside the name is doubled.</summary>
    private static string Id(string name) => "[" + name.Replace("]", "]]") + "]";

    private static string Qualified(ObjectKey key) => $"{Id(key.Schema)}.{Id(key.Name)}";

    /// <summary>Text for an N'...' literal, with single quotes doubled.</summary>
    private static string Literal(string value) => value.Replace("'", "''");
}
