using System.Globalization;
using System.Text;
using SchemaDiff.Core.Model;
using static SchemaDiff.Core.Scripting.SqlName;

namespace SchemaDiff.Core.Scripting;

public sealed record TableScriptOptions
{
    /// <summary>
    /// Drop tables that exist on the target but not in the source. A dropped table loses its
    /// rows, so every drop is also listed in <see cref="TableScriptResult.DataLossActions"/>.
    /// </summary>
    public bool IncludeDrops { get; init; } = true;

    /// <summary>
    /// Check new CHECK and FOREIGN KEY constraints against the existing rows (WITH CHECK). When off,
    /// WITH NOCHECK is written: the constraint can be added to a table whose data breaks it, but it
    /// stays not trusted. SSDT calls this "script validation for new constraints".
    /// </summary>
    public bool ValidateNewConstraints { get; init; } = true;

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
/// that points at it. Changed tables get column level ADD, ALTER and DROP COLUMN statements and
/// the index, key, CHECK, DEFAULT, foreign key and statistics changes around them.
/// Removed tables are dropped in the reverse order of the target, so the owner of a foreign key
/// goes before the table it references.
///
/// Steps that can lose data (dropping a column or a table, narrowing a type) or fail on a table
/// with rows (a NOT NULL column without a default) are written and also reported in
/// <see cref="TableScriptResult.DataLossActions"/>, so nothing risky goes through unnoticed.
///
/// Foreign keys are collected for the whole script: every foreign key drop runs first and every
/// add runs last. A new foreign key therefore never waits for a table that is created or changed
/// later in the same script.
///
/// Out of scope, reported as skipped: a change of column order (it needs a table rebuild with a
/// data copy), IDENTITY or computed column changes, and switching a table to system versioning.
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

        var fkDrops = new List<string>();
        var fkAdds = new List<string>();
        var sb = new StringBuilder(8192);

        var orderedCreates = DependencyOrder.Order(creates, result.Source.References, comparer, out var hadCycle);
        foreach (var key in orderedCreates)
            EmitCreate(sb, key, result, included, skipped);

        foreach (var diff in changes.OrderBy(d => d.Key.Schema, StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(d => d.Key.Name, StringComparer.OrdinalIgnoreCase))
            EmitAlter(sb, diff, result, options, included, skipped, gated, fkDrops, fkAdds);

        // Removed tables exist on the target, so the target's reference graph decides the order.
        var orderedDrops = DependencyOrder.Order(drops, result.Target.References, comparer, out var dropCycle);
        orderedDrops.Reverse();
        hadCycle |= dropCycle;
        foreach (var key in orderedDrops)
            EmitDrop(sb, key, result, options, included, skipped, gated);

        var script = new StringBuilder(sb.Length + 1024);
        AppendBatch(script, fkDrops);   // first, so no foreign key blocks a column or index change
        script.Append(sb);
        AppendBatch(script, fkAdds);    // last, when every referenced table and key exists

        return new TableScriptResult(script.ToString(), included, skipped, gated, hadCycle);
    }

    private static void AppendBatch(StringBuilder sb, List<string> statements)
    {
        if (statements.Count == 0) return;
        foreach (var statement in statements) sb.AppendLine(statement);
        sb.AppendLine("GO");
        sb.AppendLine();
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
        StringBuilder sb, ObjectDiff diff, CompareResult result, TableScriptOptions options,
        List<ObjectKey> included, List<SkippedObject> skipped, List<GatedAction> gated,
        List<string> fkDrops, List<string> fkAdds)
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

        AppendRenames(key, result.Source.CaseSensitiveColumnNames, source.Columns, targetByName, out var renames);

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
                      $"{AddColumnAttributes(column)} {Nullability(column)}{defaultClause};";
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
        var retyped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            // The ALTER COLUMN grammar puts SPARSE after NULL or NOT NULL. It is written explicitly
            // because leaving it out does not guarantee the column stays sparse.
            var sparse = column.IsSparse ? " SPARSE" : string.Empty;
            var sql = $"ALTER TABLE {qualified} ALTER COLUMN {Id(column.Name)} {RenderType(column)} {Nullability(column)}{sparse};";
            alters.Add(sql);
            retyped.Add(column.Name);

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

