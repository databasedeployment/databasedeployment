using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SchemaDiff.Core.Hashing;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Normalization;

namespace SchemaDiff.Core.Extraction;

public sealed record SnapshotOptions
{
    public NormalizationOptions Normalization { get; init; } = NormalizationOptions.Default;

    /// <summary>
    /// System generated constraint names (PK__Tbl__A1B2C3, DF__Tbl__Col__X) differ between
    /// environments. By default they are left out of the comparison.
    /// </summary>
    public bool IgnoreSystemNamedConstraints { get; init; } = true;

    /// <summary>Ignore every physical index setting (fill factor, padding, ignore_dup_key).</summary>
    public bool IgnoreIndexPhysicalOptions { get; init; }

    /// <summary>Ignore only the index fill factor (SSDT: "Ignore fill factor").</summary>
    public bool IgnoreFillFactor { get; init; }

    /// <summary>Ignore only index padding (SSDT: "Ignore index padding").</summary>
    public bool IgnoreIndexPadding { get; init; }

    /// <summary>Ignore DATA_COMPRESSION differences on indexes and tables (SSDT: "Ignore data compression options").</summary>
    public bool IgnoreDataCompression { get; init; }

    /// <summary>Ignore SET ANSI_NULLS differences (SSDT: "Ignore ANSI NULLS").</summary>
    public bool IgnoreAnsiNulls { get; init; }

    /// <summary>Ignore SET QUOTED_IDENTIFIER differences (SSDT: "Ignore quoted identifiers").</summary>
    public bool IgnoreQuotedIdentifiers { get; init; }

    /// <summary>Ignore the IDENTITY increment (SSDT: "Ignore increment").</summary>
    public bool IgnoreIdentityIncrement { get; init; }

    /// <summary>Ignore the enabled or disabled state of DML triggers (SSDT: "Ignore DML trigger state").</summary>
    public bool IgnoreDmlTriggerState { get; init; }

    /// <summary>Compare columns by name instead of by definition order.</summary>
    public bool IgnoreColumnOrder { get; init; }

    /// <summary>
    /// Ignore column collation differences. Where servers have different defaults
    /// (for example Turkish_CI_AS and SQL_Latin1_General_CP1_CI_AS) every text column would
    /// show up as a difference. This option suppresses that noise.
    /// </summary>
    public bool IgnoreCollation { get; init; }

    /// <summary>Ignore the IDENTITY seed. The increment has its own option, and whether
    /// a column is an identity column is always compared.</summary>
    public bool IgnoreIdentitySeed { get; init; }

    public bool CaseSensitiveNames { get; init; }

    /// <summary>
    /// Treat column names as case-sensitive. Off by default, so a column that only differs in
    /// casing ([Id] and [id]) is not a difference. When on, the difference is reported and fixed
    /// with <c>sp_rename</c> during deployment. Object names have their own <see cref="CaseSensitiveNames"/>.
    /// </summary>
    public bool CaseSensitiveColumnNames { get; init; }

    /// <summary>
    /// Also produce and keep the full text of objects (original body for modules, a CREATE
    /// script for tables). Not needed for comparison, only for a readable diff in the UI.
    /// Memory cost grows with the schema, so it is off by default.
    /// </summary>
    public bool KeepDisplayScripts { get; init; }

    public static readonly SnapshotOptions Default = new();
}

