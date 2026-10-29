using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>
/// Sections of the combined deployment script in the order they run. Types come before the
/// tables that use them, tables before the modules that read them, and security, permissions and
/// settings last because they refer to objects created earlier.
/// </summary>
public enum DeploymentSection
{
    Schemas = 0,
    Types = 1,
    Tables = 2,
    Modules = 3,
    Security = 4,
    Permissions = 5,
    Settings = 6,
}

public sealed record ScriptOptions
{
    /// <summary>Drop objects that exist on the target but not in the source.</summary>
    public bool IncludeDrops { get; init; } = true;

    /// <summary>Wrap everything in one transaction so a failing step leaves nothing behind.</summary>
    public bool WrapInTransaction { get; init; } = true;

    /// <summary>Create schemas that the scripted objects need and the target does not have.</summary>
    public bool CreateMissingSchemas { get; init; } = true;

    /// <summary>Only produce the CREATE statements for missing schemas and skip everything else.
    /// The combined script uses this to create schemas first, before any table.</summary>
    public bool SchemasOnly { get; init; }

    /// <summary>Timestamp written into the header. The caller passes it so generation stays deterministic.</summary>
    public string? GeneratedAt { get; init; }

    /// <summary>
    /// Table changes are handled by another section. When set, tables are not listed as out of
    /// scope, because the header of the combined script would otherwise claim they are skipped.
    /// </summary>
    public bool TablesHandledElsewhere { get; init; }

    /// <summary>
    /// Object kinds handled by other sections (roles, types and so on). They are not listed as
    /// out of scope either, so nothing is reported twice in the combined script.
    /// </summary>
    public ISet<ObjectKind>? HandledElsewhere { get; init; }

    public static readonly ScriptOptions Default = new();
}

public sealed record SkippedObject(ObjectKey Key, string Reason);

public sealed record ScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<ObjectKey> OutOfScope,
    IReadOnlyList<SkippedObject> Skipped,
    bool HadDependencyCycle)
{
    public bool IsEmpty => Included.Count == 0;
}
