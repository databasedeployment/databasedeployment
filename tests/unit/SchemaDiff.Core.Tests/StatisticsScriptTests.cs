using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Core.Tests.TestFactory;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// User created statistics on changed tables. ALTER STATISTICS can only switch NORECOMPUTE, so
/// every other change drops and recreates the statistic. Drops run before column changes and
/// creates after them, the same way as indexes.
/// </summary>
public class StatisticsScriptTests
{
    private static readonly ObjectKey Customer = Table("Customer");

    private static StatisticsDefinition Stat(
        string name, string[] columns, string? filter = null, bool noRecompute = false, bool incremental = false) =>
        new(name, columns, filter, noRecompute, incremental);

    private static ObjectSnapshot Snapshot(UInt128 hash, IReadOnlyList<ColumnInfo> columns, params StatisticsDefinition[] statistics)
    {
        var snapshot = Obj(Customer, hash, columns: columns);
        snapshot.StatisticsDefinitions = statistics;
        return snapshot;
    }

    private static readonly ColumnInfo[] Columns = [Column("Id"), Column("Name"), Column("Email")];

    private static string Script(ObjectSnapshot source, ObjectSnapshot target) =>
        TableScriptGenerator.Generate(FixtureCompare.Compare(Database("dev", [source]), Database("prod", [target]))).Sql;

    private static int At(string sql, string text) => sql.IndexOf(text, StringComparison.Ordinal);

    [Fact]
    public void Added_statistic_is_created()
    {
        var sql = Script(Snapshot(1, Columns, Stat("ST_Customer_Email", ["Email"])), Snapshot(2, Columns));

        Assert.Contains("CREATE STATISTICS [ST_Customer_Email] ON [dbo].[Customer] ([Email]);", sql);
        Assert.DoesNotContain("DROP STATISTICS", sql);
    }

    [Fact]
    public void Removed_statistic_is_dropped()
    {
        var sql = Script(Snapshot(1, Columns), Snapshot(2, Columns, Stat("ST_Customer_Email", ["Email"])));

        Assert.Contains("DROP STATISTICS [dbo].[Customer].[ST_Customer_Email];", sql);
        Assert.DoesNotContain("CREATE STATISTICS", sql);
    }

    [Fact]
    public void Changed_statistic_is_dropped_and_recreated()
    {
        var sql = Script(
            Snapshot(1, Columns, Stat("ST_X", ["Name", "Email"])),
            Snapshot(2, Columns, Stat("ST_X", ["Name"])));

        Assert.Contains("DROP STATISTICS [dbo].[Customer].[ST_X];", sql);
        Assert.Contains("CREATE STATISTICS [ST_X] ON [dbo].[Customer] ([Name], [Email]);", sql);
        Assert.True(At(sql, "DROP STATISTICS") < At(sql, "CREATE STATISTICS"));
    }

    [Fact]
    public void Column_order_inside_a_statistic_counts_as_a_change()
    {
        var sql = Script(
            Snapshot(1, Columns, Stat("ST_X", ["Email", "Name"])),
            Snapshot(2, Columns, Stat("ST_X", ["Name", "Email"])));

        Assert.Contains("CREATE STATISTICS [ST_X] ON [dbo].[Customer] ([Email], [Name]);", sql);
    }

    [Fact]
    public void Identical_statistic_is_left_alone()
    {
        var sql = Script(
            Snapshot(1, [.. Columns, Column("Note")], Stat("ST_X", ["Email"])),
            Snapshot(2, Columns, Stat("ST_X", ["Email"])));

        Assert.Contains("ADD [Note]", sql);
        Assert.DoesNotContain("STATISTICS", sql);
    }

    [Fact]
    public void Statistic_drop_runs_before_the_column_it_covers_is_dropped()
    {
        var sql = Script(
            Snapshot(1, [Column("Id"), Column("Name")]),
            Snapshot(2, Columns, Stat("ST_Email", ["Email"])));

        var stat = At(sql, "DROP STATISTICS");
        var column = At(sql, "DROP COLUMN [Email]");
        Assert.True(stat >= 0 && column >= 0 && stat < column, "the statistic has to go before its column");
    }

    [Fact]
    public void Statistic_create_runs_after_the_column_it_covers_is_added()
    {
        var sql = Script(
            Snapshot(1, Columns, Stat("ST_Email", ["Email"])),
            Snapshot(2, [Column("Id"), Column("Name")]));

        var column = At(sql, "ADD [Email]");
        var stat = At(sql, "CREATE STATISTICS");
        Assert.True(column >= 0 && stat >= 0 && column < stat, "the statistic is created after its column exists");
    }

    [Fact]
    public void Norecompute_is_written_into_the_create()
    {
        var sql = Script(Snapshot(1, Columns, Stat("ST_E", ["Email"], noRecompute: true)), Snapshot(2, Columns));

        Assert.Contains("CREATE STATISTICS [ST_E] ON [dbo].[Customer] ([Email]) WITH NORECOMPUTE;", sql);
    }

    [Fact]
    public void Filter_comes_before_the_with_clause()
    {
        var sql = Script(
            Snapshot(1, Columns, Stat("ST_E", ["Email"], filter: "([Id]>(0))", noRecompute: true)),
            Snapshot(2, Columns));

        Assert.Contains("CREATE STATISTICS [ST_E] ON [dbo].[Customer] ([Email]) WHERE ([Id]>(0)) WITH NORECOMPUTE;", sql);
    }

    [Fact]
    public void Norecompute_and_incremental_share_one_with_clause()
    {
        var sql = Script(
            Snapshot(1, Columns, Stat("ST_E", ["Email"], noRecompute: true, incremental: true)),
            Snapshot(2, Columns));

        Assert.Contains("WITH NORECOMPUTE, INCREMENTAL = ON;", sql);
    }

    [Fact]
    public void Statistic_names_with_brackets_are_escaped()
    {
        var sql = Script(Snapshot(1, Columns, Stat("ST_[odd]", ["Email"])), Snapshot(2, Columns));

        Assert.Contains("CREATE STATISTICS [ST_[odd]]] ON [dbo].[Customer] ([Email]);", sql);
    }
}
