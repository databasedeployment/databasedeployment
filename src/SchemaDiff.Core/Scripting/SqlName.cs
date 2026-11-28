using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>Quoting of names and literals in generated SQL, shared by the script generators.</summary>
internal static class SqlName
{
    /// <summary>Bracketed identifier. A closing bracket inside the name is doubled.</summary>
    public static string Id(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>Two part name, <c>[schema].[name]</c>.</summary>
    public static string Qualified(string schema, string name) => $"{Id(schema)}.{Id(name)}";

    public static string Qualified(ObjectKey key) => Qualified(key.Schema, key.Name);

    /// <summary>Text for an N'...' literal, with single quotes doubled.</summary>
    public static string Literal(string value) => value.Replace("'", "''");
}
