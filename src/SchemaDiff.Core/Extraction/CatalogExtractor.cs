using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Extraction;

internal sealed class CatalogSet
{
    public List<SchemaRow> Schemas { get; set; } = [];
    public List<ObjectRow> Objects { get; set; } = [];
    public List<ModuleRow> Modules { get; set; } = [];
    public List<ColumnRow> Columns { get; set; } = [];
    public List<IndexRow> Indexes { get; set; } = [];
    public List<IndexColumnRow> IndexColumns { get; set; } = [];
    public List<XmlSchemaCollectionRow> XmlSchemaCollections { get; set; } = [];
    public List<IndexExtraRow> IndexExtras { get; set; } = [];
    public List<KeyConstraintRow> KeyConstraints { get; set; } = [];
    public List<ForeignKeyRow> ForeignKeys { get; set; } = [];
    public List<ForeignKeyColumnRow> ForeignKeyColumns { get; set; } = [];
    public List<CheckConstraintRow> CheckConstraints { get; set; } = [];
    public List<TriggerRow> Triggers { get; set; } = [];
    public List<RowCountRow> RowCounts { get; set; } = [];
    public List<TemporalRow> Temporal { get; set; } = [];
    public List<DependencyRow> Dependencies { get; set; } = [];
    public List<ExternalReferenceRow> ExternalReferences { get; set; } = [];
    public List<DataSpaceRow> DataSpaces { get; set; } = [];
    public List<TablePlacementRow> TablePlacements { get; set; } = [];
    public List<PartitionColumnRow> PartitionColumns { get; set; } = [];

    public string ServerName { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public bool HasViewDefinition { get; set; }
}

/// <summary>
/// Reads the schema metadata with one query per object class instead of one per object.
/// The queries run on parallel connections, so there is no need to build a semantic model like DacFx.
/// Optional queries that fail on an older version or because of missing permissions do not stop the
/// comparison. Their names go to ExtractionReport.FailedQueries so "could not read" stays apart from "none".
/// </summary>
public sealed class CatalogExtractor(ExtractionGate? gate = null, int commandTimeoutSeconds = 300)
{
    private readonly ExtractionGate _gate = gate ?? ExtractionGate.Unbounded;

