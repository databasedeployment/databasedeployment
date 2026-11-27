using System.Text;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Cli;

/// <summary>
/// Script of the <c>script</c> command: creates the objects that exist in the source but not in the
/// target, from their source definitions and in dependency order. Changed and removed objects need
/// ALTER and DROP statements, which this script does not write. They are returned as out of scope so
/// the command can name every one of them instead of dropping them silently.
/// </summary>
internal static class MissingObjectScript
{
    public static ScriptResult Build(CompareResult result)
    {
        var added = result.Differences.Where(d => d.Kind == DiffKind.Added).Select(d => d.Key).ToList();
        var outOfScope = result.Differences
            .Where(d => d.Kind is DiffKind.Changed or DiffKind.Removed)
            .Select(d => d.Key)
            .ToList();
        var skipped = result.Differences
            .Where(d => d.Kind == DiffKind.Indeterminate)
            .Select(d => new SkippedObject(d.Key, "could not be compared, " + string.Join(", ", d.ChangedParts)))
            .ToList();

        var ordered = DependencyOrder.Order(
            added, result.Source.References, ObjectKeyComparer.CaseInsensitive, out var hadCycle);

        var body = new StringBuilder();
        var included = new List<ObjectKey>(ordered.Count);

        foreach (var key in ordered)
        {
            var source = result.Source.Objects[key];
            if (string.IsNullOrWhiteSpace(source.DisplayScript))
            {
                skipped.Add(new SkippedObject(key, "the source definition is not available"));
                continue;
            }

            // A module keeps the SET options it was created with, otherwise its behavior changes.
            if (key.Kind.IsModule() && source.UsesAnsiNulls is { } ansiNulls && source.UsesQuotedIdentifier is { } quoted)
            {
                body.AppendLine($"SET ANSI_NULLS {(ansiNulls ? "ON" : "OFF")};");
                body.AppendLine($"SET QUOTED_IDENTIFIER {(quoted ? "ON" : "OFF")};");
                body.AppendLine("GO");
            }

            body.AppendLine(source.DisplayScript.Trim());
            body.AppendLine("GO");
            body.AppendLine();
            included.Add(key);
        }

        var sql = included.Count == 0
            ? string.Empty
            : DeploymentHeader.Master(result, generatedAt: null, allowDataLoss: false, result.Target.Database) + body;

        return new ScriptResult(sql, included, outOfScope, skipped, hadCycle);
    }
}
