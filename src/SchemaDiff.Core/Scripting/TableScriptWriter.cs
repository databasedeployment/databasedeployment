using System.Globalization;
using System.Text;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>Catalog lookups needed to write a table script.</summary>
internal sealed class TableScriptSources
{
    public required Dictionary<int, List<ColumnRow>> ColumnsBy { get; init; }
    public required Dictionary<int, List<IndexRow>> IndexesBy { get; init; }
    public required Dictionary<long, List<IndexColumnRow>> IndexColumnsBy { get; init; }
    public required Dictionary<long, KeyConstraintRow> KeyConstraintByIndex { get; init; }
    public required Dictionary<int, List<CheckConstraintRow>> ChecksBy { get; init; }
    public required Dictionary<int, List<ForeignKeyRow>> ForeignKeysBy { get; init; }
    public required Dictionary<int, List<ForeignKeyColumnRow>> FkColumnsBy { get; init; }
    public required Dictionary<long, string> ColumnNames { get; init; }
    public Dictionary<int, string> XmlCollections { get; init; } = [];
    public required Dictionary<int, ObjectKey> KeyById { get; init; }
    /// <summary>object_id to "[filegroup]" or "[scheme]([column])" for the CREATE TABLE "ON [...]" clause.</summary>
    public Dictionary<int, string?> TablePlacement { get; init; } = [];
    /// <summary>Pair(object_id, index_id) to the placement of an index for the CREATE INDEX "ON [...]" clause.</summary>
    public Dictionary<long, string?> IndexPlacement { get; init; } = [];
    public Dictionary<int, TemporalRow> TemporalBy { get; init; } = [];
    public Dictionary<long, IndexExtraRow> IndexExtrasBy { get; init; } = [];
}

