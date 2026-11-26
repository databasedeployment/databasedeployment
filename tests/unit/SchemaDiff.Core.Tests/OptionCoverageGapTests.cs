using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Comparison options that no other test covers directly. With the option off the difference
/// must show up, with the option on it must be ignored.
/// </summary>
public class OptionCoverageGapTests
{
    private const int TableId = 100;

    private static ColumnRow Col(int columnId, string name, string? collation = null,
        string? defaultName = null, string? defaultDefinition = null, bool defaultSystemNamed = false) =>
        new(TableId, columnId, name, "sys", "int", 4, 10, 0, true, collation, false, false,
            defaultName, defaultDefinition, defaultName is null ? null : defaultSystemNamed,
            null, null, null, null);

    private static CatalogSet Table(params ColumnRow[] columns) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(TableId, "dbo", "T", "U", DateTime.UnixEpoch, 0)],
        Columns = [.. columns],
    };

    private static bool Differs(CatalogSet source, CatalogSet target, SnapshotOptions options)
    {
        var key = TestFactory.Table("T");
        return SnapshotBuilder.Build(source, new ExtractionReport(), options).Objects[key].Hash
            != SnapshotBuilder.Build(target, new ExtractionReport(), options).Objects[key].Hash;
    }

    [Fact]
    public void IgnoreColumnOrder_toggles_the_difference()
    {
        var source = Table(Col(1, "Id"), Col(2, "A"));
        var target = Table(Col(1, "A"), Col(2, "Id"));   // same columns in reverse order

        Assert.True(Differs(source, target, SnapshotOptions.Default), "Off: the order difference must show");
        Assert.False(Differs(source, target, SnapshotOptions.Default with { IgnoreColumnOrder = true }), "On: it must be ignored");
    }

    [Fact]
    public void IgnoreCollation_toggles_the_difference()
    {
        var source = Table(Col(1, "Id"), Col(2, "Name", collation: "Latin1_General_CI_AS"));
        var target = Table(Col(1, "Id"), Col(2, "Name", collation: "SQL_Latin1_General_CP1_CI_AS"));

        Assert.True(Differs(source, target, SnapshotOptions.Default), "Off: the collation difference must show");
        Assert.False(Differs(source, target, SnapshotOptions.Default with { IgnoreCollation = true }), "On: it must be ignored");
    }

    [Fact]
    public void IgnoreSystemNamedConstraints_toggles_the_difference()
    {
        // A system-named DEFAULT whose name differs while the definition is the same.
        var source = Table(Col(1, "Id"), Col(2, "A", defaultName: "DF__T__A__1234", defaultDefinition: "((0))", defaultSystemNamed: true));
        var target = Table(Col(1, "Id"), Col(2, "A", defaultName: "DF__T__A__5678", defaultDefinition: "((0))", defaultSystemNamed: true));

        Assert.True(Differs(source, target, SnapshotOptions.Default with { IgnoreSystemNamedConstraints = false }), "Off: the name difference must show");
        Assert.False(Differs(source, target, SnapshotOptions.Default with { IgnoreSystemNamedConstraints = true }), "On: the name must be ignored");
    }
}
