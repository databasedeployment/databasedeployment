using System.Text;
using SchemaDiff.Core.Model;
using static SchemaDiff.Core.Scripting.SqlName;

namespace SchemaDiff.Core.Scripting;

public sealed record TypeScriptOptions
{
    /// <summary>
    /// Drop types that exist on the target but not in the source. Off by default, because DROP TYPE
    /// fails while a column, a parameter or a table type still uses the type, so it needs a deliberate choice.
    /// </summary>
    public bool IncludeDrops { get; init; }

    /// <summary>
    /// Rebuild a changed table type: drop the modules that use it, drop and create the type, then
    /// create the modules again. Off by default, because a procedure that is dropped and cannot be
    /// created again (its definition is not readable) is a damage that cannot be repaired. Even when
    /// on, nothing is written unless every dependent module is readable.
    /// </summary>
    public bool RecreateChangedTableTypes { get; init; }

    public static readonly TypeScriptOptions Default = new();
}

public sealed record TypeScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<SkippedObject> Skipped)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>
/// Produces the deployment script for user-defined alias types and table types. This section
/// runs before tables and modules, because a new column or a new procedure parameter can use a
/// new type, which has to exist first. Alias types come before table types, since a table type
/// column can use an alias type.
///
/// A type cannot be altered. A changed alias type is reported as skipped with its reason. A changed
/// table type is either reported as well or, with <see cref="TypeScriptOptions.RecreateChangedTableTypes"/>,
/// rebuilt together with the modules that use it. Removed types are only dropped with
/// <see cref="TypeScriptOptions.IncludeDrops"/>, behind an existence check.
/// </summary>
public static class TypeScriptGenerator
{
    /// <summary>
    /// Object kinds this generator handles. Other generators read the set to decide what is out of
    /// their scope, so a kind added here is never reported as skipped somewhere else.
    /// </summary>
    public static readonly IReadOnlySet<ObjectKind> HandledKinds = new HashSet<ObjectKind>
    {
        ObjectKind.UserDefinedType,
        ObjectKind.TableType,
    };