/// <summary>
/// Turns raw catalog rows into canonical text and hashes.
/// The canonical text has to be deterministic: the same schema must give byte for byte the
/// same output, otherwise the hash comparison reports false differences.
/// </summary>
internal static class SnapshotBuilder
{
    public static DatabaseSnapshot Build(
        CatalogSet catalog, ExtractionReport report, SnapshotOptions options, CompareProgress? progress = null)
    {
        var sw = Stopwatch.StartNew();

        var keyById = new Dictionary<int, ObjectKey>(catalog.Objects.Count);
        foreach (var o in catalog.Objects)
            keyById[o.ObjectId] = new ObjectKey(o.SchemaName, o.Name, ObjectKindMap.FromSysType(o.Type));

        // (objectId, columnId) to the raw column name. The structural definitions used for
        // scripting read it so columns keep their real casing in generated SQL.
        var columnNames = new Dictionary<long, string>(catalog.Columns.Count);
        foreach (var c in catalog.Columns) columnNames[Pair(c.ObjectId, c.ColumnId)] = c.Name;

        // Column names for the canonical (hashed) comparison. In case-insensitive mode they are
        // lowercased so column references in indexes, keys and FKs stay consistent with BuildColumns.
        // Otherwise a table where only the casing of a column changed ([CustomerShareholderNk] and
        // [CustomerShareHolderNk]) would look changed because of its index canonical text.
        var canonicalColumnNames = options.CaseSensitiveColumnNames
            ? columnNames
            : columnNames.ToDictionary(kv => kv.Key, kv => kv.Value.ToLowerInvariant());

        // Storage placement. Every table and index sits on "ON [filegroup]" or "ON [scheme]([column])".
        // data_space covers both filegroups and partition schemes (type 'PS').
        var dataSpaces = new Dictionary<int, (string Name, bool IsScheme)>(catalog.DataSpaces.Count);
        foreach (var d in catalog.DataSpaces) dataSpaces[d.Id] = (d.Name, d.Type == "PS");
        var partitionCols = new Dictionary<long, string>(catalog.PartitionColumns.Count);
        foreach (var p in catalog.PartitionColumns) partitionCols[Pair(p.ObjectId, p.IndexId)] = p.Column;

        string? ResolvePlacement(int dataSpaceId, int objectId, int indexId)
        {
            if (dataSpaceId <= 0 || !dataSpaces.TryGetValue(dataSpaceId, out var ds)) return null;
            if (!ds.IsScheme) return $"[{ds.Name}]";
            var col = partitionCols.GetValueOrDefault(Pair(objectId, indexId));
            return col is null ? $"[{ds.Name}]" : $"[{ds.Name}]([{col}])";
        }

        var indexPlacement = new Dictionary<long, string?>(catalog.Indexes.Count);
        foreach (var i in catalog.Indexes)
            indexPlacement[Pair(i.ObjectId, i.IndexId)] = ResolvePlacement(i.DataSpaceId, i.ObjectId, i.IndexId);
        var tablePlacement = new Dictionary<int, string?>(catalog.TablePlacements.Count);
        foreach (var tp in catalog.TablePlacements)
            tablePlacement[tp.ObjectId] = ResolvePlacement(tp.DataSpaceId, tp.ObjectId, tp.IndexId);

        // Typed XML columns store an id, not a name. Ids differ between environments, so the
        // comparison and the script use the collection name.
        var xmlCollections = new Dictionary<int, string>(catalog.XmlSchemaCollections.Count);
        foreach (var x in catalog.XmlSchemaCollections)
            xmlCollections[x.CollectionId] = $"[{x.SchemaName}].[{x.Name}]";

        var columnsBy = GroupBy(catalog.Columns, c => c.ObjectId);
        var indexesBy = GroupBy(catalog.Indexes, i => i.ObjectId);
        // Depending on the version, sys.index_columns sometimes reports NONCLUSTERED COLUMNSTORE
        // columns with is_included_column = 1. Treated as included, the column list would be empty,
        // the same index would look different on two servers and the script would fail with "ON [t]()".
        // Columnstore has no INCLUDE concept, every column is a columnstore column.
        var nonclusteredColumnstore = catalog.Indexes
            .Where(i => i.TypeDesc.Equals("NONCLUSTERED COLUMNSTORE", StringComparison.OrdinalIgnoreCase))
            .Select(i => Pair(i.ObjectId, i.IndexId))
            .ToHashSet();
        var indexColumnsBy = GroupBy(
            catalog.IndexColumns
                .Select(ic => ic.IsIncluded && nonclusteredColumnstore.Contains(Pair(ic.ObjectId, ic.IndexId))
                    ? ic with { IsIncluded = false, KeyOrdinal = 0 }
                    : ic)
                .ToList(),
            ic => Pair(ic.ObjectId, ic.IndexId));

        // If the extra index options could not be read (server older than 2019) they are unknown,
        // not off. Treating them as empty would create false differences between mixed versions.
        var indexExtrasUnreadable = report.FailedQueries.Contains("indexExtras");
        if (indexExtrasUnreadable && catalog.Indexes.Count > 0)
        {
            report.Warnings.Add(
                "Extra index options (OPTIMIZE_FOR_SEQUENTIAL_KEY / STATISTICS_NORECOMPUTE) " +
                "could not be read and were left out of the comparison (server older than 2019 or missing permission).");
        }

        var indexExtrasBy = indexExtrasUnreadable
            ? []
            : catalog.IndexExtras.ToDictionary(e => Pair(e.ObjectId, e.IndexId));

        var checksBy = GroupBy(catalog.CheckConstraints, c => c.ParentObjectId);
        var foreignKeysBy = GroupBy(catalog.ForeignKeys, f => f.ParentObjectId);
        var fkColumnsBy = GroupBy(catalog.ForeignKeyColumns, f => f.ConstraintObjectId);

        var keyConstraintByIndex = new Dictionary<long, KeyConstraintRow>();
        foreach (var kc in catalog.KeyConstraints)
            if (kc.UniqueIndexId is { } indexId)
                keyConstraintByIndex[Pair(kc.ParentObjectId, indexId)] = kc;

        var modulesById = catalog.Modules.ToDictionary(m => m.ObjectId);
        var triggersById = catalog.Triggers.ToDictionary(t => t.ObjectId);
        var rowCountsById = catalog.RowCounts.ToDictionary(r => r.ObjectId, r => r.Rows);
        var temporalById = catalog.Temporal.ToDictionary(t => t.ObjectId);

        // The most expensive step is tokenizing T-SQL. Objects are independent and run in parallel.
        // Progress is reported from here because locally this loop dominates the compare time.
        // Every module counts as one unit, even with a NULL definition, so the total adds up.
        // In case-insensitive mode (caseSensitiveNames off) identifier casing is folded, so modules
        // that only differ in the casing of a name ([GetCustomer] and [getcustomer]) are equal.
        // Table column names take a separate path (BuildColumns) and keep their casing, so column
        // rename detection (sp_rename) is not affected.
        var norm = options.Normalization with { FoldIdentifierCase = !options.CaseSensitiveNames };

        var normalizedBodies = new ConcurrentDictionary<int, string>();
        Parallel.ForEach(catalog.Modules, module =>
        {
            if (module.Definition is not null)
                normalizedBodies[module.ObjectId] =
                    TSqlNormalizer.Normalize(module.Definition, module.UsesQuotedIdentifier, norm);
            progress?.BuildItemCompleted();
        });

        // Matching is always case-insensitive. [GetCustomer] and [getcustomer] are two spellings of
        // the same object, not two objects. The case-sensitive names option only decides whether
        // such a casing difference is reported (see SchemaComparer). When on it shows up as a single
        // change, when off it is not listed at all.
        var comparer = ObjectKeyComparer.CaseInsensitive;
        var objects = new Dictionary<ObjectKey, ObjectSnapshot>(catalog.Objects.Count + catalog.Schemas.Count, comparer);

        foreach (var schema in catalog.Schemas)
        {
            var key = new ObjectKey(schema.Name, schema.Name, ObjectKind.Schema);
            var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero };
            // Fold the canonical name so a casing difference does not count in case-insensitive mode.
            var schemaCanonicalName = options.CaseSensitiveNames ? schema.Name : schema.Name.ToLowerInvariant();
            SetPart(snapshot, "definition", $"schema|{schemaCanonicalName}");
            // Show a readable script in the detail panel instead of the raw "schema|RPT" text.
            if (options.KeepDisplayScripts) snapshot.DisplayScript = $"CREATE SCHEMA [{schema.Name}];";
            Finalize(snapshot);
            objects[key] = snapshot;
        }

