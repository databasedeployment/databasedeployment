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
/// A type cannot be altered, so a changed type is reported as skipped with its reason. Removed
/// types are only dropped with <see cref="TypeScriptOptions.IncludeDrops"/>, behind an existence check.
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

    private static string DropType(ObjectKey key)
    {
        var name = Qualified(key);
        return $"IF TYPE_ID(N'{Literal(name)}') IS NOT NULL{Environment.NewLine}    DROP TYPE {name};";
    }
}
