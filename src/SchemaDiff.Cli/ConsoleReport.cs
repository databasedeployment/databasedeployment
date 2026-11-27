using System.Globalization;
using SchemaDiff.Core.Jobs;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Cli;

/// <summary>Plain text output of the commands. Numbers use the invariant culture so the output is the same on every machine.</summary>
internal static class ConsoleReport
{
    public static void Header(TextWriter output, string title)
    {
        output.WriteLine();
        output.WriteLine($"=== {title} ".PadRight(80, '='));
    }

    public static void JobList(TextWriter output, IReadOnlyList<ComparisonJob> jobs)
    {
        var width = Math.Max(4, jobs.Max(j => j.Name.Length));
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            output.WriteLine($"  {i + 1,3}) {job.Name.PadRight(width)}   {job.SourceLabel}  ->  {job.TargetLabel}");
        }
    }

    public static void Summary(TextWriter output, CompareResult result)
    {
        output.WriteLine($"  Source objects : {Num(result.Source.Count)}");
        output.WriteLine($"  Target objects : {Num(result.Target.Count)}");
        output.WriteLine($"  Equal          : {Num(result.EqualCount)}");
        output.WriteLine($"  Added          : {Num(result.AddedCount)}");
        output.WriteLine($"  Removed        : {Num(result.RemovedCount)}");
        output.WriteLine($"  Changed        : {Num(result.ChangedCount)}");
        if (result.IndeterminateCount > 0)
            output.WriteLine($"  Indeterminate  : {Num(result.IndeterminateCount)}  <-- could not be compared");

        foreach (var warning in result.Source.Report.Warnings)
            output.WriteLine($"  [!] {result.Source.Database}: {warning}");
        foreach (var warning in result.Target.Report.Warnings)
            output.WriteLine($"  [!] {result.Target.Database}: {warning}");
    }

    /// <summary>Lists at most <paramref name="limit"/> differences, with the changed parts of each.</summary>
    public static void Differences(TextWriter output, CompareResult result, int limit)
    {
        if (limit <= 0 || result.Differences.Count == 0) return;

        output.WriteLine();
        foreach (var diff in result.Differences.Take(limit))
        {
            var parts = diff.ChangedParts.Count > 0 ? $"   [{string.Join(", ", diff.ChangedParts)}]" : string.Empty;
            output.WriteLine($"  {Marker(diff.Kind)} {diff.Key}{parts}");
        }

        if (result.Differences.Count > limit)
            output.WriteLine($"  ... {result.Differences.Count - limit} more (raise the limit with --details)");
    }

    /// <summary>States what the script contains and, by name, every difference it does not cover.</summary>
    public static void ScriptSummary(TextWriter output, string path, ScriptResult script)
    {
        output.WriteLine($"  Script written : {path}");
        output.WriteLine($"  Created objects: {Num(script.Included.Count)}");
        if (script.HadDependencyCycle)
            output.WriteLine("  [!] The objects reference each other in a cycle. Check the order before running the script.");

        if (script.Skipped.Count > 0)
        {
            output.WriteLine($"  Not scripted   : {Num(script.Skipped.Count)}");
            foreach (var skipped in script.Skipped)
                output.WriteLine($"      {skipped.Key}  ({skipped.Reason})");
        }

        if (script.OutOfScope.Count > 0)
        {
            output.WriteLine($"  Out of scope   : {Num(script.OutOfScope.Count)} changed or removed objects, compare them with 'schemadiff compare'");
            foreach (var key in script.OutOfScope)
                output.WriteLine($"      {key}");
        }
    }

    private static string Marker(DiffKind kind) => kind switch
    {
        DiffKind.Added => "+",
        DiffKind.Removed => "-",
        DiffKind.Changed => "~",
        DiffKind.Indeterminate => "?",
        _ => " ",
    };

    private static string Num(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