        AppendColumnAttributeDiff(qualified, source.Columns, targetByName, retyped, alters, localSkips);

        // Index and constraint drops run before the column changes, creates after them.
        var pre = new List<string>();
        var post = new List<string>();
        var foreignKeyStatements = fkDrops.Count + fkAdds.Count;
        AppendIndexConstraintDiff(qualified, source, target, pre, post, fkDrops, fkAdds,
            options.ValidateNewConstraints, retyped);
        AppendStatisticsDiff(qualified, source, target, pre, post);
        AppendDefaultDiff(qualified, sourceByName, targetByName, pre, post);
        var hasForeignKeyChanges = fkDrops.Count + fkAdds.Count > foreignKeyStatements;

        // System versioning is turned off first, because a versioned table limits other schema changes.
        var versioning = new List<string>();
        AppendTemporalDiff(key, qualified, source, target, versioning, skipped);

        foreach (var reason in localSkips)
            skipped.Add(new SkippedObject(key, reason));

        // Order: rename, versioning off, index and constraint drops, DROP COLUMN,
        // ALTER COLUMN and attributes, ADD COLUMN, index and constraint creates.
        var ordered = renames.Concat(versioning).Concat(pre)
            .Concat(dropColumns).Concat(alters).Concat(adds).Concat(post).ToList();

        if (ordered.Count == 0 && hasForeignKeyChanges)
        {
            // Only foreign keys changed. Their statements are in the script wide sections.
            included.Add(key);
            return;
        }

        if (ordered.Count == 0)
        {
            if (localSkips.Count == 0)
            {
                skipped.Add(IsColumnOrderOnly(source.Columns, target.Columns)
                    ? new SkippedObject(key,
                        "only the column order differs. SQL Server cannot reorder columns with ALTER, the table " +
                        "has to be recreated with a data copy, or turn on IgnoreColumnOrder when the order does not matter")
                    : new SkippedObject(key,
                        $"no scriptable change (changed parts: {string.Join(", ", diff.ChangedParts)}), review it by hand"));
            }
            return;
        }

