namespace SchemaDiff.Cli.Tests;

public class CliArgumentsTests
{
    private static CliArguments Parse(params string[] args)
    {
        var (arguments, error) = CliArguments.Parse(args);
        Assert.True(arguments is not null, error);
        return arguments!;
    }

    private static string Error(params string[] args)
    {
        var (arguments, error) = CliArguments.Parse(args);
        Assert.Null(arguments);
        return error!;
    }

    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void No_command_or_help_shows_the_general_help(params string[] args)
    {
        var arguments = Parse(args);

        Assert.Equal(CliCommand.Help, arguments.Command);
        Assert.Null(arguments.HelpTopic);
    }

    [Theory]
    [InlineData("compare", "help", "compare")]
    [InlineData("script", "script", "--help")]
    [InlineData("list", "list", "-h")]
    [InlineData("init", "init", "--help")]
    public void Help_can_be_asked_for_one_command(string topic, params string[] args)
    {
        var arguments = Parse(args);

        Assert.Equal(CliCommand.Help, arguments.Command);
        Assert.True(CliArguments.TryParseCommand(topic, out var expected));
        Assert.Equal(expected, arguments.HelpTopic);
    }

    [Fact]
    public void Commands_are_not_case_sensitive()
    {
        Assert.Equal(CliCommand.Compare, Parse("COMPARE", "-c", "jobs.json").Command);
    }

    [Fact]
    public void An_unknown_command_is_an_error()
    {
        Assert.Contains("Unknown command 'diff'", Error("diff", "-c", "jobs.json"));
    }

    [Fact]
    public void Compare_reads_jobs_selection_and_options()
    {
        var arguments = Parse("compare", "-c", "jobs.json", "-j", "1,3-5", "-d", "10", "--max-queries", "4",
            "--case-sensitive", "--respect-comments", "--ignore-index-physical");

        Assert.Equal(CliCommand.Compare, arguments.Command);
        Assert.Equal("jobs.json", arguments.Config);
        Assert.Equal("1,3-5", arguments.Selection);
        Assert.Equal(10, arguments.Details);
        Assert.Equal(4, arguments.MaxQueries);
        Assert.True(arguments.CaseSensitive);
        Assert.True(arguments.RespectComments);
        Assert.True(arguments.IgnoreIndexPhysicalOptions);
    }

    [Fact]
    public void Comparison_options_reach_the_snapshot_options()
    {
        var options = Parse("compare", "-c", "jobs.json", "--case-sensitive", "--keep-system-names",
            "--respect-whitespace", "--ignore-column-order").ToSnapshotOptions();

        Assert.True(options.CaseSensitiveNames);
        Assert.False(options.IgnoreSystemNamedConstraints);
        Assert.False(options.Normalization.IgnoreWhitespace);
        Assert.True(options.Normalization.IgnoreComments);
        Assert.True(options.IgnoreColumnOrder);
        Assert.False(options.KeepDisplayScripts);
    }

    [Fact]
    public void Defaults_match_the_comparison_defaults()
    {
        var options = Parse("compare", "-c", "jobs.json").ToSnapshotOptions();

        Assert.True(options.IgnoreSystemNamedConstraints);
        Assert.True(options.Normalization.IgnoreWhitespace);
        Assert.False(options.CaseSensitiveNames);
    }

    [Fact]
    public void Script_keeps_the_definitions_it_needs()
    {
        var arguments = Parse("script", "-s", "a", "-t", "b", "--out", "deploy.sql");

        Assert.Equal("deploy.sql", arguments.OutputPath);
        Assert.True(arguments.ToSnapshotOptions().KeepDisplayScripts);
    }

    [Fact]
    public void Script_needs_an_output_file()
    {
        Assert.Contains("--out", Error("script", "-c", "jobs.json"));
    }

    [Fact]
    public void Databases_are_split_on_commas()
    {
        var arguments = Parse("compare", "-s", "a", "-t", "b", "--databases", "Sales, Billing,,Hr");

        Assert.Equal(["Sales", "Billing", "Hr"], arguments.Databases);
    }

    [Theory]
    [InlineData("compare")]
    [InlineData("compare", "-s", "a")]
    [InlineData("list", "-t", "b")]
    public void A_job_source_is_required(params string[] args)
    {
        Assert.Contains("--config", Error(args));
    }

    [Fact]
    public void Config_and_a_single_pair_cannot_be_mixed()
    {
        Assert.Contains("not both", Error("compare", "-c", "jobs.json", "-s", "a", "-t", "b"));
    }

    [Fact]
    public void All_and_jobs_cannot_be_mixed()
    {
        Assert.Contains("not both", Error("compare", "-c", "jobs.json", "--all", "--jobs", "1"));
    }

    [Theory]
    [InlineData("compare", "-c", "jobs.json", "--out", "x.sql")]
    [InlineData("script", "-c", "jobs.json", "--out", "x.sql", "--details", "5")]
    [InlineData("list", "-c", "jobs.json", "--all")]
    [InlineData("compare", "-c", "jobs.json", "--colour")]
    public void Options_that_do_not_belong_to_the_command_are_rejected(params string[] args)
    {
        Assert.Contains("Unknown option", Error(args));
    }

    [Theory]
    [InlineData("compare", "-c")]
    [InlineData("compare", "-c", "jobs.json", "--jobs", "--all")]
    public void A_missing_value_is_an_error(params string[] args)
    {
        Assert.Contains("needs a value", Error(args));
    }

    [Theory]
    [InlineData("--details", "many")]
    [InlineData("--details", "-1")]
    [InlineData("--max-queries", "0")]
    public void Numbers_are_validated(string option, string value)
    {
        Assert.Contains("whole number", Error("compare", "-c", "jobs.json", option, value));
    }

    [Fact]
    public void Init_takes_exactly_one_path()
    {
        Assert.Equal("jobs.json", Parse("init", "jobs.json").OutputPath);
        Assert.Contains("exactly one", Error("init"));
        Assert.Contains("exactly one", Error("init", "a.json", "b.json"));
    }

    [Fact]
    public void Help_for_an_unknown_topic_is_an_error()
    {
        Assert.Contains("no help topic", Error("help", "deploy"));
    }
}
