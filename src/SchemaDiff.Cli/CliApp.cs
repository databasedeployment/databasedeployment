using System.Text;
using SchemaDiff.Core;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Jobs;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Cli;

/// <summary>Compares one job. The real one connects to SQL Server, tests pass a fake.</summary>
internal delegate Task<CompareResult> CompareJob(
    ComparisonJob job, SnapshotOptions options, int maxQueries, CancellationToken ct);

/// <summary>Runs one command of the command line tool.</summary>
internal sealed class CliApp(CliConsole console, CompareJob? compare = null)
{
    public const int Completed = 0;
    public const int Failed = 2;

    private readonly CompareJob _compare = compare ?? CompareWithServerAsync;

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var (arguments, error) = CliArguments.Parse(args);
        if (arguments is null)
        {
            console.Error.WriteLine($"Error: {error}");
            console.Error.WriteLine("Run 'schemadiff help' to see the commands and their options.");
            return Failed;
        }

        try
        {
            return arguments.Command switch
            {
                CliCommand.Init => await InitAsync(arguments.OutputPath!, ct),
                CliCommand.List => await ListAsync(arguments, ct),
                CliCommand.Compare or CliCommand.Script => await RunJobsAsync(arguments, ct),
                _ => Help(arguments.HelpTopic),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or System.Text.Json.JsonException)
        {
            console.Error.WriteLine($"Error: {ex.Message}");
            if (arguments.Verbose) console.Error.WriteLine(ex);
            return Failed;
        }
    }

    private int Help(CliCommand? topic)
    {
        console.Out.WriteLine(HelpText.For(topic));
        return Completed;
    }

    private async Task<int> InitAsync(string path, CancellationToken ct)
    {
        await JobConfiguration.WriteTemplateAsync(path, ct);
        console.Out.WriteLine($"Starter configuration written: {path}");
        console.Out.WriteLine("Edit the connection strings, then use it with --config.");
        return Completed;
    }

    private async Task<int> ListAsync(CliArguments arguments, CancellationToken ct)
    {
        var jobs = await JobSource.LoadAsync(arguments, ct);
        console.Out.WriteLine($"{jobs.Count} defined jobs:");
        console.Out.WriteLine();
        ConsoleReport.JobList(console.Out, jobs);
        return Completed;
    }

    private async Task<int> RunJobsAsync(CliArguments arguments, CancellationToken ct)
    {
        var jobs = await JobSource.LoadAsync(arguments, ct);
        var selected = JobSource.Select(jobs, arguments, console);
        if (selected is null) return Failed;
        if (selected.Count == 0)
        {
            console.Out.WriteLine("Nothing selected, nothing to do.");
            return Completed;
        }

        var options = arguments.ToSnapshotOptions();
        var failed = 0;

        // Jobs run one after the other here, each one reported as soon as it is done.
        foreach (var job in selected)
        {
            ConsoleReport.Header(console.Out, $"{job.Name}   {job.SourceLabel} -> {job.TargetLabel}");

            CompareResult result;
            try
            {
                result = await _compare(job, options, arguments.MaxQueries, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                console.Error.WriteLine($"Error in job {job.Name}: {ex.Message.Split('\n')[0].Trim()}");
                if (arguments.Verbose) console.Error.WriteLine(ex);
                continue;
            }

            ConsoleReport.Summary(console.Out, result);

            if (arguments.Command == CliCommand.Script)
                await WriteScriptAsync(result, ScriptPath(arguments.OutputPath!, job, selected.Count), ct);
            else
                ConsoleReport.Differences(console.Out, result, arguments.Details);
        }

        return failed > 0 ? Failed : Completed;
    }

    private async Task WriteScriptAsync(CompareResult result, string path, CancellationToken ct)
    {
        var script = MissingObjectScript.Build(result);
        await File.WriteAllTextAsync(path, script.Sql, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
        console.Out.WriteLine();
        ConsoleReport.ScriptSummary(console.Out, path, script);
    }

    /// <summary>One job writes to the given path. With several jobs the job name goes before the extension.</summary>
    internal static string ScriptPath(string path, ComparisonJob job, int jobCount)
    {
        if (jobCount == 1) return path;

        var directory = Path.GetDirectoryName(path);
        var file = $"{Path.GetFileNameWithoutExtension(path)}.{SafeFileName(job.Name)}{Path.GetExtension(path)}";
        return string.IsNullOrEmpty(directory) ? file : Path.Combine(directory, file);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':']).ToHashSet();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static async Task<CompareResult> CompareWithServerAsync(
        ComparisonJob job, SnapshotOptions options, int maxQueries, CancellationToken ct)
    {
        using var gate = new ExtractionGate(maxQueries);
        return await SchemaDiffService.CompareAsync(
            job.SourceConnectionString, job.TargetConnectionString, options, gate, ct);
    }
}