        foreach (var statement in ordered) sb.AppendLine(statement);
        sb.AppendLine("GO");
        sb.AppendLine();
        included.Add(key);
    }

    /// <summary>
    /// Columns that only differ in casing. Columns are matched case-insensitively, so such a column
    /// is neither added nor dropped and its type is the same. SQL Server keeps the name as written,
    /// and sp_rename is the only way to fix the casing. Renames run first so later statements see
    /// the new name. When column names are case-insensitive a casing difference is not a change.
    /// </summary>
    private static void AppendRenames(
        ObjectKey key, bool caseSensitiveColumns, IReadOnlyList<ColumnInfo> sourceColumns,
        Dictionary<string, ColumnInfo> targetByName, out List<string> renames)
    {
        renames = [];
        if (!caseSensitiveColumns) return;

        foreach (var column in sourceColumns)
        {
            if (!targetByName.TryGetValue(column.Name, out var current)) continue;
            if (string.Equals(column.Name, current.Name, StringComparison.Ordinal)) continue;

            renames.Add(
                $"EXEC sp_rename N'{RenameTarget(key.Schema)}.{RenameTarget(key.Name)}.{RenameTarget(current.Name)}', " +
                $"N'{Literal(column.Name)}', N'COLUMN';");
        }
    }

    /// <summary>
    /// System versioning. Turning it off is safe and scripted in full. Turning it on, or changing
    /// the history table or PERIOD, needs PERIOD columns with defaults and a history table, so it is
    /// reported for a manual step instead of writing a half working script.
    /// </summary>
    private static void AppendTemporalDiff(
        ObjectKey key, string qualified, ObjectSnapshot source, ObjectSnapshot target,
        List<string> versioning, List<SkippedObject> skipped)
    {
        var sourceTemporal = source.PartCanonical.GetValueOrDefault("temporal");
        var targetTemporal = target.PartCanonical.GetValueOrDefault("temporal");
        if (sourceTemporal == targetTemporal) return;

        if (sourceTemporal is null)
        {
            versioning.Add($"ALTER TABLE {qualified} SET (SYSTEM_VERSIONING = OFF);");
            versioning.Add($"ALTER TABLE {qualified} DROP PERIOD FOR SYSTEM_TIME;");
        }
        else if (targetTemporal is null)
        {
            skipped.Add(new SkippedObject(key,
                "the table becomes temporal. It needs GENERATED ALWAYS period columns and a history table, apply " +
                "ADD PERIOD FOR SYSTEM_TIME and SET SYSTEM_VERSIONING = ON by hand"));
        }
        else
        {
            skipped.Add(new SkippedObject(key,
                "the temporal history or PERIOD setup changes. Rebuilding it is risky, review it by hand"));
        }
    }

    /// <summary>
    /// Storage attributes of a new column, in the order of the T-SQL grammar. COLLATE is already
    /// part of <see cref="RenderType"/>, and FILESTREAM columns (varbinary(max)) take no collation.
    /// </summary>
    private static string AddColumnAttributes(ColumnInfo column)
    {
        var sb = new StringBuilder();
        if (column.IsFileStream) sb.Append(" FILESTREAM");
        if (column.IsSparse) sb.Append(" SPARSE");
        if (column.IsColumnSet) sb.Append(" COLUMN_SET FOR ALL_SPARSE_COLUMNS");
        if (column.IsRowGuidCol) sb.Append(" ROWGUIDCOL");

        // Without IDENTITY the column would be added as a plain column, which is silently wrong.
        if (column.IsIdentity)
        {
            sb.Append(" IDENTITY");
            if (column.IdentitySeed is not null && column.IdentityIncrement is not null)
                sb.Append('(').Append(column.IdentitySeed).Append(", ").Append(column.IdentityIncrement).Append(')');
            if (column.IdentityNotForReplication) sb.Append(" NOT FOR REPLICATION");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Storage attribute differences of columns that exist on both sides. SPARSE and ROWGUIDCOL can
    /// be switched with <c>ALTER COLUMN ... ADD|DROP</c>. FILESTREAM, COLUMN_SET and the NOT FOR
    /// REPLICATION flag of an IDENTITY cannot, so they are reported instead of scripted.
    /// </summary>
    /// <param name="retyped">Columns that already get a type level ALTER COLUMN, which carries SPARSE.</param>
    private static void AppendColumnAttributeDiff(
        string qualified,
        IReadOnlyList<ColumnInfo> sourceColumns,
        Dictionary<string, ColumnInfo> targetByName,
        HashSet<string> retyped,
        List<string> statements,
        List<string> localSkips)
    {
        foreach (var column in sourceColumns)
        {
            if (!targetByName.TryGetValue(column.Name, out var current)) continue;
            var name = Id(column.Name);

            if (column.IsRowGuidCol != current.IsRowGuidCol)
                statements.Add($"ALTER TABLE {qualified} ALTER COLUMN {name} {(column.IsRowGuidCol ? "ADD" : "DROP")} ROWGUIDCOL;");

            // A retyped column already carries SPARSE in its ALTER. Removing SPARSE cannot be said
            // there, because leaving it out does not mean "remove", so DROP SPARSE is written.
            if (column.IsSparse != current.IsSparse && !(retyped.Contains(column.Name) && column.IsSparse))
                statements.Add($"ALTER TABLE {qualified} ALTER COLUMN {name} {(column.IsSparse ? "ADD" : "DROP")} SPARSE;");

            if (column.IdentityNotForReplication != current.IdentityNotForReplication)
                localSkips.Add($"{name} IDENTITY NOT FOR REPLICATION is {(column.IdentityNotForReplication ? "added" : "removed")}, " +
                               "which ALTER cannot do. The table has to be recreated");

            if (column.IsFileStream != current.IsFileStream)
                localSkips.Add($"{name} FILESTREAM is {(column.IsFileStream ? "added" : "removed")}, " +
                               "which ALTER cannot do. The table has to be recreated");

            if (column.IsColumnSet != current.IsColumnSet)
                localSkips.Add($"{name} COLUMN_SET changes, which ALTER cannot do. The table has to be recreated");
        }
    }

    // Indexes and constraints

    /// <summary>
    /// Index and constraint differences of a changed table. The same name with a different
    /// definition means drop and recreate. Drops go into <paramref name="pre"/> and run before the
    /// column changes, because an index or constraint on a column blocks dropping or altering it.
    /// Creates go into <paramref name="post"/> and run after them, when new columns exist.
    /// Foreign keys go into the script wide lists instead, see <see cref="Generate"/>.
    /// </summary>
    /// <param name="alteredColumns">Columns that get an ALTER COLUMN. An unchanged index on such a
    /// column is dropped and created again, because SQL Server refuses the ALTER otherwise (Msg 5074).</param>
    private static void AppendIndexConstraintDiff(
        string qualified, ObjectSnapshot source, ObjectSnapshot target,
        List<string> pre, List<string> post, List<string> fkDrops, List<string> fkAdds,
        bool validateConstraints, HashSet<string> alteredColumns)
    {
        // Records compare list members by reference, so definitions are compared by a structural
        // signature. Otherwise an identical primary key would look changed and get a needless
        // DROP and ADD, which fails when a foreign key references it.

        var sourceForeignKeys = ByName(source.ForeignKeyDefinitions, f => f.Name);
        var targetForeignKeys = ByName(target.ForeignKeyDefinitions, f => f.Name);
        foreach (var (name, fk) in targetForeignKeys)
            if (!sourceForeignKeys.TryGetValue(name, out var s) || Sig(s) != Sig(fk))
                fkDrops.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {Id(name)};");
        foreach (var (name, fk) in sourceForeignKeys)
        {
            if (!targetForeignKeys.TryGetValue(name, out var t) || Sig(t) != Sig(fk))
            {
                fkAdds.Add(AddForeignKey(qualified, fk, AddClause(fk.IsDisabled, fk.IsNotTrusted, validateConstraints)));
                if (fk.IsDisabled) fkAdds.Add(ConstraintState(qualified, name, disabled: true, notTrusted: true));
            }
            else if (t.IsDisabled != fk.IsDisabled || t.IsNotTrusted != fk.IsNotTrusted)
            {
                // Same definition, only the state differs. Drop and recreate would be needless and risky.
                fkAdds.Add(ConstraintState(qualified, name, fk.IsDisabled, fk.IsNotTrusted));
            }
        }

        var sourceChecks = ByName(source.CheckDefinitions, c => c.Name);
        var targetChecks = ByName(target.CheckDefinitions, c => c.Name);
        foreach (var (name, check) in targetChecks)
            if (!sourceChecks.TryGetValue(name, out var s) || Sig(s) != Sig(check))
                pre.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {Id(name)};");
        var checkAdds = new List<string>();
        foreach (var (name, check) in sourceChecks)
        {
            if (!targetChecks.TryGetValue(name, out var t) || Sig(t) != Sig(check))
            {
                checkAdds.Add($"ALTER TABLE {qualified} {AddClause(check.IsDisabled, check.IsNotTrusted, validateConstraints)} " +
                              $"ADD CONSTRAINT {Id(check.Name)} CHECK " +
                              $"{(check.NotForReplication ? "NOT FOR REPLICATION " : string.Empty)}{check.Definition};");
                if (check.IsDisabled) checkAdds.Add(ConstraintState(qualified, name, disabled: true, notTrusted: true));
            }
            else if (t.IsDisabled != check.IsDisabled || t.IsNotTrusted != check.IsNotTrusted)
            {
                checkAdds.Add(ConstraintState(qualified, name, check.IsDisabled, check.IsNotTrusted));
            }
        }

        // Indexes, including the ones behind PRIMARY KEY and UNIQUE constraints.
        var sourceIndexes = ByName(source.IndexDefinitions, i => i.Name);
        var targetIndexes = ByName(target.IndexDefinitions, i => i.Name);
        foreach (var (name, index) in targetIndexes)
            if (!sourceIndexes.TryGetValue(name, out var s) || Sig(s) != Sig(index))
                pre.Add(DropIndex(qualified, index));
        var indexAdds = new List<string>();
        var recreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, index) in sourceIndexes)
            if (!targetIndexes.TryGetValue(name, out var t) || Sig(t) != Sig(index))
            {
                indexAdds.Add(CreateIndex(qualified, index));
                recreated.Add(name);
            }

        // An unchanged index on an altered column blocks the ALTER (Msg 5074 "index is dependent on
        // column"). It is dropped before the ALTER and created again with the same definition.
        if (alteredColumns.Count > 0)
            foreach (var (name, index) in sourceIndexes)
            {
                if (recreated.Contains(name)) continue;
                var touches = index.KeyColumns.Any(k => alteredColumns.Contains(k.Column))
                           || index.IncludedColumns.Any(alteredColumns.Contains);
                if (!touches) continue;
                pre.Add(DropIndex(qualified, index));
                indexAdds.Add(CreateIndex(qualified, index));
            }

        // Inside the table, keys and indexes come before checks. Foreign keys come at the very end.
        post.AddRange(indexAdds);
        post.AddRange(checkAdds);
    }

    /// <summary>
    /// User statistics. ALTER STATISTICS can only switch NORECOMPUTE, a change of columns or filter
    /// needs a drop and create. Every change takes that one path to keep it simple. Like indexes,
    /// drops run before the column changes (a statistic on a column blocks dropping it) and
    /// creates after them (a new column exists by then).
    /// </summary>
    private static void AppendStatisticsDiff(
        string qualified, ObjectSnapshot source, ObjectSnapshot target,
        List<string> pre, List<string> post)
    {
        var sourceStatistics = ByName(source.StatisticsDefinitions, s => s.Name);
        var targetStatistics = ByName(target.StatisticsDefinitions, s => s.Name);

        foreach (var (name, statistic) in targetStatistics)
            if (!sourceStatistics.TryGetValue(name, out var s) || Sig(s) != Sig(statistic))
                pre.Add(StatisticsScript.Drop(qualified, name));

        foreach (var (name, statistic) in sourceStatistics)
            if (!targetStatistics.TryGetValue(name, out var t) || Sig(t) != Sig(statistic))
                post.Add(StatisticsScript.Create(qualified, statistic));
    }

    /// <summary>
    /// DEFAULT changes on columns that exist on both sides: the old default is dropped and the new
    /// one added. A new column gets its default inline in ADD COLUMN, so it is not handled here.
    /// </summary>
    private static void AppendDefaultDiff(
        string qualified,
        Dictionary<string, ColumnInfo> sourceByName,
        Dictionary<string, ColumnInfo> targetByName,
        List<string> pre, List<string> post)
    {
        foreach (var (name, source) in sourceByName)
        {
            if (!targetByName.TryGetValue(name, out var target)) continue;
            if (string.Equals(source.DefaultDefinition, target.DefaultDefinition, StringComparison.Ordinal)) continue;

            // The old default is dropped by its real name, even a system generated one, because
            // the name comes from the target catalog.
            if (target.DefaultDefinition is not null && target.DefaultName is not null)
                pre.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {Id(target.DefaultName)};");

            // A user chosen name is kept. A system generated name is left out, so the target
            // generates its own instead of copying one that belongs to the source.
            if (source.DefaultDefinition is not null)
            {
                var named = source.DefaultName is not null && !source.DefaultIsSystemNamed;
                var namePart = named ? $"CONSTRAINT {Id(source.DefaultName!)} " : string.Empty;
                post.Add($"ALTER TABLE {qualified} ADD {namePart}DEFAULT {source.DefaultDefinition} FOR {Id(name)};");
            }
        }
    }

    /// <summary>
    /// Whether a new constraint checks the existing rows. A constraint that is disabled or not
    /// trusted in the source may well be violated by the data, and adding it WITH CHECK would make
    /// the deployment fail. So the source state wins over the ValidateNewConstraints option.
    /// </summary>
    private static string AddClause(bool disabled, bool notTrusted, bool validateConstraints) =>
        disabled || notTrusted || !validateConstraints ? "WITH NOCHECK" : "WITH CHECK";

    /// <summary>
    /// Brings a constraint to the state it has in the source. The three states are written differently:
    /// disabled is NOCHECK CONSTRAINT (which also makes it untrusted), enabled but untrusted is
    /// CHECK CONSTRAINT (enables without checking rows), and enabled and trusted is
    /// WITH CHECK CHECK CONSTRAINT (checks the existing rows).
    /// </summary>
    private static string ConstraintState(string qualified, string name, bool disabled, bool notTrusted) =>
        disabled ? $"ALTER TABLE {qualified} NOCHECK CONSTRAINT {Id(name)};"
        : notTrusted ? $"ALTER TABLE {qualified} CHECK CONSTRAINT {Id(name)};"
        : $"ALTER TABLE {qualified} WITH CHECK CHECK CONSTRAINT {Id(name)};";

    private static string DropIndex(string qualified, IndexDefinition index) => index.IsConstraint
        ? $"ALTER TABLE {qualified} DROP CONSTRAINT {Id(index.Name)};"
        : $"DROP INDEX {Id(index.Name)} ON {qualified};";

    private static string CreateIndex(string qualified, IndexDefinition index)
    {
        if (index.IsConstraint)
        {
            var kind = index.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
            var constraint = new StringBuilder(
                $"ALTER TABLE {qualified} ADD CONSTRAINT {Id(index.Name)} {kind} {Clustered(index.TypeDesc)} ({KeyList(index, ordered: true)})");
            constraint.Append(WithOptions(index));
            // Placement such as ON [scheme]([column]) for a partitioned clustered key. Without it
            // the key would be created on the default filegroup and not partitioned.
            if (!string.IsNullOrEmpty(index.Placement)) constraint.Append(" ON ").Append(index.Placement);
            return constraint.Append(';').ToString();
        }

        var columnstore = index.TypeDesc.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase);
        // A clustered columnstore index covers the whole table, so it has no column list, INCLUDE
        // or WHERE. A nonclustered columnstore index lists its columns without ASC or DESC.
        var clusteredColumnstore = columnstore && !index.TypeDesc.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder("CREATE ");
        if (index.IsUnique && !columnstore) sb.Append("UNIQUE ");
        sb.Append(index.TypeDesc).Append(" INDEX ").Append(Id(index.Name)).Append(" ON ").Append(qualified);

        if (!clusteredColumnstore)
        {
            sb.Append(" (").Append(KeyList(index, ordered: !columnstore)).Append(')');
            if (!columnstore && index.IncludedColumns.Count > 0)
                sb.Append(" INCLUDE (").Append(string.Join(", ", index.IncludedColumns.Select(Id))).Append(')');
            if (index.FilterDefinition is not null)
                sb.Append(" WHERE ").Append(index.FilterDefinition);
        }

        sb.Append(WithOptions(index));
        if (!string.IsNullOrEmpty(index.Placement)) sb.Append(" ON ").Append(index.Placement);
        return sb.Append(';').ToString();
    }

    /// <summary>
    /// The single WITH (...) list of an index. Only values that differ from the default are written:
    /// row and page locks default to ON, compression to NONE.
    /// </summary>
    private static string WithOptions(IndexDefinition index)
    {
        var columnstore = index.TypeDesc.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase);
        var options = new List<string>(4);
        if (index.ExplicitCompression is { } compression) options.Add($"DATA_COMPRESSION = {compression}");
        // Lock and statistics options are not valid on columnstore indexes.
        if (!columnstore)
        {
            if (!index.AllowRowLocks) options.Add("ALLOW_ROW_LOCKS = OFF");
            if (!index.AllowPageLocks) options.Add("ALLOW_PAGE_LOCKS = OFF");
            if (index.StatisticsNoRecompute) options.Add("STATISTICS_NORECOMPUTE = ON");
            // SQL Server 2019 and later. Only written when on, so older targets do not fail.
            if (index.OptimizeForSequentialKey) options.Add("OPTIMIZE_FOR_SEQUENTIAL_KEY = ON");
        }
        return options.Count == 0 ? string.Empty : $" WITH ({string.Join(", ", options)})";
    }

    private static string AddForeignKey(string qualified, ForeignKeyDefinition fk, string checkClause)
    {
        var parent = string.Join(", ", fk.Columns.Select(c => Id(c.Parent)));
        var referenced = string.Join(", ", fk.Columns.Select(c => Id(c.Referenced)));
        var sb = new StringBuilder(
            $"ALTER TABLE {qualified} {checkClause} ADD CONSTRAINT {Id(fk.Name)} FOREIGN KEY ({parent}) " +
            $"REFERENCES {Id(fk.ReferencedSchema)}.{Id(fk.ReferencedName)} ({referenced})");
        if (fk.DeleteAction != 0) sb.Append(" ON DELETE ").Append(ReferentialAction(fk.DeleteAction));
        if (fk.UpdateAction != 0) sb.Append(" ON UPDATE ").Append(ReferentialAction(fk.UpdateAction));
        if (fk.NotForReplication) sb.Append(" NOT FOR REPLICATION");
        return sb.Append(';').ToString();
    }

    private static string KeyList(IndexDefinition index, bool ordered) => string.Join(", ",
        index.KeyColumns.Select(k => ordered ? $"{Id(k.Column)} {(k.Descending ? "DESC" : "ASC")}" : Id(k.Column)));

    private static string Clustered(string typeDesc) =>
        typeDesc.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase)
        && !typeDesc.StartsWith("NON", StringComparison.OrdinalIgnoreCase)
            ? "CLUSTERED" : "NONCLUSTERED";

    private static string ReferentialAction(byte action) => action switch
    {
        1 => "CASCADE",
        2 => "SET NULL",
        3 => "SET DEFAULT",
        _ => "NO ACTION",
    };

    private static Dictionary<string, T> ByName<T>(IReadOnlyList<T>? items, Func<T, string> name) =>
        (items ?? []).ToDictionary(name, StringComparer.OrdinalIgnoreCase);

    // Structural signatures, so list members are compared by value.

    private static string Sig(IndexDefinition i) =>
        $"pk={i.IsPrimaryKey}|uq={i.IsUniqueConstraint}|u={i.IsUnique}|t={i.TypeDesc}|" +
        $"keys={string.Join(",", i.KeyColumns.Select(k => $"{k.Column}:{(k.Descending ? "D" : "A")}"))}|" +
        $"inc={string.Join(",", i.IncludedColumns)}|f={i.FilterDefinition ?? ""}|" +
        $"comp={i.ExplicitCompression ?? ""}|rowLocks={i.AllowRowLocks}|pageLocks={i.AllowPageLocks}|" +
        $"seqKey={i.OptimizeForSequentialKey}|statsNoRecompute={i.StatisticsNoRecompute}|place={i.Placement ?? ""}";

    private static string Sig(StatisticsDefinition s) =>
        $"cols={string.Join(",", s.Columns)}|f={s.FilterDefinition ?? ""}|" +
        $"norecompute={s.NoRecompute}|incremental={s.IsIncremental}";

    private static string Sig(CheckDefinition c) => $"def={c.Definition}|nfr={c.NotForReplication}";

    private static string Sig(ForeignKeyDefinition f) =>
        $"ref={f.ReferencedSchema}.{f.ReferencedName}|" +
        $"cols={string.Join(",", f.Columns.Select(c => $"{c.Parent}>{c.Referenced}"))}|" +
        $"del={f.DeleteAction}|upd={f.UpdateAction}|nfr={f.NotForReplication}";

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

    /// <summary>Name part for the first argument of sp_rename. It sits inside brackets and inside
    /// an N'...' literal, so both closing brackets and quotes are escaped.</summary>
    private static string RenameTarget(string name) => Literal(Id(name));
}
