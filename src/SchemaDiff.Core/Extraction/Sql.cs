namespace SchemaDiff.Core.Extraction;

/// <summary>
/// Catalog queries. The rule is one query per object class and never one query per object.
/// All of them are read-only and independent of each other, so they can run in parallel.
/// </summary>
internal static class Sql
{
    public const string Preflight = """
        SELECT
            CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')),
            DB_NAME(),
            SERVERPROPERTY('ProductVersion'),
            CONVERT(int, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'));
        """;

    // schema_id 1..4 are built in (dbo, guest, INFORMATION_SCHEMA, sys),
    // 16384 and above belong to the fixed database roles.
    public const string Schemas = """
        SELECT s.schema_id, s.name
        FROM sys.schemas AS s
        WHERE s.schema_id > 4 AND s.schema_id < 16384;
        """;

    // Temporal history tables (temporal_type=1) are excluded. They belong to their system-versioned
    // table and are not compared as separate objects (their names are often generated).
    public const string Objects = """
        SELECT o.object_id, s.name, o.name, RTRIM(o.type), o.modify_date, o.parent_object_id
        FROM sys.objects AS o
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('U','V','P','FN','IF','TF','TR')
          AND o.object_id NOT IN (SELECT ht.object_id FROM sys.tables AS ht WHERE ht.temporal_type = 1)
          -- SSMS database diagram support objects (sysdiagrams, fn_diagramobjects, sp_*diagram)
          -- carry the microsoft_database_tools_support extended property. SSDT skips them and so do we.
          AND NOT EXISTS (
              SELECT 1 FROM sys.extended_properties AS dtp
              WHERE dtp.class = 1 AND dtp.major_id = o.object_id AND dtp.minor_id = 0
                AND dtp.name = N'microsoft_database_tools_support');
        """;

    // System-versioned temporal tables (temporal_type=2) with their history table and PERIOD columns.
    // sys.periods may be missing on old versions, so the query is optional.
    public const string Temporal = """
        SELECT t.object_id, SCHEMA_NAME(h.schema_id), h.name, sc.name, ec.name
        FROM sys.tables AS t
        LEFT JOIN sys.tables  AS h  ON h.object_id = t.history_table_id
        LEFT JOIN sys.periods AS p  ON p.object_id = t.object_id
        LEFT JOIN sys.columns AS sc ON sc.object_id = t.object_id AND sc.column_id = p.start_column_id
        LEFT JOIN sys.columns AS ec ON ec.object_id = t.object_id AND ec.column_id = p.end_column_id
        WHERE t.is_ms_shipped = 0 AND t.temporal_type = 2;
        """;

    public const string Modules = """
        SELECT m.object_id, m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier
        FROM sys.sql_modules AS m
        INNER JOIN sys.objects AS o ON o.object_id = m.object_id
        WHERE o.is_ms_shipped = 0;
        """;

    public const string Columns = """
        SELECT c.object_id, c.column_id, c.name,
               SCHEMA_NAME(tp.schema_id), tp.name,
               c.max_length, c.precision, c.scale,
               c.is_nullable, c.collation_name, c.is_identity, c.is_computed,
               dc.name, dc.definition, dc.is_system_named,
               cc.definition, cc.is_persisted,
               ic.seed_value, ic.increment_value,
               c.generated_always_type, c.is_hidden,
               c.is_sparse, c.is_filestream, c.is_rowguidcol, c.is_column_set,
               c.xml_collection_id, c.is_xml_document, ic.is_not_for_replication
        FROM sys.columns AS c
        INNER JOIN sys.objects AS o
            ON o.object_id = c.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V')
        INNER JOIN sys.types AS tp ON tp.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.computed_columns AS cc
            ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.identity_columns AS ic
            ON ic.object_id = c.object_id AND ic.column_id = c.column_id;
        """;

    // Resolves the collection name of typed XML columns (xml(CONTENT [schema].[collection])).
    // The column only stores an id. Without the name the script would write plain "xml" and create the column wrongly.
    public const string XmlSchemaCollections = """
        SELECT xsc.xml_collection_id, SCHEMA_NAME(xsc.schema_id), xsc.name
        FROM sys.xml_schema_collections AS xsc;
        """;

    // type = 0 is a heap and has no definition to compare.
    // allow_row_locks and allow_page_locks exist since SQL 2005. optimize_for_sequential_key (2019+)
    // is deliberately not read here. This query is required and failing on an old server would end the comparison.
    public const string Indexes = """
        SELECT i.object_id, i.index_id, i.name, i.type_desc,
               i.is_unique, i.is_primary_key, i.is_unique_constraint,
               i.fill_factor, i.is_padded, i.ignore_dup_key, i.filter_definition,
               p.data_compression_desc,
               i.allow_row_locks, i.allow_page_locks, i.data_space_id
        FROM sys.indexes AS i
        INNER JOIN sys.objects AS o
            ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V')
        LEFT JOIN sys.partitions AS p
            ON p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1
        WHERE i.type NOT IN (0, 3, 4);
        """;

    // Extra index options live in a separate, optional query:
    //   - optimize_for_sequential_key, added in SQL Server 2019
    //   - no_recompute (STATISTICS_NORECOMPUTE), read from the statistics behind the index
    // In the required Indexes query they would make it fail on 2016 or 2017 and stop the whole
    // comparison. Here a failure only leaves these two settings out of the comparison.
    public const string IndexExtras = """
        SELECT i.object_id, i.index_id, i.optimize_for_sequential_key, st.no_recompute
        FROM sys.indexes AS i
        INNER JOIN sys.objects AS o
            ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V')
        LEFT JOIN sys.stats AS st
            ON st.object_id = i.object_id AND st.stats_id = i.index_id
        WHERE i.type NOT IN (0, 3, 4);
        """;

