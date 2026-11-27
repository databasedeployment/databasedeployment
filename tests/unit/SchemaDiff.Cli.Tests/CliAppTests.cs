using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Jobs;
using SchemaDiff.Core.Model;
using static SchemaDiff.Cli.Tests.Fixtures;

namespace SchemaDiff.Cli.Tests;

/// <summary>Runs whole commands against a fake comparison, so the wiring is tested without a server.</summary>
public sealed class CliAppTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"schemadiff-cli-{Guid.NewGuid():N}");

    public CliAppTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private readonly List<(ComparisonJob Job, SnapshotOptions Options, int MaxQueries)> _calls = [];

    private Task<CompareResult> Fake(ComparisonJob job, SnapshotOptions options, int maxQueries, CancellationToken ct)
    {
        _calls.Add((job, options, maxQueries));
        if (job.Name == "Broken") throw new InvalidOperationException("Login failed for user 'x'.\nsecond line");
        return Task.FromResult(SalesComparison());
    }

    private async Task<(int Code, string Out, string Error)> Run(params string[] args)
    {
        var (console, output, error) = Fixtures.Console();
        var code = await new CliApp(console, Fake).RunAsync(args);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Help_prints_the_usage_and_completes()
    {
        var (code, output, _) = await Run("help");

        Assert.Equal(CliApp.Completed, code);
        Assert.Contains("usage: schemadiff <command>", output);
    }

    [Fact]
    public async Task Invalid_arguments_fail_with_a_hint()
    {
        var (code, _, error) = await Run("compare", "--wat");

        Assert.Equal(CliApp.Failed, code);
        Assert.Contains("Unknown option '--wat'", error);
        Assert.Contains("schemadiff help", error);
    }

    [Fact]
    public async Task Compare_prints_the_summary_and_the_differences()
    {
        var (code, output, _) = await Run("compare", "-s", Dev, "-t", Prod, "--case-sensitive", "--max-queries", "3");

        Assert.Equal(CliApp.Completed, code);
        Assert.Contains("Equal          : 1", output);
        Assert.Contains("Added          : 2", output);
        Assert.Contains("+ Table [dbo].[Invoice]", output);
        Assert.Contains("- Procedure [dbo].[LegacyReport]", output);
        Assert.True(_calls.Single().Options.CaseSensitiveNames);
        Assert.Equal(3, _calls.Single().MaxQueries);
    }

    [Fact]
    public async Task Details_limit_the_listed_differences()
    {
        var (_, output, _) = await Run("compare", "-s", Dev, "-t", Prod, "-d", "1");

        Assert.Contains("... 3 more", output);
        Assert.DoesNotContain("LegacyReport", output);
    }

    [Fact]
    public async Task A_failed_job_is_reported_and_the_others_still_run()
    {
        var config = Path.Combine(_directory, "jobs.json");
        await File.WriteAllTextAsync(config, $$"""
            {
              "defaults": { "source": "{{Dev}}", "target": "{{Prod}}" },
              "jobs": [ { "name": "Broken", "database": "X" }, { "database": "Sales" } ]
            }
            """);

        var (code, output, error) = await Run("compare", "-c", config, "--all");

        Assert.Equal(CliApp.Failed, code);
        Assert.Equal(2, _calls.Count);
        Assert.Contains("Error in job Broken: Login failed for user 'x'.", error);
        Assert.DoesNotContain("second line", error);
        Assert.Contains("Added          : 2", output);
    }

    [Fact]
    public async Task Script_writes_the_file_and_names_what_it_does_not_cover()
    {
        var path = Path.Combine(_directory, "deploy.sql");

        var (code, output, _) = await Run("script", "-s", Dev, "-t", Prod, "--out", path);

        Assert.Equal(CliApp.Completed, code);
        Assert.True(_calls.Single().Options.KeepDisplayScripts);
        var sql = await File.ReadAllTextAsync(path);
        Assert.Contains("CREATE TABLE [dbo].[Invoice]", sql);
        Assert.Contains("Created objects: 2", output);
        Assert.Contains("Out of scope   : 2", output);
        Assert.Contains("Procedure [dbo].[LegacyReport]", output);
    }

    [Fact]
    public async Task Script_with_several_jobs_writes_one_file_per_job()
    {
        var path = Path.Combine(_directory, "deploy.sql");

        var (code, _, _) = await Run("script", "-s", Dev, "-t", Prod, "--databases", "Sales,Billing", "--all", "-o", path);

        Assert.Equal(CliApp.Completed, code);
        Assert.True(File.Exists(Path.Combine(_directory, "deploy.Sales.sql")));
        Assert.True(File.Exists(Path.Combine(_directory, "deploy.Billing.sql")));
    }

    [Theory]
    [InlineData("deploy.sql", 1, "deploy.sql")]
    [InlineData("deploy.sql", 2, "deploy.Sales.sql")]
    [InlineData("out/deploy.sql", 2, "out/deploy.Sales.sql")]
    public void Script_path_gets_the_job_name_only_for_several_jobs(string path, int jobs, string expected)
    {
        var job = ComparisonJob.ForDatabase(Dev, Prod, "Sales");

        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar),
            CliApp.ScriptPath(path.Replace('/', Path.DirectorySeparatorChar), job, jobs));
    }

    [Fact]
    public void Job_names_that_are_not_valid_file_names_are_made_safe()
    {
        var job = new ComparisonJob("Sales/EU:1", Dev, Prod);

        Assert.Equal("deploy.Sales_EU_1.sql", CliApp.ScriptPath("deploy.sql", job, 2));
    }

    [Fact]
    public async Task Init_writes_a_configuration_that_list_can_read()
    {
        var config = Path.Combine(_directory, "jobs.json");

        var (initCode, initOut, _) = await Run("init", config);
        var (listCode, listOut, _) = await Run("list", "-c", config);

        Assert.Equal(CliApp.Completed, initCode);
        Assert.Contains("Starter configuration written", initOut);
        Assert.Equal(CliApp.Completed, listCode);
        Assert.Contains("4 defined jobs", listOut);
        Assert.Contains("different-names-example", listOut);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_broken_configuration_fails_with_its_message()
    {
        var config = Path.Combine(_directory, "jobs.json");
        await File.WriteAllTextAsync(config, """{ "jobs": [ { "database": "Sales" } ] }""");

        var (code, _, error) = await Run("list", "-c", config);

        Assert.Equal(CliApp.Failed, code);
        Assert.Contains("jobs[0]: no 'source' given", error);
    }

    [Fact]
    public async Task Nothing_selected_is_not_an_error()
    {
        var (console, output, _) = Fixtures.Console(input: "\n", inputRedirected: false);

        var code = await new CliApp(console, Fake).RunAsync(["compare", "-s", Dev, "-t", Prod, "--databases", "A,B"]);

        Assert.Equal(CliApp.Completed, code);
        Assert.Contains("Nothing selected", output.ToString());
        Assert.Empty(_calls);
    }
}
