using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// The builder turns raw catalog rows into canonical text and hashes. These tests build small
/// catalogs by hand, so the rules can be checked without a SQL Server.
/// </summary>
public class SnapshotBuilderTests
{
    private const int CustomerId = 100;
    private const int OrderId = 200;
    private const int ProcId = 300;

    private static ColumnRow Col(int objectId, int columnId, string name, string type = "int",
        short maxLength = 4, bool nullable = false) =>
        new(objectId, columnId, name, "sys", type, maxLength, 10, 0, nullable, null, false, false,
            null, null, null, null, null, null, null);

    private static CatalogSet Catalog(Action<CatalogSet>? change = null)
    {
        var catalog = new CatalogSet
        {
            ServerName = "TESTSRV",
            DatabaseName = "Sales",
            Schemas = [new SchemaRow(5, "sales")],
            Objects =
            [
                new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
                new ObjectRow(OrderId, "dbo", "Order", "U", DateTime.UnixEpoch, 0),
                new ObjectRow(ProcId, "dbo", "GetCustomer", "P", DateTime.UnixEpoch, 0),
            ],
            Columns =
            [
                Col(CustomerId, 1, "Id"),
                Col(CustomerId, 2, "Name", "nvarchar", 100, nullable: true),
                Col(OrderId, 1, "Id"),
                Col(OrderId, 2, "CustomerId"),
            ],
            Indexes = [new IndexRow(CustomerId, 1, "PK__Customer__3214EC07", "CLUSTERED", true, true, false, 0, false, false, null)],
            IndexColumns = [new IndexColumnRow(CustomerId, 1, 1, 1, 1, false, false)],
            KeyConstraints = [new KeyConstraintRow(CustomerId, 1, "PK__Customer__3214EC07", "PK", true)],
            ForeignKeys = [new ForeignKeyRow(900, OrderId, "FK_Order_Customer", false, CustomerId, 0, 0, false, false)],
            ForeignKeyColumns = [new ForeignKeyColumnRow(900, 1, OrderId, 2, CustomerId, 1)],
            Modules = [new ModuleRow(ProcId, "CREATE PROCEDURE dbo.GetCustomer AS SELECT Id FROM dbo.Customer", true, true)],
        };
        change?.Invoke(catalog);
        return catalog;
    }

    private static DatabaseSnapshot Build(CatalogSet catalog, SnapshotOptions? options = null) =>
        SnapshotBuilder.Build(catalog, new ExtractionReport(), options ?? SnapshotOptions.Default);

    private static ObjectSnapshot Table(DatabaseSnapshot snapshot, string name) =>
        snapshot.Objects[new ObjectKey("dbo", name, ObjectKind.Table)];

    [Fact]
    public void Same_catalog_always_gives_the_same_hash()
    {
        Assert.Equal(Table(Build(Catalog()), "Customer").Hash, Table(Build(Catalog()), "Customer").Hash);
    }

    [Fact]
    public void Schemas_tables_and_modules_become_objects()
    {
        var snapshot = Build(Catalog());

        Assert.Contains(new ObjectKey("sales", "sales", ObjectKind.Schema), snapshot.Objects.Keys);
        Assert.Contains(new ObjectKey("dbo", "GetCustomer", ObjectKind.Procedure), snapshot.Objects.Keys);
        Assert.Equal(2, snapshot.Objects.Keys.Count(k => k.Kind == ObjectKind.Table));
    }

    [Fact]
    public void A_column_type_change_only_changes_the_columns_part()
    {
        var before = Table(Build(Catalog()), "Customer");
        var after = Table(Build(Catalog(c => c.Columns[1] = c.Columns[1] with { MaxLength = 200 })), "Customer");

        Assert.NotEqual(before.Hash, after.Hash);
        Assert.NotEqual(before.Parts["columns"], after.Parts["columns"]);
        Assert.Equal(before.Parts["indexes"], after.Parts["indexes"]);
    }

    [Fact]
    public void Column_name_casing_is_ignored_unless_requested()
    {
        var renamed = Catalog(c => c.Columns[1] = c.Columns[1] with { Name = "NAME" });

        Assert.Equal(Table(Build(Catalog()), "Customer").Hash, Table(Build(renamed), "Customer").Hash);

        var strict = SnapshotOptions.Default with { CaseSensitiveColumnNames = true };
        Assert.NotEqual(Table(Build(Catalog(), strict), "Customer").Hash, Table(Build(renamed, strict), "Customer").Hash);
    }

    [Fact]
    public void System_generated_constraint_names_do_not_make_a_difference()
    {
        var otherName = Catalog(c =>
        {
            c.Indexes[0] = c.Indexes[0] with { Name = "PK__Customer__9A1B2C3D" };
            c.KeyConstraints[0] = c.KeyConstraints[0] with { Name = "PK__Customer__9A1B2C3D" };
        });

        Assert.Equal(Table(Build(Catalog()), "Customer").Hash, Table(Build(otherName), "Customer").Hash);
    }

    [Fact]
    public void Column_order_is_ignored_only_with_the_option()
    {
        var swapped = Catalog(c => c.Columns = [c.Columns[1] with { ColumnId = 1 }, c.Columns[0] with { ColumnId = 2 }, c.Columns[2], c.Columns[3]]);

        Assert.NotEqual(Table(Build(Catalog()), "Customer").Hash, Table(Build(swapped), "Customer").Hash);

        var ignoreOrder = SnapshotOptions.Default with { IgnoreColumnOrder = true };
        Assert.Equal(Table(Build(Catalog(), ignoreOrder), "Customer").Parts["columns"],
            Table(Build(swapped, ignoreOrder), "Customer").Parts["columns"]);
    }

    [Fact]
    public void Formatting_only_changes_in_a_module_body_are_equal()
    {
        var reformatted = Catalog(c => c.Modules[0] = c.Modules[0] with
        {
            Definition = "CREATE PROCEDURE dbo.GetCustomer AS\n    -- read one row\n    SELECT Id\n    FROM   dbo.Customer",
        });
        var key = new ObjectKey("dbo", "GetCustomer", ObjectKind.Procedure);

        Assert.Equal(Build(Catalog()).Objects[key].Hash, Build(reformatted).Objects[key].Hash);
    }

    [Fact]
    public void A_module_without_a_readable_definition_is_never_equal()
    {
        var hidden = Catalog(c => c.Modules[0] = c.Modules[0] with { Definition = null });

        var proc = Build(hidden).Objects[new ObjectKey("dbo", "GetCustomer", ObjectKind.Procedure)];

        Assert.True(proc.Incomparable);
        Assert.False(string.IsNullOrEmpty(proc.IncomparableReason));
    }

    [Fact]
    public void Foreign_keys_are_added_to_the_reference_graph()
    {
        var snapshot = Build(Catalog());

        var references = snapshot.References[new ObjectKey("dbo", "Order", ObjectKind.Table)];

        Assert.Contains(new ObjectKey("dbo", "Customer", ObjectKind.Table), references);
    }

    [Fact]
    public void Structural_definitions_are_only_kept_when_requested()
    {
        Assert.Null(Table(Build(Catalog()), "Customer").IndexDefinitions);

        var keep = SnapshotOptions.Default with { KeepDisplayScripts = true, IgnoreSystemNamedConstraints = false };
        var definitions = Table(Build(Catalog(), keep), "Customer").IndexDefinitions;

        Assert.NotNull(definitions);
        Assert.Single(definitions, d => d.IsPrimaryKey);
    }
}
