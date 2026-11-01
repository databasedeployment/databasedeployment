using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// The display script of a table is produced from the catalog, the same way SSMS does
/// "Script Table as CREATE". The tests go through SnapshotBuilder so the real lookups are used.
/// </summary>
public class TableScriptWriterTests
{
    private const int OrderId = 10;
    private const int CustomerId = 20;

    private static ColumnRow Col(int objectId, int columnId, string name, string type, short maxLength = 4,
        bool nullable = false, bool identity = false, string? defaultName = null, string? defaultDefinition = null) =>
        new(objectId, columnId, name, "sys", type, maxLength, 18, 2, nullable, null, identity, false,
            defaultName, defaultDefinition, false, null, null, identity ? "1" : null, identity ? "1" : null);

    private static CatalogSet Catalog(Action<CatalogSet>? change = null)
    {
        var catalog = new CatalogSet
        {
            Objects =
            [
                new ObjectRow(OrderId, "sales", "Order", "U", DateTime.UnixEpoch, 0),
                new ObjectRow(CustomerId, "sales", "Customer", "U", DateTime.UnixEpoch, 0),
            ],
            Columns =
            [
                Col(OrderId, 1, "Id", "int", identity: true),
                Col(OrderId, 2, "CustomerId", "int"),
                Col(OrderId, 3, "Total", "decimal", 9),
                Col(OrderId, 4, "Note", "nvarchar", -1, nullable: true),
                Col(OrderId, 5, "CreatedAt", "datetime2", 8, defaultName: "DF_Order_CreatedAt", defaultDefinition: "(sysutcdatetime())"),
                Col(CustomerId, 1, "Id", "int"),
            ],
            Indexes =
            [
                new IndexRow(OrderId, 1, "PK_Order", "CLUSTERED", true, true, false, 0, false, false, null),
                new IndexRow(OrderId, 2, "IX_Order_CustomerId", "NONCLUSTERED", false, false, false, 90, false, false, null),
            ],
            IndexColumns =
            [
                new IndexColumnRow(OrderId, 1, 1, 1, 1, false, false),
                new IndexColumnRow(OrderId, 2, 1, 2, 1, false, false),
                new IndexColumnRow(OrderId, 2, 2, 3, 0, false, true),
            ],
            KeyConstraints = [new KeyConstraintRow(OrderId, 1, "PK_Order", "PK", false)],
            ForeignKeys = [new ForeignKeyRow(99, OrderId, "FK_Order_Customer", false, CustomerId, 1, 0, false, false)],
            ForeignKeyColumns = [new ForeignKeyColumnRow(99, 1, OrderId, 2, CustomerId, 1)],
        };
        change?.Invoke(catalog);
        return catalog;
    }

    private static string Script(CatalogSet catalog, SnapshotOptions? options = null)
    {
        options ??= SnapshotOptions.Default with { KeepDisplayScripts = true };
        var snapshot = SnapshotBuilder.Build(catalog, new ExtractionReport(), options);
        return snapshot.Objects[new ObjectKey("sales", "Order", ObjectKind.Table)].DisplayScript!;
    }

    [Fact]
    public void Writes_columns_with_types_identity_nullability_and_defaults()
    {
        var script = Script(Catalog());

        Assert.StartsWith("CREATE TABLE [sales].[Order] (", script);
        Assert.Contains("IDENTITY (1, 1)", script);
        Assert.Contains("DECIMAL (18, 2)", script);
        Assert.Contains("NVARCHAR (MAX)", script);
        Assert.Contains("NULL", script);
        Assert.Contains("CONSTRAINT [DF_Order_CreatedAt] DEFAULT (sysutcdatetime())", script);
    }

    [Fact]
    public void Writes_primary_and_foreign_keys_inside_the_table()
    {
        var script = Script(Catalog());

        Assert.Contains("CONSTRAINT [PK_Order] PRIMARY KEY CLUSTERED ([Id] ASC)", script);
        Assert.Contains("CONSTRAINT [FK_Order_Customer] FOREIGN KEY ([CustomerId]) REFERENCES [sales].[Customer] ([Id]) ON DELETE CASCADE", script);
    }

    [Fact]
    public void Writes_other_indexes_as_separate_batches()
    {
        var script = Script(Catalog());

        Assert.Contains("GO\nCREATE NONCLUSTERED INDEX [IX_Order_CustomerId]".Replace("\n", Environment.NewLine), script);
        Assert.Contains("INCLUDE([Total])", script);
        Assert.Contains("WITH (FILLFACTOR = 90)", script);
    }

    [Fact]
    public void System_named_constraint_names_are_not_written()
    {
        var systemNamed = Catalog(c => c.KeyConstraints[0] = c.KeyConstraints[0] with { Name = "PK__Order__3214EC07", IsSystemNamed = true });

        var script = Script(systemNamed);

        Assert.DoesNotContain("PK__Order__3214EC07", script);
        Assert.Contains("    PRIMARY KEY CLUSTERED ([Id] ASC)", script);
    }

    [Fact]
    public void A_disabled_foreign_key_is_disabled_after_the_table_is_created()
    {
        var disabled = Catalog(c => c.ForeignKeys[0] = c.ForeignKeys[0] with { IsDisabled = true });

        Assert.Contains("ALTER TABLE [sales].[Order] NOCHECK CONSTRAINT [FK_Order_Customer];", Script(disabled));
    }

    [Fact]
    public void Use_database_block_escapes_the_name()
    {
        var block = DeploymentHeader.UseDatabase("odd]name");

        Assert.StartsWith("USE [odd]]name];", block);
        Assert.Contains("GO", block);
    }
}
