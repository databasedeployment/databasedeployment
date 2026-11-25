using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace SchemaDiff.Core.Analysis;

/// <param name="Covered">Whether the comparison extracts this class today.</param>
/// <param name="Count">How many the database has, or null when the probe query failed.</param>
/// <param name="Error">First line of the error when the probe query failed.</param>
public sealed record CoverageItem(
    string Category, string Item, bool Covered, long? Count, string? Error);

public sealed record CoverageReport(
    string Server, string Database, string ProductVersion,
    IReadOnlyList<CoverageItem> Items, TimeSpan Elapsed)
{
    /// <summary>Classes the comparison does not cover but the database really contains. This is the to-do list.</summary>
    public IReadOnlyList<CoverageItem> Gaps =>
        [.. Items.Where(i => !i.Covered && i.Count > 0).OrderByDescending(i => i.Count)];
}

/// <summary>
/// Answers "what are we missing in this database?".
///
/// The comparison silently skips object classes it does not cover. That is one of the most dangerous
/// wrong answers the tool can give: the report looks clean but is incomplete. The probe counts how
/// many objects of each class the database contains, so a coverage gap is known by number and not by guess.
///
/// Every probe runs on its own and a failure does not stop the others. When a catalog view does not
/// exist on that SQL Server version or the permission is not enough, the report is still completed.
/// </summary>
public static class CoverageProbe
{
    // Covered reflects what the catalog extraction reads today. Classes that are not covered yet
    // are probed as well, and a count above zero puts them on the gap list.
    internal static readonly (string Category, string Item, bool Covered, string Sql)[] Probes =
    [
        // Covered
        ("Schema", "Schemas", true, "SELECT COUNT(*) FROM sys.schemas WHERE schema_id > 4 AND schema_id < 16384"),
        ("Table", "Tables", true, "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0"),
        ("Table", "Columns", true, "SELECT COUNT(*) FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id WHERE o.is_ms_shipped = 0"),
        // XML (3) and spatial (4) indexes are not read by the index query and have their own probes below.
        ("Table", "Indexes", true, "SELECT COUNT(*) FROM sys.indexes i JOIN sys.objects o ON o.object_id = i.object_id WHERE o.is_ms_shipped = 0 AND i.type NOT IN (0, 3, 4)"),
        ("Table", "Foreign keys", true, "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_ms_shipped = 0"),
        ("Table", "Check constraints", true, "SELECT COUNT(*) FROM sys.check_constraints WHERE is_ms_shipped = 0"),
        ("Table", "Default constraints", true, "SELECT COUNT(*) FROM sys.default_constraints WHERE is_ms_shipped = 0"),
        ("Table", "Temporal (system-versioned) tables", true, "SELECT COUNT(*) FROM sys.tables WHERE temporal_type = 2"),
        ("Table", "Sparse, column set, FILESTREAM and ROWGUIDCOL columns", true, "SELECT COUNT(*) FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id WHERE o.is_ms_shipped = 0 AND (c.is_sparse = 1 OR c.is_column_set = 1 OR c.is_filestream = 1 OR c.is_rowguidcol = 1)"),
        ("Storage", "Compressed partitions (DATA_COMPRESSION)", true, "SELECT COUNT(*) FROM sys.partitions p JOIN sys.objects o ON o.object_id = p.object_id WHERE o.is_ms_shipped = 0 AND p.data_compression <> 0"),
        ("Module", "Views", true, "SELECT COUNT(*) FROM sys.views WHERE is_ms_shipped = 0"),
        ("Module", "Procedures", true, "SELECT COUNT(*) FROM sys.procedures WHERE is_ms_shipped = 0"),
        ("Module", "Functions", true, "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('FN','IF','TF')"),
        ("Module", "DML triggers", true, "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 1 AND is_ms_shipped = 0"),

        // Not covered yet. A count above zero is a gap.
        ("Module", "Encrypted or unreadable modules", false, "SELECT COUNT(*) FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.is_ms_shipped = 0 AND m.definition IS NULL"),
        ("Other", "Sequences", false, "SELECT COUNT(*) FROM sys.sequences WHERE is_ms_shipped = 0"),
        ("Other", "Synonyms", false, "SELECT COUNT(*) FROM sys.synonyms WHERE is_ms_shipped = 0"),
        ("Trigger", "DDL triggers", false, "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0"),
        ("Type", "User-defined alias types", false, "SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1 AND is_table_type = 0 AND is_assembly_type = 0"),
        ("Type", "User-defined table types", false, "SELECT COUNT(*) FROM sys.table_types WHERE is_user_defined = 1"),
        ("Type", "CLR (assembly) types", false, "SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1 AND is_assembly_type = 1"),
        ("Storage", "Partition functions", false, "SELECT COUNT(*) FROM sys.partition_functions"),
        ("Storage", "Partition schemes", false, "SELECT COUNT(*) FROM sys.partition_schemes"),
        ("Storage", "Filegroups other than PRIMARY", false, "SELECT COUNT(*) FROM sys.filegroups WHERE data_space_id > 1"),
        ("Index", "XML indexes", false, "SELECT COUNT(*) FROM sys.xml_indexes"),
        ("Index", "Spatial indexes", false, "SELECT COUNT(*) FROM sys.spatial_indexes"),
        ("Index", "User-created statistics", false, "SELECT COUNT(*) FROM sys.stats WHERE user_created = 1"),
        ("Index", "Auto-created statistics (runtime artifacts, out of scope)", false, "SELECT COUNT(*) FROM sys.stats WHERE auto_created = 1"),
        ("Full-text", "Full-text catalogs", false, "SELECT COUNT(*) FROM sys.fulltext_catalogs"),
        ("Full-text", "Full-text stoplists", false, "SELECT COUNT(*) FROM sys.fulltext_stoplists"),
        ("Full-text", "Full-text indexes", false, "SELECT COUNT(*) FROM sys.fulltext_indexes"),
        ("Security", "Object and schema permissions (GRANT and DENY)", false, "SELECT COUNT(*) FROM sys.database_permissions WHERE class IN (1, 3)"),
        ("Security", "Database level permissions", false, "SELECT COUNT(*) FROM sys.database_permissions WHERE class = 0"),
        ("Security", "User-defined roles", false, "SELECT COUNT(*) FROM sys.database_principals WHERE type = 'R' AND is_fixed_role = 0 AND principal_id > 4 AND name <> 'public'"),
        ("Security", "Role memberships", false, "SELECT COUNT(*) FROM sys.database_role_members"),
        ("Security", "Database users", false, "SELECT COUNT(*) FROM sys.database_principals WHERE principal_id > 4 AND type IN ('S','U','G','E','X')"),
        ("Security", "Application roles", false, "SELECT COUNT(*) FROM sys.database_principals WHERE type = 'A'"),
        ("Security", "Row-Level Security (security policies)", false, "SELECT COUNT(*) FROM sys.security_policies"),
        ("Security", "Dynamic Data Masking columns", false, "SELECT COUNT(*) FROM sys.masked_columns"),
        ("Security", "Always Encrypted columns", false, "SELECT COUNT(*) FROM sys.columns WHERE encryption_type IS NOT NULL"),
        ("Security", "Always Encrypted keys (column master and encryption keys)", false, "SELECT (SELECT COUNT(*) FROM sys.column_master_keys) + (SELECT COUNT(*) FROM sys.column_encryption_keys)"),
        ("Security", "Certificates", false, "SELECT COUNT(*) FROM sys.certificates"),
        ("Security", "Symmetric keys", false, "SELECT COUNT(*) FROM sys.symmetric_keys WHERE name NOT LIKE '##%'"),
        ("Security", "Asymmetric keys", false, "SELECT COUNT(*) FROM sys.asymmetric_keys"),
        ("Security", "Database scoped credentials", false, "SELECT COUNT(*) FROM sys.database_scoped_credentials"),
        ("Metadata", "Extended properties (database, object, column, schema)", false, "SELECT COUNT(*) FROM sys.extended_properties WHERE class IN (0, 1, 3)"),
        ("Metadata", "Extended properties (parameter, principal, index)", false, "SELECT COUNT(*) FROM sys.extended_properties WHERE class IN (2, 4, 7)"),
        ("Metadata", "Extended properties (other classes)", false, "SELECT COUNT(*) FROM sys.extended_properties WHERE class NOT IN (0, 1, 2, 3, 4, 7)"),
        ("CLR", "Assemblies", false, "SELECT COUNT(*) FROM sys.assemblies WHERE is_user_defined = 1"),
        ("CLR", "CLR procedures and functions", false, "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('PC','FS','FT','AF')"),
        ("Table", "In-memory OLTP tables", false, "SELECT COUNT(*) FROM sys.tables WHERE is_memory_optimized = 1"),
        ("Table", "Graph node and edge tables", false, "SELECT COUNT(*) FROM sys.tables WHERE is_node = 1 OR is_edge = 1"),
        ("Programmability", "XML schema collections", false, "SELECT COUNT(*) FROM sys.xml_schema_collections WHERE schema_id <> 4"),
        ("Programmability", "Plan guides", false, "SELECT COUNT(*) FROM sys.plan_guides"),
        ("Programmability", "Service Broker (queues, services, contracts)", false, "SELECT (SELECT COUNT(*) FROM sys.service_queues WHERE is_ms_shipped = 0) + (SELECT COUNT(*) FROM sys.services WHERE service_id > 5) + (SELECT COUNT(*) FROM sys.service_contracts WHERE service_contract_id > 5)"),
        ("Integration", "External data sources and tables (PolyBase)", false, "SELECT (SELECT COUNT(*) FROM sys.external_data_sources) + (SELECT COUNT(*) FROM sys.external_tables)"),
        ("Settings", "Database scoped configurations", false, "SELECT COUNT(*) FROM sys.database_scoped_configurations WHERE is_value_default = 0"),
        ("Legacy", "CREATE RULE and CREATE DEFAULT objects", false, "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('R', 'D') AND parent_object_id = 0"),
    ];