        foreach (var obj in catalog.Objects)
        {
            var key = keyById[obj.ObjectId];
            if (key.Kind == ObjectKind.Unknown) continue;

            var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero, ModifyDate = obj.ModifyDate };

            switch (key.Kind)
            {
                case ObjectKind.Table:
                    snapshot.Columns = ToColumnInfo(columnsBy.GetValueOrDefault(obj.ObjectId), xmlCollections);
                    snapshot.RowCount = rowCountsById.TryGetValue(obj.ObjectId, out var rows) ? rows : null;
                    if (options.KeepDisplayScripts)
                    {
                        snapshot.IndexDefinitions = BuildIndexDefinitions(
                            obj.ObjectId, indexesBy, indexColumnsBy, keyConstraintByIndex, columnNames,
                            indexExtrasBy, indexPlacement, options);
                        snapshot.CheckDefinitions = BuildCheckDefinitions(checksBy.GetValueOrDefault(obj.ObjectId), options);
                        snapshot.ForeignKeyDefinitions = BuildForeignKeyDefinitions(
                            foreignKeysBy.GetValueOrDefault(obj.ObjectId), fkColumnsBy, columnNames, keyById, options);
                    }
                    SetPart(snapshot, "columns", BuildColumns(columnsBy.GetValueOrDefault(obj.ObjectId), xmlCollections, options));
                    SetPartFoldNames(snapshot, "indexes", BuildIndexes(obj.ObjectId, indexesBy, indexColumnsBy, keyConstraintByIndex, canonicalColumnNames, indexExtrasBy, indexPlacement, options), options);
                    SetPartFoldNames(snapshot, "checks", BuildChecks(checksBy.GetValueOrDefault(obj.ObjectId), options), options);
                    SetPartFoldNames(snapshot, "foreignKeys", BuildForeignKeys(foreignKeysBy.GetValueOrDefault(obj.ObjectId), fkColumnsBy, canonicalColumnNames, keyById, options), options);
                    if (temporalById.TryGetValue(obj.ObjectId, out var temporal))
                        SetPart(snapshot, "temporal", BuildTemporal(temporal));
                    // Storage placement (filegroup or partition scheme) goes into the script and
                    // its difference is detected in the canonical text. Names are folded so a casing
                    // only difference does not count. Nothing is written when there is no placement.
                    snapshot.Placement = tablePlacement.GetValueOrDefault(obj.ObjectId);
                    if (!string.IsNullOrEmpty(snapshot.Placement))
                        SetPart(snapshot, "placement", FoldName(snapshot.Placement, options));
                    break;

                case ObjectKind.View:
                    ApplyModule(snapshot, obj.ObjectId, modulesById, normalizedBodies, options);
                    // View columns are derived. Their type, length, precision and scale come from the
                    // base objects behind the SELECT, not from the view definition. When the body is the
                    // same, a difference there reflects a change in the base object and there is nothing
                    // to deploy for the view itself (the CREATE VIEW text is identical and ALTER cannot fix
                    // it). Views are therefore compared by body and derived column metadata stays out of
                    // the hash, which is also what SSDT does. The real difference is caught when the base
                    // table or view is compared.
                    SetPartFoldNames(snapshot, "indexes", BuildIndexes(obj.ObjectId, indexesBy, indexColumnsBy, keyConstraintByIndex, canonicalColumnNames, indexExtrasBy, indexPlacement, options), options);
                    break;

                case ObjectKind.Trigger:
                    ApplyModule(snapshot, obj.ObjectId, modulesById, normalizedBodies, options);
                    if (triggersById.TryGetValue(obj.ObjectId, out var trigger))
                    {
                        snapshot.IsDisabled = trigger.IsDisabled;
                        if (keyById.TryGetValue(obj.ParentObjectId, out var parentKey)) snapshot.Parent = parentKey;
                        var parent = keyById.TryGetValue(obj.ParentObjectId, out var p) ? p.ToString() : $"#{obj.ParentObjectId}";
                        // With "Ignore DML trigger state" the enabled or disabled state is not compared.
                        var disabled = options.IgnoreDmlTriggerState ? string.Empty : $"|disabled={Flag(trigger.IsDisabled)}";
                        SetPart(snapshot, "attributes",
                            $"parent={parent}{disabled}|insteadOf={Flag(trigger.IsInsteadOf)}");
                    }
                    break;

                case ObjectKind.Procedure:
                case ObjectKind.ScalarFunction:
                case ObjectKind.InlineTableFunction:
                case ObjectKind.TableFunction:
                    ApplyModule(snapshot, obj.ObjectId, modulesById, normalizedBodies, options);
                    break;
            }

