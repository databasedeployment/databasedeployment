using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Shortcuts for building model objects in tests. Real snapshots come from the catalog,
/// these helpers let comparison and scripting logic be tested in isolation.
/// </summary>
internal static class TestFactory
{
    public static ObjectKey Table(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.Table);

    public static ObjectKey View(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.View);

    public static ObjectKey Proc(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.Procedure);

    public static ObjectKey Trigger(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.Trigger);

    public static ObjectKey Schema(string name) =>
        new(string.Empty, name, ObjectKind.Schema);

    public static ObjectSnapshot Obj(
        ObjectKey key,
        UInt128 hash,
        string? displayScript = null,
        IReadOnlyList<ColumnInfo>? columns = null,
        long? rowCount = null,
        bool incomparable = false,
        ObjectKey? parent = null,
        bool ansiNulls = true,
        bool quotedIdentifier = true,
        string? indexes = null,
        string? checks = null,
        string? foreignKeys = null)
    {
        var snapshot = new ObjectSnapshot
        {
            Key = key,
            Hash = hash,
            DisplayScript = displayScript,
            Canonical = displayScript ?? string.Empty,
            Columns = columns,
            RowCount = rowCount,
            Incomparable = incomparable,
            IncomparableReason = incomparable ? "definition is not readable" : null,
            Parent = parent,
            UsesAnsiNulls = ansiNulls,
            UsesQuotedIdentifier = quotedIdentifier,
        };

        // Canonical text of sub-parts. Risk analysis reads constraints from here.
        if (indexes is not null) snapshot.PartCanonical["indexes"] = indexes;
        if (checks is not null) snapshot.PartCanonical["checks"] = checks;
        if (foreignKeys is not null) snapshot.PartCanonical["foreignKeys"] = foreignKeys;

        return snapshot;
    }

    public static ColumnInfo Column(
        string name,
        string typeName = "int",
        short maxLength = 4,
        byte precision = 10,
        byte scale = 0,
        bool nullable = true,
        bool identity = false,
        bool computed = false,
        string? defaultDefinition = null,
        string? collation = null,
        string? defaultName = null,
        bool defaultIsSystemNamed = false) =>
        new(name, "sys", typeName, maxLength, precision, scale, nullable, collation, identity, computed,
            defaultDefinition, defaultName, defaultIsSystemNamed);
}
