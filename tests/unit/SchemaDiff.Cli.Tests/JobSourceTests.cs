using SchemaDiff.Core.Jobs;
using static SchemaDiff.Cli.Tests.Fixtures;

namespace SchemaDiff.Cli.Tests;

public class JobSourceTests
{
    private static CliArguments Args(params string[] args) => CliArguments.Parse(args).Arguments!;

    private static readonly IReadOnlyList<ComparisonJob> ThreeJobs =
    [
        ComparisonJob.ForDatabase(Dev, Prod, "Sales"),
        ComparisonJob.ForDatabase(Dev, Prod, "Billing"),
        ComparisonJob.ForDatabase(Dev, Prod, "Hr"),
    ];

    [Fact]
    public async Task A_single_pair_is_named_after_its_target()
    {
        var jobs = await JobSource.LoadAsync(Args("compare", "-s", Dev, "-t", Prod));

        var job = Assert.Single(jobs);
        Assert.Equal("prod01.Sales", job.Name);
    }

    [Fact]
    public async Task Databases_give_one_job_per_name_on_both_servers()
    {
        var jobs = await JobSource.LoadAsync(Args("compare", "-s", Dev, "-t", Prod, "--databases", "Sales,Billing"));

        Assert.Equal(["Sales", "Billing"], jobs.Select(j => j.Name));
        Assert.Equal("dev01.Billing", jobs[1].SourceLabel);
        Assert.Equal("prod01.Billing", jobs[1].TargetLabel);
    }

    [Fact]
    public async Task A_missing_config_file_is_reported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        await Assert.ThrowsAsync<FileNotFoundException>(() => JobSource.LoadAsync(Args("list", "-c", path)));
    }

    [Fact]
    public void All_and_an_expression_select_without_asking()
    {
        var (console, _, _) = Fixtures.Console();

        Assert.Equal(3, JobSource.Select(ThreeJobs, Args("compare", "-c", "j", "--all"), console)!.Count);
        Assert.Equal(["Sales", "Hr"], JobSource.Select(ThreeJobs, Args("compare", "-c", "j", "-j", "1,Hr"), console)!.Select(j => j.Name));
    }

    [Fact]
    public void An_invalid_expression_is_an_error()
    {
        var (console, _, error) = Fixtures.Console();

        Assert.Null(JobSource.Select(ThreeJobs, Args("compare", "-c", "j", "-j", "7"), console));
        Assert.Contains("positions go from 1 to 3", error.ToString());
    }

    [Fact]
    public void A_single_job_runs_without_a_selection()
    {
        var (console, _, _) = Fixtures.Console();

        Assert.Single(JobSource.Select([ThreeJobs[0]], Args("compare", "-c", "j"), console)!);
    }

    [Fact]
    public void Redirected_input_without_a_selection_fails_instead_of_waiting()
    {
        var (console, output, error) = Fixtures.Console(input: "1\n", inputRedirected: true);

        Assert.Null(JobSource.Select(ThreeJobs, Args("compare", "-c", "j"), console));
        Assert.Contains("--all", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void A_terminal_asks_which_jobs_to_run()
    {
        var (console, output, _) = Fixtures.Console(input: "2-3\n", inputRedirected: false);

        var selected = JobSource.Select(ThreeJobs, Args("compare", "-c", "j"), console);

        Assert.Equal(["Billing", "Hr"], selected!.Select(j => j.Name));
        Assert.Contains("Choose the jobs", output.ToString());
        Assert.Contains("Selected: Billing, Hr", output.ToString());
    }

    [Fact]
    public void Select_asks_even_with_redirected_input()
    {
        var (console, _, _) = Fixtures.Console(input: "Sales\n", inputRedirected: true);

        var selected = JobSource.Select(ThreeJobs, Args("compare", "-c", "j", "--select"), console);

        Assert.Equal("Sales", Assert.Single(selected!).Name);
    }

    [Fact]
    public void An_empty_answer_cancels()
    {
        var (console, _, _) = Fixtures.Console(input: "\n", inputRedirected: false);

        Assert.Empty(JobSource.Select(ThreeJobs, Args("compare", "-c", "j"), console)!);
    }
}
