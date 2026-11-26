using System.Globalization;
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
/// that points at it. Changed tables get column level ADD, ALTER and DROP COLUMN statements.
/// Removed tables are dropped in the reverse order of the target, so the owner of a foreign key
/// goes before the table it references.
///
/// Steps that can lose data (dropping a column or a table, narrowing a type) or fail on a table
/// with rows (a NOT NULL column without a default) are written and also reported in
/// <see cref="TableScriptResult.DataLossActions"/>, so nothing risky goes through unnoticed.
///
/// Out of scope, reported as skipped: a change of column order (it needs a table rebuild with a
/// data copy) and IDENTITY or computed column changes.
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
        var changes = new List<ObjectDiff>();

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
                case DiffKind.Changed:
                    changes.Add(diff);
                    break;
            }
        }

        var sb = new StringBuilder(8192);

        var orderedCreates = DependencyOrder.Order(creates, result.Source.References, comparer, out var hadCycle);
        foreach (var key in orderedCreates)
            EmitCreate(sb, key, result, included, skipped);

        foreach (var diff in changes.OrderBy(d => d.Key.Schema, StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(d => d.Key.Name, StringComparer.OrdinalIgnoreCase))
            EmitAlter(sb, diff, result, included, skipped, gated);

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

    // ALTER (column level)

    private static void EmitAlter(
        StringBuilder sb, ObjectDiff diff, CompareResult result,
        List<ObjectKey> included, List<SkippedObject> skipped, List<GatedAction> gated)
    {
        var key = diff.Key;
        var source = result.Source.Objects[key];
        var target = result.Target.Objects[key];

        if (source.Columns is null || target.Columns is null)
        {
            skipped.Add(new SkippedObject(key, "no column information, a structural change cannot be scripted"));
            return;
        }

        var qualified = Qualified(key);
        var rows = target.RowCount;

        var sourceByName = source.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var targetByName = target.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        // Column statements are collected separately and written as DROP, ALTER, ADD. Dropping
        // first keeps a wide table under the row size (8060 bytes) and column count (1024) limits,
        // and a column that is dropped and added under the same name does not collide.
        var alters = new List<string>();
        var adds = new List<string>();
        var dropColumns = new List<string>();
        var localSkips = new List<string>();

        // ADD: in the source, not on the target.
        foreach (var column in source.Columns)
        {
            if (targetByName.ContainsKey(column.Name)) continue;

            if (column.IsComputed)
            {
                localSkips.Add($"{Id(column.Name)} is a new computed column. Its expression is not kept in the " +
                               "snapshot, so add it by hand or recreate the table");
                continue;
            }

            var defaultClause = column.DefaultDefinition is not null ? $" DEFAULT {column.DefaultDefinition}" : string.Empty;
            var sql = $"ALTER TABLE {qualified} ADD {Id(column.Name)} {RenderType(column)}" +
                      $" {Nullability(column)}{defaultClause};";
            adds.Add(sql);

            // A NOT NULL column without a default cannot be added to a table that has rows.
            if (!column.IsNullable && column.DefaultDefinition is null && !column.IsIdentity && rows is > 0)
                gated.Add(new GatedAction(key, column.Name,
                    $"NOT NULL column without a default is added to a table with {rows} rows, it needs a backfill first", sql));
        }

        // DROP: on the target, not in the source. The data in the column is lost.
        foreach (var column in target.Columns)
        {
            if (sourceByName.ContainsKey(column.Name)) continue;

            var sql = $"ALTER TABLE {qualified} DROP COLUMN {Id(column.Name)};";
            dropColumns.Add(sql);
            gated.Add(new GatedAction(key, column.Name, $"Column is dropped ({column.TypeDisplay}), its data is lost", sql));
        }

        // ALTER: on both sides but different.
        foreach (var column in source.Columns)
        {
            if (!targetByName.TryGetValue(column.Name, out var current)) continue;
            if (ColumnEquivalent(column, current)) continue;

            if (column.IsIdentity != current.IsIdentity || column.IsComputed || current.IsComputed)
            {
                localSkips.Add($"{Id(column.Name)} changes IDENTITY or a computed column, which ALTER COLUMN " +
                               "cannot do. The table has to be recreated");
                continue;
            }

            var sql = $"ALTER TABLE {qualified} ALTER COLUMN {Id(column.Name)} {RenderType(column)} {Nullability(column)};";
            alters.Add(sql);

            switch (ClassifyAlter(column, current, rows))
            {
                case AlterRisk.BlockedIfNotEmpty:
                    gated.Add(new GatedAction(key, column.Name,
                        $"{current.TypeDisplay} {Nullability(current)} to {column.TypeDisplay} {Nullability(column)} " +
                        $"can fail on a table with {rows} rows", sql));
                    break;
                case AlterRisk.DataLoss:
                    gated.Add(new GatedAction(key, column.Name,
                        $"{current.TypeDisplay} to {column.TypeDisplay} narrows the column, data can be truncated", sql));
                    break;
            }
        }

        foreach (var reason in localSkips)
            skipped.Add(new SkippedObject(key, reason));

        var ordered = dropColumns.Concat(alters).Concat(adds).ToList();

        if (ordered.Count == 0)
        {
            if (localSkips.Count == 0)
            {
                skipped.Add(IsColumnOrderOnly(source.Columns, target.Columns)
                    ? new SkippedObject(key,
                        "only the column order differs. SQL Server cannot reorder columns with ALTER, the table " +
                        "has to be recreated with a data copy, or turn on IgnoreColumnOrder when the order does not matter")
                    : new SkippedObject(key,
                        $"no column level change (changed parts: {string.Join(", ", diff.ChangedParts)}), review it by hand"));
            }
            return;
        }

        foreach (var statement in ordered) sb.AppendLine(statement);
        sb.AppendLine("GO");
        sb.AppendLine();
        included.Add(key);
    }

    // Risk classification

    private enum AlterRisk { Safe, BlockedIfNotEmpty, DataLoss }

    private static AlterRisk ClassifyAlter(ColumnInfo source, ColumnInfo target, long? rows)
    {
        // NULL to NOT NULL fails when existing rows hold NULL.
        if (!source.IsNullable && target.IsNullable && rows is > 0)
            return AlterRisk.BlockedIfNotEmpty;

        var sameType = string.Equals(source.TypeName, target.TypeName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(source.TypeSchema, target.TypeSchema, StringComparison.OrdinalIgnoreCase);

        if (!sameType) return AlterRisk.DataLoss;   // any type change we cannot prove safe

        if (target.MaxLength == -1 && source.MaxLength != -1) return AlterRisk.DataLoss;   // MAX to fixed
        if (source.MaxLength == -1) return AlterRisk.Safe;                                 // to MAX
        if (source.MaxLength != target.MaxLength)
            return source.MaxLength > target.MaxLength ? AlterRisk.Safe : AlterRisk.DataLoss;
        if (source.Precision != target.Precision || source.Scale != target.Scale)
            return source.Precision >= target.Precision && source.Scale >= target.Scale
                ? AlterRisk.Safe : AlterRisk.DataLoss;

        return AlterRisk.Safe;   // only nullability or collation differs
    }

    /// <summary>
    /// True when both tables have the same columns with the same definitions in a different order.
    /// That is the only difference then, and SQL Server cannot change it with ALTER.
    /// </summary>
    private static bool IsColumnOrderOnly(IReadOnlyList<ColumnInfo> source, IReadOnlyList<ColumnInfo> target)
    {
        if (source.Count != target.Count) return false;

        var byName = target.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var column in source)
        {
            if (!byName.TryGetValue(column.Name, out var other)) return false;
            if (!ColumnEquivalent(column, other)) return false;
            if (!string.Equals(column.DefaultDefinition, other.DefaultDefinition, StringComparison.Ordinal)) return false;
        }

        for (var i = 0; i < source.Count; i++)
            if (!string.Equals(source[i].Name, target[i].Name, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>Equality on the properties ALTER COLUMN can change: type, length, precision, nullability and collation.</summary>
    private static bool ColumnEquivalent(ColumnInfo a, ColumnInfo b) =>
        string.Equals(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.TypeSchema, b.TypeSchema, StringComparison.OrdinalIgnoreCase) &&
        a.MaxLength == b.MaxLength && a.Precision == b.Precision && a.Scale == b.Scale &&
        a.IsNullable == b.IsNullable && a.IsIdentity == b.IsIdentity && a.IsComputed == b.IsComputed &&
        string.Equals(a.Collation, b.Collation, StringComparison.OrdinalIgnoreCase) &&
        // The collection of a typed XML column is part of its type, a change needs ALTER COLUMN.
        string.Equals(a.XmlCollection, b.XmlCollection, StringComparison.OrdinalIgnoreCase) &&
        a.IsXmlDocument == b.IsXmlDocument;

    // Type rendering

    private static string RenderType(ColumnInfo column)
    {
        if (!string.Equals(column.TypeSchema, "sys", StringComparison.OrdinalIgnoreCase))
            return $"{Id(column.TypeSchema)}.{Id(column.TypeName)}";

        // A typed XML column written without its collection would be a different column.
        if (column.XmlTypeSuffix is { } xml) return $"{column.TypeName.ToLowerInvariant()}{xml}";

        var name = column.TypeName.ToLowerInvariant();
        var length = column.MaxLength.ToString(CultureInfo.InvariantCulture);

        var rendered = name switch
        {
            "varchar" or "char" or "varbinary" or "binary" =>
                column.MaxLength == -1 ? $"{name}(max)" : $"{name}({length})",
            "nvarchar" or "nchar" =>
                column.MaxLength == -1 ? $"{name}(max)" : $"{name}({column.MaxLength / 2})",
            "decimal" or "numeric" => $"{name}({column.Precision},{column.Scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{name}({column.Scale})",
            "float" => column.Precision == 53 ? "float" : $"float({column.Precision})",
            _ => name,
        };

        // COLLATE only applies to character types.
        if (column.Collation is not null && name is "varchar" or "char" or "nvarchar" or "nchar" or "text" or "ntext")
            rendered += $" COLLATE {column.Collation}";

        return rendered;
    }

    private static string Nullability(ColumnInfo column) => column.IsNullable ? "NULL" : "NOT NULL";

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