            Finalize(snapshot);
            objects[key] = snapshot;
        }

        report.Normalization = sw.Elapsed;

        // Reference graph, only between objects whose both ends are known.
        var references = new Dictionary<ObjectKey, List<ObjectKey>>(comparer);
        foreach (var dependency in catalog.Dependencies)
        {
            if (!keyById.TryGetValue(dependency.ReferencingId, out var from)) continue;
            if (!keyById.TryGetValue(dependency.ReferencedId, out var to)) continue;
            if (!references.TryGetValue(from, out var list)) references[from] = list = [];
            list.Add(to);
        }

        // FOREIGN KEY dependencies go into the graph too. Module dependencies
        // (sql_expression_dependencies) do not include FKs between tables. Without them, a new table
        // whose inline FK points to another new table would be ordered wrongly and its CREATE would
        // run before the referenced table exists (Msg 1767).
        foreach (var fk in catalog.ForeignKeys)
        {
            if (!keyById.TryGetValue(fk.ParentObjectId, out var from)) continue;
            if (!keyById.TryGetValue(fk.ReferencedObjectId, out var to)) continue;
            if (from.Equals(to)) continue;   // a self reference needs no ordering
            if (!references.TryGetValue(from, out var list)) references[from] = list = [];
            if (!list.Contains(to)) list.Add(to);
        }

        // External (cross-database or linked server) references are reported before deployment,
        // because such objects only work when the external source exists on the target too.
        if (catalog.ExternalReferences.Count > 0)
        {
            var samples = catalog.ExternalReferences.Take(5).Select(e =>
            {
                var target = string.Join(".",
                    new[] { e.Server, e.Database, e.RefSchema, e.RefEntity }.Where(s => s.Length > 0));
                return $"[{e.SourceSchema}].[{e.SourceName}] → {target}";
            });
            report.Warnings.Add(
                $"{catalog.ExternalReferences.Count} external (cross-database or linked server) references. These objects " +
                $"only deploy when the external source exists on the target too: {string.Join(", ", samples)}" +
                (catalog.ExternalReferences.Count > 5 ? " …" : string.Empty));
        }

        return new DatabaseSnapshot
        {
            Server = catalog.ServerName,
            Database = catalog.DatabaseName,
            Objects = objects,
            Report = report,
            References = references,
            IgnoredColumnOrder = options.IgnoreColumnOrder,
            IgnoredCollation = options.IgnoreCollation,
            CaseSensitiveNames = options.CaseSensitiveNames,
            CaseSensitiveColumnNames = options.CaseSensitiveColumnNames,
        };
    }

    // Building parts

    /// <summary>
    /// Canonical text of the SET options. ANSI_NULLS and QUOTED_IDENTIFIER can be ignored separately.
    /// When both are ignored the result is empty, which is empty on both sides and makes no difference.
    /// </summary>
    private static string SetOptions(bool ansiNulls, bool quotedIdentifier, SnapshotOptions options)
    {
        var parts = new List<string>(2);
        if (!options.IgnoreAnsiNulls) parts.Add($"ansiNulls={Flag(ansiNulls)}");
        if (!options.IgnoreQuotedIdentifiers) parts.Add($"quotedIdentifier={Flag(quotedIdentifier)}");
        return string.Join('|', parts);
    }

    private static void ApplyModule(
        ObjectSnapshot snapshot, int objectId,
        Dictionary<int, ModuleRow> modules, ConcurrentDictionary<int, string> bodies,
        SnapshotOptions options)
    {
        if (!modules.TryGetValue(objectId, out var module))
        {
            SetPart(snapshot, "body", MarkIncomparable(snapshot, "no row in sys.sql_modules"));
            return;
        }

        if (module.Definition is null)
        {
            SetPart(snapshot, "body",
                MarkIncomparable(snapshot, "definition is NULL, the object is encrypted or VIEW DEFINITION is missing"));
            return;
        }

        if (options.KeepDisplayScripts) snapshot.DisplayScript = module.Definition;

        snapshot.UsesAnsiNulls = module.UsesAnsiNulls;
        snapshot.UsesQuotedIdentifier = module.UsesQuotedIdentifier;

        SetPart(snapshot, "setOptions", SetOptions(module.UsesAnsiNulls, module.UsesQuotedIdentifier, options));
        SetPart(snapshot, "body", bodies.GetValueOrDefault(objectId, string.Empty));
    }

    private static IReadOnlyList<ColumnInfo>? ToColumnInfo(
        List<ColumnRow>? columns, Dictionary<int, string> xmlCollections) =>
        columns?.OrderBy(c => c.ColumnId)
            .Select(c => new ColumnInfo(
                c.Name, c.TypeSchema, c.TypeName, c.MaxLength, c.Precision, c.Scale,
                c.IsNullable, c.Collation, c.IsIdentity, c.IsComputed, c.DefaultDefinition,
                c.DefaultName, c.DefaultIsSystemNamed == true,
                c.IsSparse, c.IsFileStream, c.IsRowGuidCol, c.IsColumnSet,
                XmlCollectionName(c, xmlCollections), c.IsXmlDocument, c.IdentityNotForReplication,
                c.IdentitySeed, c.IdentityIncrement))
            .ToList();

    /// <summary>Collection name of a typed XML column. Null when untyped or when the name could not be resolved.</summary>
    private static string? XmlCollectionName(ColumnRow c, Dictionary<int, string> xmlCollections) =>
        c.XmlCollectionId == 0 ? null : xmlCollections.GetValueOrDefault(c.XmlCollectionId);

    private static string BuildColumns(
        List<ColumnRow>? columns, Dictionary<int, string> xmlCollections, SnapshotOptions options)
    {
        if (columns is null || columns.Count == 0) return string.Empty;

        var ordered = options.IgnoreColumnOrder
            ? columns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            : columns.OrderBy(c => c.ColumnId);

        var sb = new StringBuilder(columns.Count * 96);
        foreach (var c in ordered)
        {
            // When column names are case-insensitive they enter the canonical text in lowercase, so [Id]
            // and [id] give the same hash. When case-sensitive the name is kept as is.
            var columnName = options.CaseSensitiveColumnNames ? c.Name : c.Name.ToLowerInvariant();
            sb.Append("col|").Append(columnName)
              .Append('|').Append(c.TypeSchema).Append('.').Append(c.TypeName)
              .Append("|len=").Append(c.MaxLength.ToString(CultureInfo.InvariantCulture))
              .Append("|prec=").Append(c.Precision.ToString(CultureInfo.InvariantCulture))
              .Append("|scale=").Append(c.Scale.ToString(CultureInfo.InvariantCulture))
              .Append("|null=").Append(Flag(c.IsNullable));

            if (c.Collation is not null && !options.IgnoreCollation) sb.Append("|coll=").Append(c.Collation);

            if (c.IsIdentity)
            {
                // The "identity" token is always written, so whether a column is an identity column is
                // always compared. Seed and increment can be ignored separately.
                sb.Append("|identity");
                if (!options.IgnoreIdentitySeed) sb.Append(";seed=").Append(c.IdentitySeed);
                if (!options.IgnoreIdentityIncrement) sb.Append(";inc=").Append(c.IdentityIncrement);
                // NOT FOR REPLICATION lets replication bypass IDENTITY, which is a semantic difference.
                if (c.IdentityNotForReplication) sb.Append(";nfr");
            }
            if (c.IsComputed)
                sb.Append("|computed=").Append(NormalizeExpression(c.ComputedDefinition, options))
                  .Append(";persisted=").Append(Flag(c.ComputedIsPersisted == true));

            // Temporal PERIOD columns: GENERATED ALWAYS AS ROW START(1) or END(2) plus HIDDEN.
            if (c.GeneratedAlwaysType != 0)
                sb.Append("|genAlways=").Append(c.GeneratedAlwaysType).Append(";hidden=").Append(Flag(c.IsHidden));

            // Storage attributes are part of the physical definition and are only written when set,
            // so the canonical text of a default column does not change and older snapshots stay comparable.
            // For typed XML the collection name is compared, not the id, because ids are environment specific.
            if (XmlCollectionName(c, xmlCollections) is { } collection)
                sb.Append("|xml=").Append(c.IsXmlDocument ? "DOCUMENT" : "CONTENT").Append(' ').Append(collection);

            if (c.IsSparse) sb.Append("|sparse");
            if (c.IsFileStream) sb.Append("|filestream");
            if (c.IsRowGuidCol) sb.Append("|rowguidcol");
            if (c.IsColumnSet) sb.Append("|columnSet");

            if (c.DefaultDefinition is not null)
            {
                sb.Append("|default=");
                if (!(options.IgnoreSystemNamedConstraints && c.DefaultIsSystemNamed == true))
                    sb.Append(FoldName(c.DefaultName ?? string.Empty, options));
                sb.Append(':').Append(NormalizeExpression(c.DefaultDefinition, options));
            }

            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildIndexes(
        int objectId,
        Dictionary<int, List<IndexRow>> indexesBy,
        Dictionary<long, List<IndexColumnRow>> indexColumnsBy,
        Dictionary<long, KeyConstraintRow> keyConstraintByIndex,
        Dictionary<long, string> columnNames,
        Dictionary<long, IndexExtraRow> indexExtrasBy,
        Dictionary<long, string?> indexPlacement,
        SnapshotOptions options)
    {
        if (!indexesBy.TryGetValue(objectId, out var indexes) || indexes.Count == 0) return string.Empty;

        var lines = new List<string>(indexes.Count);
        foreach (var index in indexes)
        {
            var pairKey = Pair(objectId, index.IndexId);
            var name = index.Name ?? "(unnamed)";

            // The index behind a PK or UQ is named after its constraint. System generated names are left out.
            if ((index.IsPrimaryKey || index.IsUniqueConstraint)
                && keyConstraintByIndex.TryGetValue(pairKey, out var kc))
            {
                name = options.IgnoreSystemNamedConstraints && kc.IsSystemNamed ? "(system-named)" : kc.Name;
            }

            var sb = new StringBuilder(128);
            sb.Append("idx|").Append(name)
              .Append('|').Append(index.TypeDesc)
              .Append("|unique=").Append(Flag(index.IsUnique))
              .Append("|pk=").Append(Flag(index.IsPrimaryKey))
              .Append("|uq=").Append(Flag(index.IsUniqueConstraint));

            // Besides IgnoreIndexPhysicalOptions, fill factor and padding can also be ignored separately.
            if (!options.IgnoreIndexPhysicalOptions && !options.IgnoreFillFactor)
                sb.Append("|fill=").Append(index.FillFactor.ToString(CultureInfo.InvariantCulture));
            if (!options.IgnoreIndexPhysicalOptions && !options.IgnoreIndexPadding)
                sb.Append("|padded=").Append(Flag(index.IsPadded));
            if (!options.IgnoreIndexPhysicalOptions)
            {
                sb.Append("|ignoreDupKey=").Append(Flag(index.IgnoreDupKey));
                // ON by default. Only written when OFF so existing hashes do not change.
                if (!index.AllowRowLocks) sb.Append("|rowLocks=0");
                if (!index.AllowPageLocks) sb.Append("|pageLocks=0");

                // OFF by default. Only written when ON so existing hashes do not change.
                var extra = indexExtrasBy.GetValueOrDefault(pairKey);
                if (extra?.OptimizeForSequentialKey == true) sb.Append("|seqKey=1");
                if (extra?.StatisticsNoRecompute == true) sb.Append("|statsNoRecompute=1");
            }
            if (!options.IgnoreDataCompression && ExplicitCompression(index.DataCompression) is { } comp)
                sb.Append("|compression=").Append(comp);

            if (index.FilterDefinition is not null) sb.Append("|filter=").Append(NormalizeExpression(index.FilterDefinition, options));

            var indexColumns = indexColumnsBy.GetValueOrDefault(pairKey) ?? [];

            var keys = indexColumns
                .Where(ic => !ic.IsIncluded && ic.KeyOrdinal > 0)
                .OrderBy(ic => ic.KeyOrdinal)
                .Select(ic => $"{Column(columnNames, objectId, ic.ColumnId)}{(ic.IsDescending ? " DESC" : " ASC")}");
            sb.Append("|keys=").Append(string.Join(',', keys));

            var included = indexColumns
                .Where(ic => ic.IsIncluded)
                .Select(ic => Column(columnNames, objectId, ic.ColumnId))
                .Order(StringComparer.Ordinal);
            sb.Append("|include=").Append(string.Join(',', included));

            // In columnstore indexes the columns have key_ordinal 0 and are not included.
            var unordered = indexColumns
                .Where(ic => !ic.IsIncluded && ic.KeyOrdinal == 0)
                .Select(ic => Column(columnNames, objectId, ic.ColumnId))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (unordered.Count > 0) sb.Append("|cols=").Append(string.Join(',', unordered));

            // Storage placement (filegroup or partition scheme), so a difference shows up in the canonical text.
            if (indexPlacement.GetValueOrDefault(pairKey) is { Length: > 0 } place)
                sb.Append("|place=").Append(FoldName(place, options));

            lines.Add(sb.ToString());
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static string BuildChecks(List<CheckConstraintRow>? checks, SnapshotOptions options)
    {
        if (checks is null || checks.Count == 0) return string.Empty;

        var lines = checks.Select(c =>
        {
            var name = options.IgnoreSystemNamedConstraints && c.IsSystemNamed ? "(system-named)" : c.Name;
            var nfr = c.IsNotForReplication ? "|nfr" : string.Empty;
            return $"chk|{name}|{NormalizeExpression(c.Definition, options)}|disabled={Flag(c.IsDisabled)}|notTrusted={Flag(c.IsNotTrusted)}{nfr}";
        }).ToList();

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static string BuildForeignKeys(
        List<ForeignKeyRow>? foreignKeys,
        Dictionary<int, List<ForeignKeyColumnRow>> fkColumnsBy,
        Dictionary<long, string> columnNames,
        Dictionary<int, ObjectKey> keyById,
        SnapshotOptions options)
    {
        if (foreignKeys is null || foreignKeys.Count == 0) return string.Empty;

        var lines = foreignKeys.Select(fk =>
        {
            var name = options.IgnoreSystemNamedConstraints && fk.IsSystemNamed ? "(system-named)" : fk.Name;
            var referenced = keyById.TryGetValue(fk.ReferencedObjectId, out var refKey)
                ? $"[{FoldName(refKey.Schema, options)}].[{FoldName(refKey.Name, options)}]"
                : $"#{fk.ReferencedObjectId}";

            var columns = (fkColumnsBy.GetValueOrDefault(fk.ObjectId) ?? [])
                .OrderBy(c => c.ConstraintColumnId)
                .Select(c =>
                    $"{Column(columnNames, c.ParentObjectId, c.ParentColumnId)}>" +
                    $"{Column(columnNames, c.ReferencedObjectId, c.ReferencedColumnId)}");

            return $"fk|{name}|ref={referenced}|cols={string.Join(',', columns)}" +
                   $"|onDelete={fk.DeleteAction}|onUpdate={fk.UpdateAction}" +
                   $"|disabled={Flag(fk.IsDisabled)}|notTrusted={Flag(fk.IsNotTrusted)}" +
                   (fk.IsNotForReplication ? "|nfr" : string.Empty);
        }).ToList();

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    // Structural definitions for script generation

    private static List<IndexDefinition> BuildIndexDefinitions(
        int objectId,
        Dictionary<int, List<IndexRow>> indexesBy,
        Dictionary<long, List<IndexColumnRow>> indexColumnsBy,
        Dictionary<long, KeyConstraintRow> keyConstraintByIndex,
        Dictionary<long, string> columnNames,
        Dictionary<long, IndexExtraRow> indexExtrasBy,
        Dictionary<long, string?> indexPlacement,
        SnapshotOptions options)
    {
        var result = new List<IndexDefinition>();

        foreach (var index in indexesBy.GetValueOrDefault(objectId) ?? [])
        {
            var pairKey = Pair(objectId, index.IndexId);
            var name = index.Name;
            var systemNamed = false;

            if ((index.IsPrimaryKey || index.IsUniqueConstraint)
                && keyConstraintByIndex.TryGetValue(pairKey, out var kc))
            {
                name = kc.Name;
                systemNamed = kc.IsSystemNamed;
            }

            // A system named PK or UQ that is left out of the comparison is left out of the script too.
            if ((index.IsPrimaryKey || index.IsUniqueConstraint)
                && options.IgnoreSystemNamedConstraints && systemNamed)
                continue;
            if (string.IsNullOrEmpty(name)) continue;

            var cols = indexColumnsBy.GetValueOrDefault(pairKey) ?? [];
            var keys = cols.Where(c => !c.IsIncluded && c.KeyOrdinal > 0).OrderBy(c => c.KeyOrdinal)
                .Select(c => new IndexKeyColumn(Column(columnNames, objectId, c.ColumnId), c.IsDescending))
                .ToList();
            // Columnstore: key_ordinal 0 and not included.
            keys.AddRange(cols.Where(c => !c.IsIncluded && c.KeyOrdinal == 0)
                .Select(c => new IndexKeyColumn(Column(columnNames, objectId, c.ColumnId), false))
                .OrderBy(k => k.Column, StringComparer.Ordinal));

            var included = cols.Where(c => c.IsIncluded)
                .Select(c => Column(columnNames, objectId, c.ColumnId))
                .Order(StringComparer.Ordinal).ToList();

            result.Add(new IndexDefinition(
                name, index.IsPrimaryKey, index.IsUniqueConstraint, index.IsUnique,
                index.TypeDesc, systemNamed, keys, included, index.FilterDefinition,
                options.IgnoreDataCompression ? null : index.DataCompression,
                // When physical options are ignored the script gets the default (ON) as well.
                options.IgnoreIndexPhysicalOptions || index.AllowRowLocks,
                options.IgnoreIndexPhysicalOptions || index.AllowPageLocks,
                !options.IgnoreIndexPhysicalOptions
                    && indexExtrasBy.GetValueOrDefault(pairKey)?.OptimizeForSequentialKey == true,
                !options.IgnoreIndexPhysicalOptions
                    && indexExtrasBy.GetValueOrDefault(pairKey)?.StatisticsNoRecompute == true,
                indexPlacement.GetValueOrDefault(pairKey)));
        }

        return result;
    }

    private static List<CheckDefinition> BuildCheckDefinitions(List<CheckConstraintRow>? checks, SnapshotOptions options)
    {
        var result = new List<CheckDefinition>();
        foreach (var c in checks ?? [])
        {
            if (options.IgnoreSystemNamedConstraints && c.IsSystemNamed) continue;
            if (c.Definition is null) continue;
            result.Add(new CheckDefinition(
                c.Name, c.Definition, c.IsSystemNamed, c.IsNotForReplication, c.IsDisabled, c.IsNotTrusted));
        }
        return result;
    }

    private static List<ForeignKeyDefinition> BuildForeignKeyDefinitions(
        List<ForeignKeyRow>? foreignKeys,
        Dictionary<int, List<ForeignKeyColumnRow>> fkColumnsBy,
        Dictionary<long, string> columnNames,
        Dictionary<int, ObjectKey> keyById,
        SnapshotOptions options)
    {
        var result = new List<ForeignKeyDefinition>();
        foreach (var fk in foreignKeys ?? [])
        {
            if (options.IgnoreSystemNamedConstraints && fk.IsSystemNamed) continue;

            var referenced = keyById.TryGetValue(fk.ReferencedObjectId, out var refKey)
                ? refKey
                : new ObjectKey(string.Empty, $"#{fk.ReferencedObjectId}", ObjectKind.Table);

            var cols = (fkColumnsBy.GetValueOrDefault(fk.ObjectId) ?? [])
                .OrderBy(c => c.ConstraintColumnId)
                .Select(c => new ForeignKeyColumnPair(
                    Column(columnNames, c.ParentObjectId, c.ParentColumnId),
                    Column(columnNames, c.ReferencedObjectId, c.ReferencedColumnId)))
                .ToList();

            result.Add(new ForeignKeyDefinition(
                fk.Name, fk.IsSystemNamed, referenced.Schema, referenced.Name, cols,
                fk.DeleteAction, fk.UpdateAction, fk.IsNotForReplication, fk.IsDisabled, fk.IsNotTrusted));
        }
        return result;
    }

    /// <summary>
    /// Canonical text for system versioning. A generated history table name
    /// (MSSQL_TemporalHistoryFor_&lt;object_id&gt;) differs between environments, so it is reduced
    /// to a fixed placeholder and only a name chosen by the user is compared.
    /// </summary>
    private static string BuildTemporal(TemporalRow t)
    {
        string history;
        if (t.HistoryName is null) history = "-";
        else if (t.HistoryName.StartsWith("MSSQL_TemporalHistoryFor_", StringComparison.OrdinalIgnoreCase))
            history = "(auto)";
        else history = $"[{t.HistorySchema}].[{t.HistoryName}]";

        return $"temporal|versioning=ON|history={history}|start={t.StartColumn ?? "-"}|end={t.EndColumn ?? "-"}";
    }

    // Helpers

    private static void SetPart(ObjectSnapshot snapshot, string name, string canonical)
    {
        if (canonical.Length == 0) return;
        snapshot.PartCanonical[name] = canonical;
        snapshot.Parts[name] = Hash.Of(canonical);
    }

    /// <summary>
    /// Like <see cref="SetPart"/>, but the hash is computed with the line name (fields[1])
    /// lowercased while PartCanonical keeps the raw text, so the tree shows index and constraint
    /// names with their real casing. In case-insensitive mode a name that only changed its casing
    /// does not change the hash and the object is not shown as changed for nothing. Only the name
    /// token is folded. Values (definition, expression, column list) go into the hash unchanged,
    /// so real value differences are not masked.
    /// </summary>
    private static void SetPartFoldNames(ObjectSnapshot snapshot, string name, string canonical, SnapshotOptions options)
    {
        if (canonical.Length == 0) return;
        snapshot.PartCanonical[name] = canonical;
        snapshot.Parts[name] = Hash.Of(options.CaseSensitiveNames ? canonical : FoldLineNames(canonical));
    }

    /// <summary>Lowercases the name field of every line in a canonical text (the token after the
    /// first "|"). The rest of the line is unchanged. Only used as hash input.</summary>
    private static string FoldLineNames(string canonical)
    {
        var lines = canonical.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var f = lines[i].Split('|');
            if (f.Length >= 2 && f[1].Length > 0)
            {
                f[1] = f[1].ToLowerInvariant();
                lines[i] = string.Join('|', f);
            }
        }
        return string.Join('\n', lines);
    }

    private static void Finalize(ObjectSnapshot snapshot)
    {
        var ordered = snapshot.Parts.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        snapshot.Hash = Hash.Combine(ordered.Select(p => p.Value));
        snapshot.Canonical = string.Join('\n',
            ordered.Select(p => $"-- [{p.Key}]\n{snapshot.PartCanonical[p.Key]}"));
    }

    private static string MarkIncomparable(ObjectSnapshot snapshot, string reason)
    {
        snapshot.Incomparable = true;
        snapshot.IncomparableReason = reason;
        return $"<incomparable: {reason}>";
    }

    private static string Column(Dictionary<long, string> names, int objectId, int columnId) =>
        names.GetValueOrDefault(Pair(objectId, columnId), $"#{columnId}");

    /// <summary>Lowercases an identifier in case-insensitive name mode (CaseSensitiveNames off).
    /// Index, constraint and statistics names often contain the table name (UCIDX_&lt;Table&gt;),
    /// so an object that only changed its casing (CustomerShareholderKey and CustomerShareHolderKey)
    /// must not look changed because of those names.</summary>
    private static string FoldName(string name, SnapshotOptions options) =>
        options.CaseSensitiveNames ? name : name.ToLowerInvariant();

    /// <summary>
    /// Canonicalizes free text T-SQL expressions (CHECK, computed column, DEFAULT and filter
    /// definitions) with the same rules as module bodies. Whitespace, comments, punctuation,
    /// identifier quoting, keyword casing and, in case-insensitive mode, identifier casing are
    /// folded while string literals are kept so value differences are not masked. CHECK ([Status]>0)
    /// and CHECK ([status] > 0) are equal, ([Status]='A') and ([Status]='a') are not.
    /// It is based on the lexer (GetTokenStream), so it also works on expression fragments.
    /// </summary>
    private static string NormalizeExpression(string? definition, SnapshotOptions options)
    {
        if (string.IsNullOrEmpty(definition)) return definition ?? string.Empty;
        var norm = options.Normalization with { FoldIdentifierCase = !options.CaseSensitiveNames };
        return Normalization.TSqlNormalizer.Normalize(definition, quotedIdentifiers: true, norm);
    }

    private static char Flag(bool value) => value ? '1' : '0';

    /// <summary>
    /// Returns the compression value only when it has to be written explicitly. NONE (the default)
    /// and plain COLUMNSTORE (implied by the index type) are skipped, ROW, PAGE and COLUMNSTORE_ARCHIVE remain.
    /// </summary>
    internal static string? ExplicitCompression(string? desc) =>
        desc is "ROW" or "PAGE" or "COLUMNSTORE_ARCHIVE" ? desc : null;

    private static long Pair(int high, int low) => ((long)high << 32) | (uint)low;

    private static Dictionary<TKey, List<TValue>> GroupBy<TKey, TValue>(
        List<TValue> source, Func<TValue, TKey> keySelector) where TKey : notnull
    {
        var result = new Dictionary<TKey, List<TValue>>();
        foreach (var item in source)
        {
            var key = keySelector(item);
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(item);
        }
        return result;
    }
}
