using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Core.Tests.TestFactory;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// CREATE and DROP of whole tables. New tables are written from their display script and
/// created in foreign key order, removed tables are dropped in the reverse order.
/// </summary>
public class TableScriptGeneratorTests
{
    private static TableScriptResult Generate(
        DatabaseSnapshot source, DatabaseSnapshot target, TableScriptOptions? options = null) =>
        TableScriptGenerator.Generate(FixtureCompare.Compare(source, target), options);

    private static int At(string sql, string text) => sql.IndexOf(text, StringComparison.Ordinal);

    [Fact]
    public void Added_table_emits_its_create_script()
    {
        var key = Table("Orders");
        var source = Database("dev",
            [Obj(key, 1, displayScript: "CREATE TABLE [dbo].[Orders] (\n    [Id] INT NOT NULL\n);",
                columns: [Column("Id", nullable: false)])]);

        var script = Generate(source, Database("prod", []));

        Assert.Contains(key, script.Included);
        Assert.Contains("CREATE TABLE [dbo].[Orders]", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Every_table_ends_its_own_batch()
    {
        var key = Table("Orders");
        var source = Database("dev",
            [Obj(key, 1, displayScript: "CREATE TABLE [dbo].[Orders] ([Id] INT NOT NULL);")]);

        var sql = Generate(source, Database("prod", [])).Sql;

        Assert.Contains("CREATE TABLE [dbo].[Orders] ([Id] INT NOT NULL);" + Environment.NewLine + "GO", sql);
    }

    [Fact]
    public void Added_tables_are_created_in_foreign_key_order()
    {
        // NC references NP and both are new. NP has to exist first, otherwise the inline
        // foreign key of NC points at a missing table (Msg 1767).
        var np = Table("NP");
        var nc = Table("NC");
        var source = Database("dev",
        [
            Obj(nc, 2, displayScript: "CREATE TABLE [dbo].[NC] ([Id] INT NOT NULL, [PId] INT NULL, " +
                "CONSTRAINT [FK_NC_NP] FOREIGN KEY ([PId]) REFERENCES [dbo].[NP]([Id]));"),
            Obj(np, 1, displayScript: "CREATE TABLE [dbo].[NP] ([Id] INT NOT NULL);"),
        ], references: new() { [nc] = [np] });

        var sql = Generate(source, Database("prod", [])).Sql;

        Assert.True(At(sql, "CREATE TABLE [dbo].[NP]") < At(sql, "CREATE TABLE [dbo].[NC]"),
            "the referenced table (NP) must be created before the table that owns the foreign key (NC)");
    }

    [Fact]
    public void Foreign_key_cycle_between_new_tables_is_reported()
    {
        var a = Table("A");
        var b = Table("B");
        var source = Database("dev",
        [
            Obj(a, 1, displayScript: "CREATE TABLE [dbo].[A] ([Id] INT NOT NULL);"),
            Obj(b, 2, displayScript: "CREATE TABLE [dbo].[B] ([Id] INT NOT NULL);"),
        ], references: new() { [a] = [b], [b] = [a] });

        var script = Generate(source, Database("prod", []));

        Assert.True(script.HadDependencyCycle);
        Assert.Equal(2, script.Included.Count);
    }

    [Fact]
    public void Removed_tables_are_dropped_in_reverse_foreign_key_order()
    {
        // OC references OP and both are removed. The owner of the foreign key (OC) has to go
        // first, otherwise dropping OP fails because another object still references it.
        var op = Table("OP");
        var oc = Table("OC");
        var target = Database("prod",
        [
            Obj(op, 1, columns: [Column("Id")]),
            Obj(oc, 2, columns: [Column("Id"), Column("PId")]),
        ], references: new() { [oc] = [op] });

        var sql = Generate(Database("dev", []), target).Sql;

        Assert.True(At(sql, "DROP TABLE [dbo].[OC]") < At(sql, "DROP TABLE [dbo].[OP]"),
            "the owner of the foreign key (OC) must be dropped before the referenced table (OP)");
    }

    [Fact]
    public void Removed_table_is_dropped_behind_an_existence_check()
    {
        var key = Table("Gone");
        var target = Database("prod", [Obj(key, 1, columns: [Column("Id")])]);

        var script = Generate(Database("dev", []), target);

        Assert.Contains("IF OBJECT_ID(N'[dbo].[Gone]', N'U') IS NOT NULL", script.Sql);
        Assert.Contains("DROP TABLE [dbo].[Gone];", script.Sql);
        Assert.Contains(key, script.Included);
    }

    [Fact]
    public void Removed_table_is_listed_as_a_data_loss_action_with_its_row_count()
    {
        var key = Table("Gone");
        var target = Database("prod", [Obj(key, 1, columns: [Column("Id")], rowCount: 999)]);

        var script = Generate(Database("dev", []), target);

        var action = Assert.Single(script.DataLossActions);
        Assert.Equal(key, action.Table);
        Assert.Contains("999", action.Description, StringComparison.Ordinal);
        Assert.Equal("DROP TABLE [dbo].[Gone];", action.Sql);
    }

    [Fact]
    public void Drops_can_be_turned_off()
    {
        var key = Table("Gone");
        var target = Database("prod", [Obj(key, 1, columns: [Column("Id")], rowCount: 5)]);

        var script = Generate(Database("dev", []), target, new TableScriptOptions { IncludeDrops = false });

        Assert.DoesNotContain("DROP TABLE", script.Sql);
        Assert.DoesNotContain(key, script.Included);
        Assert.Contains(script.Skipped, s => s.Key == key);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Creates_come_before_drops()
    {
        var added = Table("New");
        var removed = Table("Old");
        var source = Database("dev", [Obj(added, 1, displayScript: "CREATE TABLE [dbo].[New] ([Id] INT NOT NULL);")]);
        var target = Database("prod", [Obj(removed, 2, columns: [Column("Id")])]);

        var sql = Generate(source, target).Sql;

        Assert.True(At(sql, "CREATE TABLE [dbo].[New]") < At(sql, "DROP TABLE [dbo].[Old]"));
    }

    [Fact]
    public void Added_table_without_display_script_is_skipped()
    {
        var key = Table("Orders");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Id")])]);

        var script = Generate(source, Database("prod", []));

        Assert.Equal(string.Empty, script.Sql);
        Assert.True(script.IsEmpty);
        Assert.Contains(script.Skipped, s => s.Key == key && s.Reason.Contains("display script", StringComparison.Ordinal));
    }

    [Fact]
    public void Table_that_could_not_be_compared_is_skipped()
    {
        var key = Table("Secret");
        var source = Database("dev", [Obj(key, 1, incomparable: true)]);
        var target = Database("prod", [Obj(key, 1)]);

        var script = Generate(source, target);

        Assert.Empty(script.Included);
        Assert.Contains(script.Skipped, s => s.Key == key);
    }

    [Fact]
    public void Modules_are_ignored_only_tables_are_handled()
    {
        var view = View("V");
        var table = Table("T");
        var source = Database("dev",
        [
            Obj(view, 1, displayScript: "CREATE VIEW [dbo].[V] AS SELECT 1 AS x"),
            Obj(table, 1, displayScript: "CREATE TABLE [dbo].[T] ([Id] INT NOT NULL);", columns: [Column("Id", nullable: false)]),
        ]);

        var script = Generate(source, Database("prod", []));

        Assert.Contains(table, script.Included);
        Assert.DoesNotContain(view, script.Included);
        Assert.DoesNotContain("CREATE VIEW", script.Sql);
    }

    // New tables built from catalog rows: the CREATE comes from TableScriptWriter, so every
    // column property it writes reaches the deployment script unchanged.

    private const int OrderLineId = 100;

    private static ColumnRow Row(
        int columnId, string name, string type = "int", short maxLength = 4, bool nullable = false,
        bool identity = false, bool computed = false, string? computedDefinition = null, bool? persisted = null,
        string? defaultName = null, string? defaultDefinition = null, bool sparse = false) =>
        new(OrderLineId, columnId, name, "sys", type, maxLength, 10, 0, nullable, null, identity, computed,
            defaultName, defaultDefinition, defaultName is null ? null : false, computedDefinition, persisted,
            identity ? "1" : null, identity ? "1" : null, IsSparse: sparse);

    private static string CreateFromCatalog(params ColumnRow[] columns)
    {
        var catalog = new CatalogSet
        {
            ServerName = "TESTSRV",
            DatabaseName = "dev",
            Objects = [new ObjectRow(OrderLineId, "sales", "OrderLine", "U", DateTime.UnixEpoch, 0)],
            Columns = [.. columns],
        };
        var source = SnapshotBuilder.Build(catalog, new ExtractionReport(),
            SnapshotOptions.Default with { KeepDisplayScripts = true });

        return Generate(source, Database("prod", [])).Sql;
    }

    [Fact]
    public void New_table_keeps_identity_columns()
    {
        var sql = CreateFromCatalog(Row(1, "Id", identity: true));

        Assert.Contains("[Id] INT IDENTITY (1, 1) NOT NULL", sql);
    }

    [Fact]
    public void New_table_keeps_computed_columns()
    {
        var sql = CreateFromCatalog(
            Row(1, "Qty"), Row(2, "Price"),
            Row(3, "Total", computed: true, computedDefinition: "([Qty]*[Price])", persisted: true));

        Assert.Contains("AS ([Qty]*[Price]) PERSISTED", sql);
    }

    [Fact]
    public void New_table_keeps_sparse_columns()
    {
        var sql = CreateFromCatalog(Row(1, "Id"), Row(2, "Note", "varchar", 50, nullable: true, sparse: true));

        Assert.Contains("SPARSE NULL", sql);
    }

    [Fact]
    public void New_table_keeps_named_defaults()
    {
        var sql = CreateFromCatalog(Row(1, "Qty", defaultName: "DF_OrderLine_Qty", defaultDefinition: "((1))"));

        Assert.Contains("CONSTRAINT [DF_OrderLine_Qty] DEFAULT ((1))", sql);
    }
}
