using SchemaDiff.Core.Execution;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// SqlClient does not understand GO, so the executor splits the script itself. The splitter
/// must only react to GO on its own line and must never cut a statement in half.
/// </summary>
public class ScriptBatchesTests
{
    [Fact]
    public void Splits_on_go_lines()
    {
        var batches = ScriptBatches.Split("CREATE TABLE dbo.A (Id int)\nGO\nCREATE VIEW dbo.V AS SELECT 1 AS X\nGO\n");

        Assert.Equal(2, batches.Count);
        Assert.StartsWith("CREATE TABLE", batches[0].Trim());
        Assert.StartsWith("CREATE VIEW", batches[1].Trim());
    }

    [Theory]
    [InlineData("go")]
    [InlineData("  GO  ")]
    [InlineData("\tGo")]
    [InlineData("GO -- end of batch")]
    public void Go_is_case_insensitive_and_may_have_spaces_or_a_comment(string separator)
    {
        Assert.Equal(2, ScriptBatches.Split($"SELECT 1\n{separator}\nSELECT 2").Count);
    }

    [Fact]
    public void Go_with_a_count_repeats_the_batch()
    {
        var batches = ScriptBatches.Split("INSERT dbo.T DEFAULT VALUES\nGO 3\n");

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Contains("INSERT", b));
    }

    [Theory]
    [InlineData("SELECT go_live FROM dbo.T")]
    [InlineData("GOTO done")]
    [InlineData("SELECT 'GO' AS word")]
    [InlineData("EXEC dbo.Go")]
    public void Go_inside_a_statement_does_not_split(string sql)
    {
        Assert.Single(ScriptBatches.Split(sql));
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        Assert.Equal(2, ScriptBatches.Split("SELECT 1\r\nGO\r\nSELECT 2\r\n").Count);
    }

    [Fact]
    public void Empty_batches_are_skipped()
    {
        Assert.Equal(["SELECT 1\n"], ScriptBatches.Split("GO\n\nGO\nSELECT 1\nGO\n   \nGO"));
        Assert.Empty(ScriptBatches.Split("  \n"));
    }

    [Fact]
    public void Line_numbers_of_each_batch_are_kept()
    {
        var batches = ScriptBatches.SplitWithLines("SELECT 1\nGO\n\nSELECT 2\nGO\nSELECT 3");

        Assert.Equal([1, 4, 6], batches.Select(b => b.FirstLine).ToArray());
    }
}