/// <summary>
/// Writes a readable CREATE script for a table.
///
/// SQL Server does not keep the original DDL text of a table. There is no such "schema file".
/// "Script Table as CREATE" in SSMS also builds the text from the catalog, and this class does
/// the same. Its purpose is not deployment but showing a change in context in the UI, before
/// and after, with real line numbers.
///
/// Both sides go through the same writer, so formatting never shows up in the diff. Things that
/// are ignored by the comparison (system generated names, collation) are not written here either,
/// otherwise the screen would show lines as different that the result list does not.
/// </summary>
internal static class TableScriptWriter
{
    public static string Write(ObjectKey key, int objectId, TableScriptSources sources, SnapshotOptions options)
    {
        var columns = sources.ColumnsBy.GetValueOrDefault(objectId) ?? [];
        var sb = new StringBuilder(columns.Count * 64 + 256);

        sb.Append("CREATE TABLE ").Append(Quote(key.Schema, key.Name)).AppendLine(" (");

        var body = new List<string>(columns.Count + 8);
        var nameWidth = columns.Count == 0 ? 0 : columns.Max(c => c.Name.Length + 2);
        var typeWidth = columns.Count == 0 ? 0 : columns.Max(c => FormatType(c, sources).Length);

        foreach (var column in columns.OrderBy(c => c.ColumnId))
            body.Add(WriteColumn(column, nameWidth, typeWidth, sources, options));

        body.AddRange(WriteTableConstraints(objectId, sources, options));

        // For a system-versioned temporal table the PERIOD line goes at the end of the body and
        // SYSTEM_VERSIONING after the closing parenthesis. Generated always columns are only valid this way.
        var temporal = sources.TemporalBy.GetValueOrDefault(objectId);
        if (temporal is { StartColumn: { } start, EndColumn: { } end })
            body.Add($"    PERIOD FOR SYSTEM_TIME ([{start}], [{end}])");

        for (var i = 0; i < body.Count; i++)
            sb.Append(body[i]).AppendLine(i < body.Count - 1 ? "," : string.Empty);

        // Storage placement: ") ON [filegroup]" or ") ON [scheme]([column])". In T-SQL the ON clause
        // comes before the temporal WITH (SYSTEM_VERSIONING ...) suffix.
        var placement = sources.TablePlacement.GetValueOrDefault(objectId);
        var placementClause = string.IsNullOrEmpty(placement) ? string.Empty : $" ON {placement}";
        sb.Append(')').Append(placementClause).Append(SystemVersioningSuffix(temporal)).AppendLine(";");

        foreach (var line in WriteIndexes(key, objectId, sources, options))
        {
            sb.AppendLine();
            sb.Append(line);
        }

        foreach (var line in WriteConstraintState(key, objectId, sources))
        {
            sb.AppendLine();
            sb.Append(line);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// <c>) WITH (SYSTEM_VERSIONING = ON [(HISTORY_TABLE = [s].[n])])</c> for temporal tables.
    /// A generated history table name differs between environments, so HISTORY_TABLE is left out
    /// in that case and SQL Server creates the history table itself.
    /// </summary>
    private static string SystemVersioningSuffix(TemporalRow? temporal)
    {
        if (temporal is null || temporal.StartColumn is null) return string.Empty;
        var auto = temporal.HistoryName is null
            || temporal.HistoryName.StartsWith("MSSQL_TemporalHistoryFor_", StringComparison.OrdinalIgnoreCase);
        var history = auto
            ? string.Empty
            : $" (HISTORY_TABLE = [{temporal.HistorySchema}].[{temporal.HistoryName}])";
        return $" WITH (SYSTEM_VERSIONING = ON{history})";
    }

    // Columns

    private static string WriteColumn(
        ColumnRow column, int nameWidth, int typeWidth, TableScriptSources sources, SnapshotOptions options)
    {
        var name = $"[{column.Name}]".PadRight(nameWidth);

        if (column.IsComputed)
        {
            var persisted = column.ComputedIsPersisted == true ? " PERSISTED" : string.Empty;
            return $"    {name} AS {column.ComputedDefinition}{persisted}";
        }

        var sb = new StringBuilder("    ");
        sb.Append(name).Append(' ').Append(FormatType(column, sources).PadRight(typeWidth));

        // Same order as the T-SQL grammar: FILESTREAM, COLLATE, SPARSE, COLUMN_SET, ROWGUIDCOL.
        if (column.IsFileStream) sb.Append(" FILESTREAM");

        if (column.Collation is not null && !options.IgnoreCollation)
            sb.Append(" COLLATE ").Append(column.Collation);

        if (column.IsSparse) sb.Append(" SPARSE");
        if (column.IsColumnSet) sb.Append(" COLUMN_SET FOR ALL_SPARSE_COLUMNS");
        if (column.IsRowGuidCol) sb.Append(" ROWGUIDCOL");

        if (column.IsIdentity)
        {
            sb.Append(" IDENTITY");
            if (!options.IgnoreIdentitySeed)
                sb.Append(" (").Append(column.IdentitySeed).Append(", ").Append(column.IdentityIncrement).Append(')');
            if (column.IdentityNotForReplication) sb.Append(" NOT FOR REPLICATION");
        }

        // Temporal PERIOD column: GENERATED ALWAYS AS ROW START/END [HIDDEN]. It is only valid
        // together with PERIOD FOR SYSTEM_TIME in the CREATE TABLE, which Write adds for temporal tables.
        if (column.GeneratedAlwaysType == 1) sb.Append(" GENERATED ALWAYS AS ROW START");
        else if (column.GeneratedAlwaysType == 2) sb.Append(" GENERATED ALWAYS AS ROW END");
        if (column.GeneratedAlwaysType != 0 && column.IsHidden) sb.Append(" HIDDEN");

        sb.Append(column.IsNullable ? " NULL" : " NOT NULL");

        if (column.DefaultDefinition is not null)
        {
            var named = !(options.IgnoreSystemNamedConstraints && column.DefaultIsSystemNamed == true);
            if (named && column.DefaultName is not null) sb.Append(" CONSTRAINT [").Append(column.DefaultName).Append(']');
            sb.Append(" DEFAULT ").Append(column.DefaultDefinition);
        }

        return sb.ToString();
    }

    private static string FormatType(ColumnRow column, TableScriptSources sources)
    {
        // User-defined types are written with their schema.
        if (!string.Equals(column.TypeSchema, "sys", StringComparison.OrdinalIgnoreCase))
            return Quote(column.TypeSchema, column.TypeName);

        var upper = column.TypeName.ToUpperInvariant();
        var length = column.MaxLength.ToString(CultureInfo.InvariantCulture);

        // Typed XML: xml(CONTENT|DOCUMENT [schema].[collection]). Writing plain XML when the collection
        // name cannot be resolved would create the wrong column, so the suffix needs a known name.
        if (column.XmlCollectionId != 0
            && sources.XmlCollections.TryGetValue(column.XmlCollectionId, out var collection))
        {
            return $"{upper} ({(column.IsXmlDocument ? "DOCUMENT" : "CONTENT")} {collection})";
        }

        return column.TypeName.ToLowerInvariant() switch
        {
            "varchar" or "char" or "varbinary" or "binary" =>
                column.MaxLength == -1 ? $"{upper} (MAX)" : $"{upper} ({length})",
            "nvarchar" or "nchar" =>
                column.MaxLength == -1 ? $"{upper} (MAX)" : $"{upper} ({column.MaxLength / 2})",
            "decimal" or "numeric" => $"{upper} ({column.Precision}, {column.Scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{upper} ({column.Scale})",
            "float" => column.Precision == 53 ? upper : $"{upper} ({column.Precision})",
            _ => upper,
        };
    }

    // Constraints inside the table

    private static IEnumerable<string> WriteTableConstraints(
        int objectId, TableScriptSources sources, SnapshotOptions options)
    {
        var lines = new List<string>();

        // PK and UNIQUE constraints are written from the index that carries them.
        foreach (var index in (sources.IndexesBy.GetValueOrDefault(objectId) ?? [])
                     .Where(i => i.IsPrimaryKey || i.IsUniqueConstraint)
                     .OrderByDescending(i => i.IsPrimaryKey))
        {
            var pairKey = Pair(objectId, index.IndexId);
            sources.KeyConstraintByIndex.TryGetValue(pairKey, out var constraint);

            var prefix = NameConstraint(constraint?.Name, constraint?.IsSystemNamed ?? true, options);
            var kind = index.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
            var clustered = index.TypeDesc.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase)
                            && !index.TypeDesc.StartsWith("NON", StringComparison.OrdinalIgnoreCase)
                ? "CLUSTERED" : "NONCLUSTERED";

            lines.Add($"    {prefix}{kind} {clustered} ({KeyColumns(objectId, index.IndexId, sources)})");
        }

        foreach (var check in (sources.ChecksBy.GetValueOrDefault(objectId) ?? []).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var prefix = NameConstraint(check.Name, check.IsSystemNamed, options);
            // Grammar: CHECK [NOT FOR REPLICATION] (expression).
            var nfr = check.IsNotForReplication ? "NOT FOR REPLICATION " : string.Empty;
            lines.Add($"    {prefix}CHECK {nfr}{check.Definition}");
        }

        foreach (var foreignKey in (sources.ForeignKeysBy.GetValueOrDefault(objectId) ?? []).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var prefix = NameConstraint(foreignKey.Name, foreignKey.IsSystemNamed, options);
            var columns = sources.FkColumnsBy.GetValueOrDefault(foreignKey.ObjectId) ?? [];

            var parentColumns = string.Join(", ", columns.OrderBy(c => c.ConstraintColumnId)
                .Select(c => $"[{ColumnName(sources, c.ParentObjectId, c.ParentColumnId)}]"));
            var referencedColumns = string.Join(", ", columns.OrderBy(c => c.ConstraintColumnId)
                .Select(c => $"[{ColumnName(sources, c.ReferencedObjectId, c.ReferencedColumnId)}]"));

            var referenced = sources.KeyById.TryGetValue(foreignKey.ReferencedObjectId, out var refKey)
                ? Quote(refKey.Schema, refKey.Name)
                : $"[#{foreignKey.ReferencedObjectId}]";

            var actions = new StringBuilder();
            if (foreignKey.DeleteAction != 0) actions.Append(" ON DELETE ").Append(ReferentialAction(foreignKey.DeleteAction));
            if (foreignKey.UpdateAction != 0) actions.Append(" ON UPDATE ").Append(ReferentialAction(foreignKey.UpdateAction));
            if (foreignKey.IsNotForReplication) actions.Append(" NOT FOR REPLICATION");

            lines.Add($"    {prefix}FOREIGN KEY ({parentColumns}) REFERENCES {referenced} ({referencedColumns}){actions}");
        }

        return lines;
    }

    private static string NameConstraint(string? name, bool isSystemNamed, SnapshotOptions options) =>
        name is null || (options.IgnoreSystemNamedConstraints && isSystemNamed)
            ? string.Empty
            : $"CONSTRAINT [{name}] ";

    private static string ReferentialAction(byte action) => action switch
    {
        1 => "CASCADE",
        2 => "SET NULL",
        3 => "SET DEFAULT",
        _ => "NO ACTION",
    };

    // Standalone indexes

    private static IEnumerable<string> WriteIndexes(
        ObjectKey key, int objectId, TableScriptSources sources, SnapshotOptions options)
    {
        var lines = new List<string>();

        foreach (var index in (sources.IndexesBy.GetValueOrDefault(objectId) ?? [])
                     .Where(i => !i.IsPrimaryKey && !i.IsUniqueConstraint && i.Name is not null)
                     .OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var columnstore = index.TypeDesc.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase);
            // A CLUSTERED COLUMNSTORE index covers the whole table, so there is no column list, INCLUDE or WHERE.
            var clusteredColumnstore = columnstore && !index.TypeDesc.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.Append("GO").AppendLine();
            sb.Append("CREATE ");
            if (index.IsUnique && !columnstore) sb.Append("UNIQUE ");
            sb.Append(index.TypeDesc).Append(" INDEX [").Append(index.Name).AppendLine("]");
            sb.Append("    ON ").Append(Quote(key.Schema, key.Name));
            if (!clusteredColumnstore)
                sb.Append('(').Append(KeyColumns(objectId, index.IndexId, sources)).Append(')');

            if (!columnstore)   // columnstore indexes have no INCLUDE
            {
                var included = IncludedColumns(objectId, index.IndexId, sources);
                if (included.Length > 0) sb.AppendLine().Append("    INCLUDE(").Append(included).Append(')');
            }

            if (!clusteredColumnstore && index.FilterDefinition is not null)
                sb.AppendLine().Append("    WHERE ").Append(index.FilterDefinition);

            // Physical options go into one WITH list and only values that differ from the default are written.
            // Lock and fill factor options are not valid for columnstore, so they are skipped there.
            if (!options.IgnoreIndexPhysicalOptions && !columnstore)
            {
                var withOptions = new List<string>(5);
                if (index.FillFactor > 0)
                    withOptions.Add($"FILLFACTOR = {index.FillFactor.ToString(CultureInfo.InvariantCulture)}");
                if (!index.AllowRowLocks) withOptions.Add("ALLOW_ROW_LOCKS = OFF");
                if (!index.AllowPageLocks) withOptions.Add("ALLOW_PAGE_LOCKS = OFF");

                var extra = sources.IndexExtrasBy.GetValueOrDefault(Pair(objectId, index.IndexId));
                if (extra?.StatisticsNoRecompute == true) withOptions.Add("STATISTICS_NORECOMPUTE = ON");
                if (extra?.OptimizeForSequentialKey == true) withOptions.Add("OPTIMIZE_FOR_SEQUENTIAL_KEY = ON");
                if (withOptions.Count > 0)
                    sb.AppendLine().Append("    WITH (").Append(string.Join(", ", withOptions)).Append(')');
            }

            // Storage placement: " ON [filegroup]" or " ON [scheme]([column])".
            var place = sources.IndexPlacement.GetValueOrDefault(Pair(objectId, index.IndexId));
            if (!string.IsNullOrEmpty(place)) sb.Append(" ON ").Append(place);

            sb.Append(';');
            lines.Add(sb.ToString());
        }

        return lines;
    }