    public const string IndexColumns = """
        SELECT ic.object_id, ic.index_id, ic.index_column_id, ic.column_id,
               ic.key_ordinal, ic.is_descending_key, ic.is_included_column
        FROM sys.index_columns AS ic
        INNER JOIN sys.objects AS o
            ON o.object_id = ic.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V');
        """;

    // Names of PK and UQ constraints. System generated names (PK__Tbl__A1B2C3) differ
    // between environments and must stay out of the comparison.
    public const string KeyConstraints = """
        SELECT kc.parent_object_id, kc.unique_index_id, kc.name, RTRIM(kc.type), kc.is_system_named
        FROM sys.key_constraints AS kc
        INNER JOIN sys.objects AS o ON o.object_id = kc.parent_object_id AND o.is_ms_shipped = 0;
        """;

    public const string ForeignKeys = """
        SELECT fk.object_id, fk.parent_object_id, fk.name, fk.is_system_named,
               fk.referenced_object_id,
               fk.delete_referential_action, fk.update_referential_action,
               fk.is_disabled, fk.is_not_trusted, fk.is_not_for_replication
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.objects AS o ON o.object_id = fk.parent_object_id AND o.is_ms_shipped = 0;
        """;

    public const string ForeignKeyColumns = """
        SELECT fkc.constraint_object_id, fkc.constraint_column_id,
               fkc.parent_object_id, fkc.parent_column_id,
               fkc.referenced_object_id, fkc.referenced_column_id
        FROM sys.foreign_key_columns AS fkc
        INNER JOIN sys.objects AS o ON o.object_id = fkc.parent_object_id AND o.is_ms_shipped = 0;
        """;

    public const string CheckConstraints = """
        SELECT cc.parent_object_id, cc.name, cc.is_system_named,
               cc.definition, cc.is_disabled, cc.is_not_trusted, cc.is_not_for_replication
        FROM sys.check_constraints AS cc
        INNER JOIN sys.objects AS o ON o.object_id = cc.parent_object_id AND o.is_ms_shipped = 0;
        """;

    // Approximate row count read from metadata without scanning the table (milliseconds).
    // Used to warn before deployment that a table has rows and a change will be blocked.
    // Needs VIEW DATABASE STATE. Without it extraction continues without this query.
    public const string RowCounts = """
        SELECT ps.object_id, SUM(ps.row_count)
        FROM sys.dm_db_partition_stats AS ps
        INNER JOIN sys.objects AS o
            ON o.object_id = ps.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
        WHERE ps.index_id IN (0, 1)
        GROUP BY ps.object_id;
        """;

    // References between modules. Script generation needs them to create new objects in the
    // right order. Rows with a NULL referenced_id (unresolved or outside the database) do not
    // help the ordering and are left out.
    public const string Dependencies = """
        SELECT DISTINCT d.referencing_id, d.referenced_id
        FROM sys.sql_expression_dependencies AS d
        INNER JOIN sys.objects AS o
            ON o.object_id = d.referencing_id AND o.is_ms_shipped = 0
        WHERE d.referenced_id IS NOT NULL
          AND d.referenced_id <> d.referencing_id;
        """;

    // External references (cross-database or linked server). Such objects can only be deployed
    // when the external source exists on the target too, so they are reported before deployment.
    public const string ExternalReferences = """
        SELECT DISTINCT SCHEMA_NAME(o.schema_id), o.name,
               ISNULL(d.referenced_server_name, N''), ISNULL(d.referenced_database_name, N''),
               ISNULL(d.referenced_schema_name, N''), ISNULL(d.referenced_entity_name, N'')
        FROM sys.sql_expression_dependencies AS d
        INNER JOIN sys.objects AS o ON o.object_id = d.referencing_id AND o.is_ms_shipped = 0
        WHERE d.referenced_database_name IS NOT NULL OR d.referenced_server_name IS NOT NULL;
        """;

    // parent_class = 1 means DML triggers on tables and views. DDL triggers are not part of this query.
    public const string Triggers = """
        SELECT tr.object_id, tr.is_disabled, tr.is_instead_of_trigger
        FROM sys.triggers AS tr
        INNER JOIN sys.objects AS o ON o.object_id = tr.object_id AND o.is_ms_shipped = 0
        WHERE tr.parent_class = 1;
        """;

    // Storage placement, the "ON [filegroup]" or "ON [scheme]([column])" clause of CREATE TABLE and INDEX.
    // data_space covers both filegroups and partition schemes. Type 'PS' is a partition scheme.
    public const string DataSpaces = """
        SELECT data_space_id, name, type FROM sys.data_spaces;
        """;

    // Base placement of a table, meaning the data_space of the heap (index_id 0) or the clustered
    // index (index_id 1). The trailing "ON [...]" of CREATE TABLE comes from here.
    public const string TablePlacement = """
        SELECT i.object_id, i.index_id, i.data_space_id
        FROM sys.indexes AS i
        INNER JOIN sys.objects AS o
            ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
        WHERE i.index_id IN (0, 1);
        """;

    // Partitioning column of a partitioned object or index (partition_ordinal = 1). Needed for
    // "ON [scheme]([column])" placements.
    public const string PartitionColumns = """
        SELECT ic.object_id, ic.index_id, c.name
        FROM sys.index_columns AS ic
        INNER JOIN sys.columns AS c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        INNER JOIN sys.objects AS o
            ON o.object_id = ic.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V')
        WHERE ic.partition_ordinal = 1;
        """;

}
