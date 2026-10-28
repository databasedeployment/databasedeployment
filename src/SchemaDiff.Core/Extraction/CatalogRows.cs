using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SchemaDiff.Core.Extraction;

// Raw rows read from the catalog views. They are deliberately kept dumb.
// Interpretation and canonicalization are the job of SnapshotBuilder.

internal sealed record SchemaRow(int SchemaId, string Name);

internal sealed record ObjectRow(
    int ObjectId, string SchemaName, string Name, string Type,
    DateTime ModifyDate, int ParentObjectId);

internal sealed record ModuleRow(
    int ObjectId, string? Definition, bool UsesAnsiNulls, bool UsesQuotedIdentifier);

internal sealed record ColumnRow(
    int ObjectId, int ColumnId, string Name,
    string TypeSchema, string TypeName,
    short MaxLength, byte Precision, byte Scale,
    bool IsNullable, string? Collation, bool IsIdentity, bool IsComputed,
    string? DefaultName, string? DefaultDefinition, bool? DefaultIsSystemNamed,
    string? ComputedDefinition, bool? ComputedIsPersisted,
    string? IdentitySeed, string? IdentityIncrement,
    byte GeneratedAlwaysType = 0, bool IsHidden = false,
    bool IsSparse = false, bool IsFileStream = false, bool IsRowGuidCol = false, bool IsColumnSet = false,
    int XmlCollectionId = 0, bool IsXmlDocument = false, bool IdentityNotForReplication = false);

/// <summary>Schema collection referenced by typed XML columns, used to resolve the id to a name.</summary>
internal sealed record XmlSchemaCollectionRow(int CollectionId, string SchemaName, string Name);

internal sealed record IndexRow(
    int ObjectId, int IndexId, string? Name, string TypeDesc,
    bool IsUnique, bool IsPrimaryKey, bool IsUniqueConstraint,
    byte FillFactor, bool IsPadded, bool IgnoreDupKey, string? FilterDefinition,
    string? DataCompression = null,
    bool AllowRowLocks = true, bool AllowPageLocks = true,
    int DataSpaceId = 0);

/// <summary>Filegroup or partition scheme (type='PS'), the "ON [...]" placement of a CREATE.</summary>
internal sealed record DataSpaceRow(int Id, string Name, string Type);

/// <summary>Base placement of a table, the data_space of its heap (0) or clustered (1) index.</summary>
internal sealed record TablePlacementRow(int ObjectId, int IndexId, int DataSpaceId);

/// <summary>Partitioning column of a partitioned object or index (partition_ordinal = 1).</summary>
internal sealed record PartitionColumnRow(int ObjectId, int IndexId, string Column);

/// <summary>Version dependent extra index options, read by a separate optional query.</summary>
internal sealed record IndexExtraRow(
    int ObjectId, int IndexId, bool OptimizeForSequentialKey, bool StatisticsNoRecompute);

internal sealed record IndexColumnRow(
    int ObjectId, int IndexId, int IndexColumnId, int ColumnId,
    byte KeyOrdinal, bool IsDescending, bool IsIncluded);

internal sealed record KeyConstraintRow(
    int ParentObjectId, int? UniqueIndexId, string Name, string Type, bool IsSystemNamed);

internal sealed record ForeignKeyRow(
    int ObjectId, int ParentObjectId, string Name, bool IsSystemNamed, int ReferencedObjectId,
    byte DeleteAction, byte UpdateAction, bool IsDisabled, bool IsNotTrusted,
    bool IsNotForReplication = false);

internal sealed record ForeignKeyColumnRow(
    int ConstraintObjectId, int ConstraintColumnId,
    int ParentObjectId, int ParentColumnId,
    int ReferencedObjectId, int ReferencedColumnId);

internal sealed record CheckConstraintRow(
    int ParentObjectId, string Name, bool IsSystemNamed,
    string? Definition, bool IsDisabled, bool IsNotTrusted, bool IsNotForReplication = false);

internal sealed record TriggerRow(int ObjectId, bool IsDisabled, bool IsInsteadOf);

internal sealed record RowCountRow(int ObjectId, long Rows);

/// <summary>System-versioned temporal table with its history table and PERIOD columns.</summary>
internal sealed record TemporalRow(
    int ObjectId, string? HistorySchema, string? HistoryName, string? StartColumn, string? EndColumn);

internal sealed record DependencyRow(int ReferencingId, int ReferencedId);

/// <summary>External (cross-database or linked server) reference from a source object to server, database, schema and object.</summary>
internal sealed record ExternalReferenceRow(
    string SourceSchema, string SourceName, string Server, string Database, string RefSchema, string RefEntity);

/// <summary>SqlDataReader helpers. Readers use SequentialAccess, so every mapper has to
/// read the ordinals in increasing order.</summary>
internal static class Rdr
{
    public static string Str(SqlDataReader r, int i) => r.GetString(i);
    public static string? NStr(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static bool Bool(SqlDataReader r, int i) => r.GetBoolean(i);
    public static bool? NBool(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);
    public static int Int(SqlDataReader r, int i) => r.GetInt32(i);
    public static int? NInt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static short Short(SqlDataReader r, int i) => r.GetInt16(i);
    public static long Long(SqlDataReader r, int i) => r.GetInt64(i);
    public static byte Byte(SqlDataReader r, int i) => r.IsDBNull(i) ? (byte)0 : r.GetByte(i);
    public static DateTime Date(SqlDataReader r, int i) => r.GetDateTime(i);
    public static double? NDbl(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    /// <summary>For sql_variant columns such as identity seed and increment.</summary>
    public static string? Variant(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
}