    internal async Task<(CatalogSet Catalog, ExtractionReport Report)> ExtractAsync(
        string connectionString, CancellationToken ct = default, CompareProgress? progress = null)
    {
        progress ??= CompareProgress.None;
        var report = new ExtractionReport();
        var catalog = new CatalogSet();
        var total = Stopwatch.StartNew();

        progress.EnterPhase(ComparePhase.Connecting);
        await PreflightAsync(connectionString, catalog, report, ct);
        progress.EnterPhase(ComparePhase.Extracting);

        // Each query runs on its own connection. They all run in parallel, so the total time is the slowest query.
        var tasks = new List<Task>
        {
            Run("schemas", Sql.Schemas, r => new SchemaRow(Rdr.Int(r, 0), Rdr.Str(r, 1)), rows => catalog.Schemas = rows),
            Run("objects", Sql.Objects, MapObject, rows => catalog.Objects = rows),
            Run("modules", Sql.Modules, MapModule, rows => catalog.Modules = rows),
            Run("columns", Sql.Columns, MapColumn, rows => catalog.Columns = rows),
            Run("indexes", Sql.Indexes, MapIndex, rows => catalog.Indexes = rows),
            Run("indexColumns", Sql.IndexColumns, MapIndexColumn, rows => catalog.IndexColumns = rows),
            Run("keyConstraints", Sql.KeyConstraints, MapKeyConstraint, rows => catalog.KeyConstraints = rows),
            Run("foreignKeys", Sql.ForeignKeys, MapForeignKey, rows => catalog.ForeignKeys = rows),
            Run("foreignKeyColumns", Sql.ForeignKeyColumns, MapForeignKeyColumn, rows => catalog.ForeignKeyColumns = rows),
            Run("checkConstraints", Sql.CheckConstraints, MapCheckConstraint, rows => catalog.CheckConstraints = rows),
            Run("triggers", Sql.Triggers, MapTrigger, rows => catalog.Triggers = rows),

            // Needs a permission. The comparison still works without it, only the risk analysis gets weaker.
            Run("rowCounts", Sql.RowCounts, MapRowCount, rows => catalog.RowCounts = rows, optional: true),

            // sys.periods does not exist on old versions. Missing it must not stop the comparison.
            Run("temporal", Sql.Temporal, MapTemporal, rows => catalog.Temporal = rows, optional: true),

            // Collection names of typed XML columns. Without them the column would be scripted as plain "xml".
            Run("xmlSchemaCollections", Sql.XmlSchemaCollections, MapXmlSchemaCollection,
                rows => catalog.XmlSchemaCollections = rows, optional: true),

            // optimize_for_sequential_key needs SQL 2019 or later. On older servers this query fails
            // and the comparison continues without these two settings.
            Run("indexExtras", Sql.IndexExtras, MapIndexExtra, rows => catalog.IndexExtras = rows, optional: true),

            Run("externalReferences", Sql.ExternalReferences, MapExternalReference,
                rows => catalog.ExternalReferences = rows, optional: true),

            // Only used by script generation and does not affect the comparison.
            Run("dependencies", Sql.Dependencies, MapDependency, rows => catalog.Dependencies = rows, optional: true),

            // Storage placement: filegroups, partition schemes and partitioning columns.
            Run("dataSpaces", Sql.DataSpaces, MapDataSpace,
                rows => catalog.DataSpaces = rows, optional: true),
            Run("tablePlacement", Sql.TablePlacement, MapTablePlacement,
                rows => catalog.TablePlacements = rows, optional: true),
            Run("partitionColumns", Sql.PartitionColumns, MapPartitionColumn,
                rows => catalog.PartitionColumns = rows, optional: true),
        };

        await Task.WhenAll(tasks);
        report.TotalExtraction = total.Elapsed;

        VerifyDefinitionsVisible(catalog, report);
        return (catalog, report);

        // AddQueries is called synchronously because the collection initializer evaluates each Run in order.
        // The total is therefore complete before WhenAll and done never gets ahead of total.
        Task Run<T>(string name, string sql, Func<SqlDataReader, T> map, Action<List<T>> assign,
                    bool optional = false)
        {
            progress.AddQueries(1);
            return Task.Run(async () =>
            {
                try
                {
                    var (rows, elapsed) = await _gate.RunAsync(
                        () => QueryAsync(connectionString, sql, map, ct), ct);
                    assign(rows);
                    lock (report)
                    {
                        report.QueryTimings[name] = elapsed;
                        report.RowCounts[name] = rows.Count;
                    }
                }
                catch (Exception ex) when (optional)
                {
                    lock (report)
                    {
                        report.FailedQueries.Add(name);
                        report.Warnings.Add(
                            $"Query '{name}' could not run ({ex.Message.Split('\n')[0]}). " +
                            "The comparison continues, but analyses that rely on this data will be incomplete.");
                    }
                }
                finally
                {
                    // A failed optional query also counts as finished so the progress bar does not get stuck.
                    progress.QueryCompleted();
                }
            }, ct);
        }
    }

    private async Task PreflightAsync(
        string connectionString, CatalogSet catalog, ExtractionReport report, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Sql.Preflight, conn) { CommandTimeout = commandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            catalog.ServerName = reader.IsDBNull(0) ? conn.DataSource : reader.GetString(0);
            catalog.DatabaseName = reader.GetString(1);
            catalog.ProductVersion = reader.IsDBNull(2) ? "?" : Convert.ToString(reader.GetValue(2)) ?? "?";
            catalog.HasViewDefinition = !reader.IsDBNull(3) && reader.GetInt32(3) == 1;
        }
        report.QueryTimings["preflight"] = sw.Elapsed;
    }

    /// <summary>
    /// Without VIEW DEFINITION, sys.sql_modules.definition silently returns NULL and every module
    /// looks "equal". That is the most dangerous wrong answer this tool could give.
    /// </summary>
    private static void VerifyDefinitionsVisible(CatalogSet catalog, ExtractionReport report)
    {
        if (catalog.Modules.Count == 0) return;

        var nullDefinitions = catalog.Modules.Count(m => m.Definition is null);
        if (nullDefinitions == catalog.Modules.Count)
        {
            report.Warnings.Add(
                $"CRITICAL: the definition is NULL for all {catalog.Modules.Count} modules. " +
                "VIEW DEFINITION is missing and the comparison result cannot be trusted.");
        }
        else if (nullDefinitions > 0)
        {
            report.Warnings.Add(
                $"WARNING: the definition is NULL for {nullDefinitions} of {catalog.Modules.Count} modules " +
                "(encrypted objects or partial permissions). These objects are treated as not comparable.");
        }

        if (!catalog.HasViewDefinition)
        {
            report.Warnings.Add("The connected user does not appear to have VIEW DEFINITION at database level.");
        }
    }

