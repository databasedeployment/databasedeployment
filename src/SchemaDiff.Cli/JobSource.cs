using SchemaDiff.Core.Jobs;

namespace SchemaDiff.Cli;

/// <summary>Where the job list comes from and which of the jobs run.</summary>
internal static class JobSource
{
    /// <summary>
    /// Jobs from jobs.json, or from <c>--source</c> and <c>--target</c>. With <c>--databases</c> the
    /// same database names are compared on both servers, one job per name.
    /// </summary>
    public static async Task<IReadOnlyList<ComparisonJob>> LoadAsync(CliArguments arguments, CancellationToken ct = default)
    {
        if (arguments.Config is not null) return await JobConfiguration.LoadAsync(arguments.Config, ct);

        var source = arguments.Source!;
        var target = arguments.Target!;

        if (arguments.Databases.Count > 0)
            return [.. arguments.Databases.Select(db => ComparisonJob.ForDatabase(source, target, db))];

        // A single pair is named after its target, which is what the user recognizes in the output.
        var single = new ComparisonJob("compare", source, target);
        return [single with { Name = single.TargetLabel }];
    }

    /// <summary>
    /// Decides which jobs run. <c>--all</c> and <c>--jobs</c> win. A single job runs without asking.
    /// Otherwise the user is asked when there is a terminal, and the command fails when the input is
    /// redirected, because a script must never hang on a prompt.
    /// </summary>
    /// <returns>The selected jobs, an empty list when the user cancelled, or null after an error.</returns>
    public static IReadOnlyList<ComparisonJob>? Select(
        IReadOnlyList<ComparisonJob> jobs, CliArguments arguments, CliConsole console)
    {
        try
        {
            if (arguments.All) return jobs;
            if (arguments.Selection is not null) return JobSelector.Select(jobs, arguments.Selection);
        }
        catch (ArgumentException ex)
        {
            console.Error.WriteLine($"Error: {ex.Message}");
            return null;
        }

        if (jobs.Count == 1) return jobs;

        if (arguments.Interactive || !console.InputRedirected) return Prompt(jobs, console);

        console.Error.WriteLine("Error: it is not clear which jobs should run.");
        console.Error.WriteLine("       Use --select to choose interactively, --all for every job,");
        console.Error.WriteLine("       or --jobs \"1,3\" and --jobs \"NAME1,NAME2\" from a script.");
        console.Error.WriteLine("       Run 'schemadiff list' to see the defined jobs.");
        return null;
    }

    private static IReadOnlyList<ComparisonJob>? Prompt(IReadOnlyList<ComparisonJob> jobs, CliConsole console)
    {
        console.Out.WriteLine("Choose the jobs to compare:");
        console.Out.WriteLine();
        ConsoleReport.JobList(console.Out, jobs);
        console.Out.WriteLine();
        console.Out.Write("Selection (1,3 | 2-5 | name | a = all | empty = cancel): ");

        var input = console.In.ReadLine();
        try
        {
            var selection = JobSelector.Select(jobs, input);
            if (selection.Count > 0)
                console.Out.WriteLine($"Selected: {string.Join(", ", selection.Select(j => j.Name))}");
            return selection;
        }
        catch (ArgumentException ex)
        {
            console.Error.WriteLine($"Error: {ex.Message}");
            return null;
        }
    }
}
