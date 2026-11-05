using SchemaDiff.Core.Jobs;

namespace SchemaDiff.Core.Tests;

public class JobSelectorTests
{
    private static readonly IReadOnlyList<ComparisonJob> Jobs =
    [
        new("EDWSTG", "Server=a;Database=EDWSTG", "Server=b;Database=EDWSTG"),
        new("EDW", "Server=a;Database=EDW", "Server=b;Database=EDW"),
        new("EDWDM", "Server=a;Database=EDWDM", "Server=b;Database=EDWDM"),
        new("EDW-ARC", "Server=a;Database=EDWARC", "Server=b;Database=EDWARC"),
        new("RPT", "Server=a;Database=RPT", "Server=b;Database=RPT"),
    ];

    private static string[] Names(string expression) => JobSelector.Select(Jobs, expression).Select(j => j.Name).ToArray();

    [Theory]
    [InlineData("1,3", new[] { "EDWSTG", "EDWDM" })]
    [InlineData("2-4", new[] { "EDW", "EDWDM", "EDW-ARC" })]
    [InlineData("4-2", new[] { "EDW", "EDWDM", "EDW-ARC" })]
    [InlineData("1,4-5,EDWDM", new[] { "EDWSTG", "EDWDM", "EDW-ARC", "RPT" })]
    [InlineData("rpt edw", new[] { "EDW", "RPT" })]
    [InlineData("EDW-ARC", new[] { "EDW-ARC" })]
    public void Selects_by_index_range_and_name_in_definition_order(string expression, string[] expected)
    {
        Assert.Equal(expected, Names(expression));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ALL")]
    [InlineData("*")]
    public void Selects_every_job(string expression)
    {
        Assert.Equal(5, Names(expression).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("﻿")]
    public void Empty_selection_selects_nothing(string? expression)
    {
        Assert.Empty(JobSelector.Select(Jobs, expression));
    }

    [Fact]
    public void Invisible_characters_from_redirected_input_are_ignored()
    {
        Assert.Equal(["EDWSTG"], Names("﻿1"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("3-9")]
    [InlineData("UNKNOWN")]
    public void Invalid_tokens_are_rejected_with_a_clear_message(string expression)
    {
        var ex = Assert.Throws<ArgumentException>(() => JobSelector.Select(Jobs, expression));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}