    private async Task<(List<T> Rows, TimeSpan Elapsed)> QueryAsync<T>(
        string connectionString, string sql, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<T>(capacity: 1024);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));

        return (rows, sw.Elapsed);
    }

    // Mappers. Ordinals must be read in strictly increasing order (SequentialAccess).

    private static ObjectRow MapObject(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3), Rdr.Date(r, 4), Rdr.Int(r, 5));

    private static ModuleRow MapModule(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NStr(r, 1), Rdr.Bool(r, 2), Rdr.Bool(r, 3));

    private static ColumnRow MapColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2),
        Rdr.Str(r, 3), Rdr.Str(r, 4),
        Rdr.Short(r, 5), Rdr.Byte(r, 6), Rdr.Byte(r, 7),
        Rdr.Bool(r, 8), Rdr.NStr(r, 9), Rdr.Bool(r, 10), Rdr.Bool(r, 11),
        Rdr.NStr(r, 12), Rdr.NStr(r, 13), Rdr.NBool(r, 14),
        Rdr.NStr(r, 15), Rdr.NBool(r, 16),
        Rdr.Variant(r, 17), Rdr.Variant(r, 18),
        Rdr.Byte(r, 19), Rdr.Bool(r, 20),
        Rdr.Bool(r, 21), Rdr.Bool(r, 22), Rdr.Bool(r, 23), Rdr.Bool(r, 24),
        Rdr.Int(r, 25), Rdr.Bool(r, 26), Rdr.NBool(r, 27) == true);

    private static XmlSchemaCollectionRow MapXmlSchemaCollection(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NStr(r, 1) ?? "dbo", Rdr.Str(r, 2));

    private static IndexRow MapIndex(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.NStr(r, 2), Rdr.Str(r, 3),
        Rdr.Bool(r, 4), Rdr.Bool(r, 5), Rdr.Bool(r, 6),
        Rdr.Byte(r, 7), Rdr.Bool(r, 8), Rdr.Bool(r, 9), Rdr.NStr(r, 10), Rdr.NStr(r, 11),
        Rdr.Bool(r, 12), Rdr.Bool(r, 13), Rdr.Int(r, 14));

    private static DataSpaceRow MapDataSpace(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Str(r, 2).Trim());

    private static TablePlacementRow MapTablePlacement(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Int(r, 2));

    private static PartitionColumnRow MapPartitionColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2));

    private static IndexColumnRow MapIndexColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Int(r, 2), Rdr.Int(r, 3),
        Rdr.Byte(r, 4), Rdr.Bool(r, 5), Rdr.Bool(r, 6));

    private static IndexExtraRow MapIndexExtra(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.NBool(r, 2) == true, Rdr.NBool(r, 3) == true);

    private static KeyConstraintRow MapKeyConstraint(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NInt(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3), Rdr.Bool(r, 4));

    private static ForeignKeyRow MapForeignKey(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2), Rdr.Bool(r, 3), Rdr.Int(r, 4),
        Rdr.Byte(r, 5), Rdr.Byte(r, 6), Rdr.Bool(r, 7), Rdr.Bool(r, 8), Rdr.Bool(r, 9));

    private static ForeignKeyColumnRow MapForeignKeyColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Int(r, 2), Rdr.Int(r, 3), Rdr.Int(r, 4), Rdr.Int(r, 5));

    private static CheckConstraintRow MapCheckConstraint(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Bool(r, 2), Rdr.NStr(r, 3), Rdr.Bool(r, 4), Rdr.Bool(r, 5),
        Rdr.Bool(r, 6));

    private static TriggerRow MapTrigger(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Bool(r, 1), Rdr.Bool(r, 2));

    private static RowCountRow MapRowCount(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Long(r, 1));

    private static TemporalRow MapTemporal(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NStr(r, 1), Rdr.NStr(r, 2), Rdr.NStr(r, 3), Rdr.NStr(r, 4));

    private static ExternalReferenceRow MapExternalReference(SqlDataReader r) => new(
        Rdr.Str(r, 0), Rdr.Str(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3), Rdr.Str(r, 4), Rdr.Str(r, 5));

    private static DependencyRow MapDependency(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1));

}
