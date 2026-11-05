namespace SchemaDiff.Core.Model;

/// <summary>
/// A single schema object in a form that is ready to compare. The hash answers the
/// question "did it change?" without looking at any text. Canonical is only used to
/// build a detailed diff for objects whose hashes differ.
/// </summary>
public sealed class ObjectSnapshot
{
    public required ObjectKey Key { get; init; }
    public required UInt128 Hash { get; set; }
    public DateTime ModifyDate { get; init; }

    /// <summary>Normalized canonical text of the object. The detailed diff is produced from this.</summary>
    public string Canonical { get; set; } = string.Empty;

    /// <summary>
    /// Hashes of sub-parts ("columns", "indexes", "constraints", "foreignKeys", "body").
    /// They tell which part of a table changed without computing a text diff.
    /// </summary>
    public Dictionary<string, UInt128> Parts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> PartCanonical { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The definition could not be read (encrypted object or missing VIEW DEFINITION).
    /// Such objects are never reported as equal. A silent "equal" is the most dangerous result.
    /// </summary>
    public bool Incomparable { get; set; }

    public string? IncomparableReason { get; set; }

    /// <summary>Structural column information for tables. Data loss analysis is based on it.</summary>
    public IReadOnlyList<ColumnInfo>? Columns { get; set; }

    /// <summary>
    /// Approximate row count of the table, read from sys.dm_db_partition_stats without a scan.
    /// It answers "will this change be blocked during deployment?" before the deployment runs.
    /// </summary>
    public long? RowCount { get; set; }

    /// <summary>Enabled or disabled state for triggers.</summary>
    public bool? IsDisabled { get; set; }

    /// <summary>
    /// Full text of the object for display. For modules this is the original body before
    /// normalization, for tables a CREATE script generated from the catalog. Only filled
    /// when the snapshot options ask to keep display scripts.
    ///
    /// Canonical text is enough for comparison but not for reading. It collapses modules to
    /// a single line and is just a field list for tables. Showing a change side by side
    /// needs the full text.
    /// </summary>
    public string? DisplayScript { get; set; }

    /// <summary>SET options the module was created with. The deployment script must use the same ones.</summary>
    public bool? UsesAnsiNulls { get; set; }

    public bool? UsesQuotedIdentifier { get; set; }

    /// <summary>Table or view a trigger belongs to. Used to check prerequisites during scripting.</summary>
    public ObjectKey? Parent { get; set; }

    /// <summary>Indexes of the table, including PK and UQ constraints, kept for script generation.</summary>
    public IReadOnlyList<IndexDefinition>? IndexDefinitions { get; set; }

    /// <summary>Storage placement of the table, either "[filegroup]" or "[scheme]([column])".
    /// The trailing "ON [...]" clause of CREATE TABLE is built from it.</summary>
    public string? Placement { get; set; }

    public IReadOnlyList<CheckDefinition>? CheckDefinitions { get; set; }

    public IReadOnlyList<ForeignKeyDefinition>? ForeignKeyDefinitions { get; set; }

    /// <summary>User statistics (CREATE STATISTICS), kept for script generation.</summary>
    public IReadOnlyList<StatisticsDefinition>? StatisticsDefinitions { get; set; }

    /// <summary>Full-text index of the table (at most one), kept for script generation.</summary>
    public FullTextIndexDefinition? FullTextIndex { get; set; }

    /// <summary>
    /// Modules that use this table type as a parameter. A table type cannot be altered,
    /// so these modules are dropped before the type is rebuilt and created again afterwards.
    /// </summary>
    public IReadOnlyList<ObjectKey>? DependentModules { get; set; }

    /// <summary>Words of a full-text stoplist, used to generate word level ALTER statements.</summary>
    public IReadOnlyList<StopwordDefinition>? Stopwords { get; set; }

    /// <summary>XML and spatial indexes. Their syntax is completely different from regular indexes.</summary>
    public IReadOnlyList<XmlIndexDefinition>? XmlIndexes { get; set; }

    public IReadOnlyList<SpatialIndexDefinition>? SpatialIndexes { get; set; }
}

/// <summary>
/// User-defined statistics. The sampling rate (FULLSCAN or SAMPLE n PERCENT) is not stored in
/// the catalog. It only shows up in DMVs that describe the current data, so it is not a schema
/// difference and is not scripted either. SSDT behaves the same way.
/// </summary>
public sealed record StatisticsDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    string? FilterDefinition,
    bool NoRecompute,
    bool IsIncremental);

/// <summary>Structural definition of an index for script generation.</summary>
public sealed record IndexDefinition(
    string Name,
    bool IsPrimaryKey,
    bool IsUniqueConstraint,
    bool IsUnique,
    string TypeDesc,
    bool IsSystemNamed,
    IReadOnlyList<IndexKeyColumn> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    string? FilterDefinition,
    string? DataCompression = null,
    bool AllowRowLocks = true,
    bool AllowPageLocks = true,
    bool OptimizeForSequentialKey = false,
    bool StatisticsNoRecompute = false,
    string? Placement = null)
{
    /// <summary>PK and UNIQUE constraints are written with ALTER TABLE ADD CONSTRAINT, everything else with CREATE INDEX.</summary>
    public bool IsConstraint => IsPrimaryKey || IsUniqueConstraint;

    /// <summary>Compression that has to be written explicitly (ROW, PAGE or COLUMNSTORE_ARCHIVE), otherwise null.</summary>
    public string? ExplicitCompression =>
        DataCompression is "ROW" or "PAGE" or "COLUMNSTORE_ARCHIVE" ? DataCompression : null;
}

