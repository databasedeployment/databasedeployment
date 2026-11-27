namespace SchemaDiff.Cli.Tests;

public class HelpTextTests
{
    [Fact]
    public void General_help_lists_every_command()
    {
        foreach (var command in new[] { "compare", "script", "list", "init", "help" })
            Assert.Contains($"  {command} ", HelpText.General);
    }

    [Fact]
    public void General_help_states_the_exit_codes()
    {
        Assert.Contains("Exit code: 0", HelpText.General);
    }

    [Theory]
    [InlineData("compare", new[] { "--config", "--jobs", "--all", "--select", "--details", "--case-sensitive", "--max-queries" })]
    [InlineData("script", new[] { "--config", "--out", "--jobs", "--ignore-index-physical" })]
    [InlineData("list", new[] { "--config", "--source", "--databases" })]
    [InlineData("init", new[] { "init <file>" })]
    public void Command_help_describes_its_options(string command, string[] expected)
    {
        Assert.True(CliArguments.TryParseCommand(command, out var parsed));
        var help = HelpText.For(parsed);

        foreach (var text in expected) Assert.Contains(text, help);
        Assert.Contains($"usage: schemadiff {command}", help);
    }

    [Fact]
    public void Options_of_other_commands_are_not_offered()
    {
        Assert.DoesNotContain("--out", HelpText.Compare);
        Assert.DoesNotContain("--details", HelpText.Script);
        Assert.DoesNotContain("--jobs", HelpText.List);
    }
}