    public static async Task<CoverageReport> RunAsync(string connectionString, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        string server = connection.DataSource, database = connection.Database, version = "?";
        await using (var header = new SqlCommand(
            "SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')), DB_NAME(), CONVERT(nvarchar(64), SERVERPROPERTY('ProductVersion'))",
            connection))
        await using (var reader = await header.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0)) server = reader.GetString(0);
                database = reader.GetString(1);
                if (!reader.IsDBNull(2)) version = reader.GetString(2);
            }
        }

        var items = await CountAsync(async (sql, token) =>
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            return await command.ExecuteScalarAsync(token);
        }, ct);

        return new CoverageReport(server, database, version, items, stopwatch.Elapsed);
    }

    /// <summary>
    /// Runs every probe through <paramref name="scalar"/> one after the other. A database error
    /// (a missing catalog view or a denied permission) is kept on the item and the next probe runs.
    /// Anything else, cancellation included, stops the probe.
    /// </summary>
    internal static async Task<IReadOnlyList<CoverageItem>> CountAsync(
        Func<string, CancellationToken, Task<object?>> scalar, CancellationToken ct = default)
    {
        var items = new List<CoverageItem>(Probes.Length);
        foreach (var (category, item, covered, sql) in Probes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var value = await scalar(sql, ct);
                items.Add(new CoverageItem(category, item, covered, Convert.ToInt64(value), null));
            }
            catch (DbException ex)
            {
                items.Add(new CoverageItem(category, item, covered, null, ex.Message.Split('\n')[0].Trim()));
            }
        }
        return items;
    }
}