public sealed record IndexKeyColumn(string Column, bool Descending);

/// <summary>A stoplist word. The language (LCID) is part of the word, so the same word
/// in two languages is stored as two entries.</summary>
public sealed record StopwordDefinition(string Word, int LanguageId);

/// <summary>
/// XML index. A primary index is written as <c>CREATE PRIMARY XML INDEX</c>, a secondary one as
/// <c>CREATE XML INDEX … USING XML INDEX [primary] FOR PATH|VALUE|PROPERTY</c>.
/// Columns have no ASC or DESC, so the regular index syntax would produce invalid SQL here.
/// </summary>
public sealed record XmlIndexDefinition(
    string Name, string Column, bool IsPrimary, string? SecondaryType, string? PrimaryIndexName);

/// <summary>
/// Spatial index. GEOMETRY_GRID needs <c>BOUNDING_BOX</c> while GEOGRAPHY_GRID does not.
/// The AUTO_GRID variants do not take <c>GRIDS</c> because SQL Server chooses them itself.
/// </summary>
public sealed record SpatialIndexDefinition(
    string Name, string Column, string Tessellation,
    double? BoundingXMin, double? BoundingYMin, double? BoundingXMax, double? BoundingYMax,
    string? Level1, string? Level2, string? Level3, string? Level4, int? CellsPerObject)
{
    public bool IsAutoGrid => Tessellation.Contains("AUTO_GRID", StringComparison.OrdinalIgnoreCase);

    public bool HasBoundingBox =>
        BoundingXMin is not null && BoundingYMin is not null &&
        BoundingXMax is not null && BoundingYMax is not null &&
        Tessellation.StartsWith("GEOMETRY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Full-text index of a table. A table has at most one, so it is part of the table and not a
/// separate object. KEY INDEX is required and must be a unique, single column, NOT NULL index.
/// </summary>
public sealed record FullTextIndexDefinition(
    string KeyIndexName,
    string CatalogName,
    IReadOnlyList<FullTextIndexColumn> Columns,
    string ChangeTracking,
    string Stoplist,
    bool IsEnabled);

/// <param name="TypeColumn">Column that holds the file extension of a binary column (TYPE COLUMN), otherwise null.</param>
/// <param name="LanguageId">Language LCID. Zero means the server default and is not written.</param>
public sealed record FullTextIndexColumn(string Column, string? TypeColumn, int LanguageId);

public sealed record CheckDefinition(
    string Name, string Definition, bool IsSystemNamed, bool NotForReplication = false,
    bool IsDisabled = false, bool IsNotTrusted = false);

public sealed record ForeignKeyDefinition(
    string Name,
    bool IsSystemNamed,
    string ReferencedSchema,
    string ReferencedName,
    IReadOnlyList<ForeignKeyColumnPair> Columns,
    byte DeleteAction,
    byte UpdateAction,
    bool NotForReplication = false,
    bool IsDisabled = false,
    bool IsNotTrusted = false);

public sealed record ForeignKeyColumnPair(string Parent, string Referenced);

/// <summary>Column properties needed by the risk analysis.</summary>
public sealed record ColumnInfo(
    string Name,
    string TypeSchema,
    string TypeName,
    short MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable,
    string? Collation,
    bool IsIdentity,
    bool IsComputed,
    string? DefaultDefinition,
    string? DefaultName = null,
    bool DefaultIsSystemNamed = false,
    bool IsSparse = false,
    bool IsFileStream = false,
    bool IsRowGuidCol = false,
    bool IsColumnSet = false,
    string? XmlCollection = null,
    bool IsXmlDocument = false,
    bool IdentityNotForReplication = false,
    string? IdentitySeed = null,
    string? IdentityIncrement = null)
{
    /// <summary>Type suffix of a typed XML column, <c>(CONTENT [schema].[collection])</c>, or null when untyped.</summary>
    public string? XmlTypeSuffix =>
        XmlCollection is null ? null : $"({(IsXmlDocument ? "DOCUMENT" : "CONTENT")} {XmlCollection})";

    public string TypeDisplay => XmlCollection is not null ? $"xml {XmlTypeSuffix}" : MaxLength switch
    {
        -1 => $"{TypeName}(max)",
        _ when TypeName is "decimal" or "numeric" => $"{TypeName}({Precision},{Scale})",
        _ when TypeName is "nvarchar" or "nchar" => $"{TypeName}({MaxLength / 2})",
        _ when TypeName is "varchar" or "char" or "varbinary" or "binary" => $"{TypeName}({MaxLength})",
        _ => TypeName,
    };
}