    // Disabled constraints

    /// <summary>
    /// Constraints inside CREATE TABLE are always created enabled. Disabled ones can only be turned
    /// off after the table exists. Without this step the constraint would quietly become active on
    /// the target and a rule that was switched off on purpose in the source would start to apply.
    /// </summary>
    private static IEnumerable<string> WriteConstraintState(
        ObjectKey key, int objectId, TableScriptSources sources)
    {
        var qualified = Quote(key.Schema, key.Name);
        var names = new List<string>();

        names.AddRange((sources.ChecksBy.GetValueOrDefault(objectId) ?? [])
            .Where(c => c.IsDisabled).Select(c => c.Name));
        names.AddRange((sources.ForeignKeysBy.GetValueOrDefault(objectId) ?? [])
            .Where(f => f.IsDisabled).Select(f => f.Name));

        names.Sort(StringComparer.Ordinal);
        return names.Select(n =>
            $"GO{Environment.NewLine}ALTER TABLE {qualified} NOCHECK CONSTRAINT [{n}];");
    }

    private static string KeyColumns(int objectId, int indexId, TableScriptSources sources)
    {
        var columns = sources.IndexColumnsBy.GetValueOrDefault(Pair(objectId, indexId)) ?? [];

        var keys = columns.Where(c => !c.IsIncluded && c.KeyOrdinal > 0).OrderBy(c => c.KeyOrdinal)
            .Select(c => $"[{ColumnName(sources, objectId, c.ColumnId)}] {(c.IsDescending ? "DESC" : "ASC")}");

        // In columnstore indexes the columns have key_ordinal 0 and are not included.
        var unordered = columns.Where(c => !c.IsIncluded && c.KeyOrdinal == 0)
            .Select(c => $"[{ColumnName(sources, objectId, c.ColumnId)}]")
            .Order(StringComparer.Ordinal);

        return string.Join(", ", keys.Concat(unordered));
    }

    private static string IncludedColumns(int objectId, int indexId, TableScriptSources sources) =>
        string.Join(", ", (sources.IndexColumnsBy.GetValueOrDefault(Pair(objectId, indexId)) ?? [])
            .Where(c => c.IsIncluded)
            .Select(c => $"[{ColumnName(sources, objectId, c.ColumnId)}]")
            .Order(StringComparer.Ordinal));

    // Helpers

    private static string ColumnName(TableScriptSources sources, int objectId, int columnId) =>
        sources.ColumnNames.GetValueOrDefault(Pair(objectId, columnId), $"#{columnId}");

    private static string Quote(string schema, string name) => $"[{schema}].[{name}]";

    private static long Pair(int high, int low) => ((long)high << 32) | (uint)low;
}