    public static TypeScriptResult Generate(CompareResult result, TypeScriptOptions? options = null)
    {
        options ??= TypeScriptOptions.Default;
        var comparer = ObjectKeyComparer.CaseInsensitive;

        var included = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();

        var creates = new List<ObjectKey>();
        var drops = new List<ObjectKey>();
        var rebuilds = new List<ObjectKey>();

        foreach (var diff in result.Differences)
        {
            if (!HandledKinds.Contains(diff.Key.Kind)) continue;

            switch (diff.Kind)
            {
                case DiffKind.Indeterminate:
                    skipped.Add(new SkippedObject(diff.Key, "could not be compared because its definition is not readable"));
                    break;

                case DiffKind.Added:
                    creates.Add(diff.Key);
                    break;

                case DiffKind.Removed when options.IncludeDrops:
                    drops.Add(diff.Key);
                    break;

                case DiffKind.Removed:
                    skipped.Add(new SkippedObject(diff.Key, "drops are turned off, the type stays on the target"));
                    break;

                case DiffKind.Changed when diff.Key.Kind == ObjectKind.TableType && options.RecreateChangedTableTypes:
                    rebuilds.Add(diff.Key);
                    break;

                case DiffKind.Changed when diff.Key.Kind == ObjectKind.TableType:
                    skipped.Add(new SkippedObject(diff.Key,
                        "changed, but a table type cannot be altered. Turn on " +
                        $"{nameof(TypeScriptOptions.RecreateChangedTableTypes)} to drop the modules that use it, " +
                        "rebuild the type and create the modules again"));
                    break;

                case DiffKind.Changed:
                    skipped.Add(new SkippedObject(diff.Key,
                        "changed, but a type cannot be altered. Drop the objects that use it, then drop and " +
                        "create the type by hand"));
                    break;
            }
        }

        var sb = new StringBuilder(4096);

        // The default seed order sorts by kind, so alias types come before table types.
        foreach (var key in DependencyOrder.Order(creates, result.Source.References, comparer, out _))
        {
            if (!result.Source.Objects.TryGetValue(key, out var snapshot)
                || string.IsNullOrWhiteSpace(snapshot.DisplayScript))
            {
                skipped.Add(new SkippedObject(key, "no CREATE text, the snapshot was taken without a display script"));
                continue;
            }

            sb.AppendLine($"IF TYPE_ID(N'{Literal(Qualified(key))}') IS NULL");
            sb.AppendLine("BEGIN");
            foreach (var line in snapshot.DisplayScript!.TrimEnd().Split('\n'))
                sb.Append("    ").AppendLine(line.TrimEnd('\r'));
            sb.AppendLine("END");
            sb.AppendLine("GO");
            sb.AppendLine();
            included.Add(key);
        }

        foreach (var key in rebuilds.OrderBy(k => k.Schema, StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (RebuildTableType(sb, key, result) is { } reason) skipped.Add(new SkippedObject(key, reason));
            else included.Add(key);
        }

        // Drops run in reverse: table types before the alias types they may use.
        var orderedDrops = DependencyOrder.Order(drops, result.Target.References, comparer, out _);
        orderedDrops.Reverse();
        foreach (var key in orderedDrops)
        {
            sb.AppendLine(DropType(key));
            sb.AppendLine("GO");
            sb.AppendLine();
            included.Add(key);
        }

        return new TypeScriptResult(sb.ToString(), included, skipped);
    }

    /// <summary>
    /// Rebuilds a changed table type: drop the dependent modules, drop and create the type, then
    /// create the modules again from their source definition. Every dependent has to be readable,
    /// otherwise nothing is written.
    /// </summary>
    /// <returns>The reason when nothing could be written, otherwise null.</returns>
    private static string? RebuildTableType(StringBuilder sb, ObjectKey key, CompareResult result)
    {
        if (!result.Source.Objects.TryGetValue(key, out var source)
            || string.IsNullOrWhiteSpace(source.DisplayScript))
            return "no CREATE text, the snapshot was taken without a display script";

        result.Target.Objects.TryGetValue(key, out var target);

        // Dependents on the target are dropped and dependents in the source are created again,
        // so every module in either list has to have a readable source definition.
        var toDrop = target?.DependentModules ?? [];
        var toCreate = source.DependentModules ?? [];

        foreach (var dependent in toDrop.Concat(toCreate).Distinct())
        {
            if (!result.Source.Objects.TryGetValue(dependent, out var module)
                || string.IsNullOrWhiteSpace(module.DisplayScript) || module.Incomparable)
            {
                return $"the dependent module {dependent} cannot be restored because its definition is not " +
                       "readable. The type was not rebuilt, apply the change by hand";
            }
        }

        foreach (var dependent in toDrop)
        {
            var name = Qualified(dependent);
            sb.AppendLine($"IF OBJECT_ID(N'{Literal(name)}') IS NOT NULL");
            sb.AppendLine($"    DROP {ModuleVerb(dependent.Kind)} {name};");
        }
        sb.AppendLine(DropType(key));
        sb.AppendLine("GO");
        sb.AppendLine();

        sb.AppendLine(source.DisplayScript!.TrimEnd());
        sb.AppendLine("GO");
        sb.AppendLine();

        foreach (var dependent in toCreate)
        {
            var module = result.Source.Objects[dependent];
            // The module keeps the SET options it was created with, otherwise its behavior changes.
            sb.AppendLine($"SET ANSI_NULLS {(module.UsesAnsiNulls == false ? "OFF" : "ON")};");
            sb.AppendLine($"SET QUOTED_IDENTIFIER {(module.UsesQuotedIdentifier == false ? "OFF" : "ON")};");
            sb.AppendLine("GO");
            sb.AppendLine(module.DisplayScript!.TrimEnd());
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return null;
    }

    private static string DropType(ObjectKey key)
    {
        var name = Qualified(key);
        return $"IF TYPE_ID(N'{Literal(name)}') IS NOT NULL{Environment.NewLine}    DROP TYPE {name};";
    }

    private static string ModuleVerb(ObjectKind kind) => kind switch
    {
        ObjectKind.Procedure => "PROCEDURE",
        ObjectKind.Trigger => "TRIGGER",
        ObjectKind.View => "VIEW",
        _ => "FUNCTION",
    };
}
