using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Core.Tests.TestFactory;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Column level ALTER for tables that exist on both sides. Steps that can lose data or fail on
/// a populated table are written and also listed as data loss actions for the risk analyzer.
/// Changes that ALTER cannot express are reported as skipped instead of half scripted.
/// </summary>
public class TableAlterScriptTests
{
    private static TableScriptResult Generate(DatabaseSnapshot source, DatabaseSnapshot target) =>
        TableScriptGenerator.Generate(FixtureCompare.Compare(source, target));

    private static TableScriptResult Change(
        IReadOnlyList<ColumnInfo> sourceColumns, IReadOnlyList<ColumnInfo> targetColumns, long? rows = 10)
    {
        var key = Table("T");
        return Generate(
            Database("dev", [Obj(key, 1, columns: sourceColumns)]),
            Database("prod", [Obj(key, 2, columns: targetColumns, rowCount: rows)]));
    }

    private static int At(string sql, string text) => sql.IndexOf(text, StringComparison.Ordinal);

    // ADD COLUMN

    [Fact]
    public void Adding_nullable_column_is_emitted_directly()
    {
        var script = Change(
            [Column("Id"), Column("Note", typeName: "nvarchar", maxLength: 200)],
            [Column("Id")], rows: 5000);

        Assert.Contains(Table("T"), script.Included);
        Assert.Contains("ALTER TABLE [dbo].[T] ADD [Note] nvarchar(100) NULL;", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Added_column_keeps_its_default_inline()
    {
        var script = Change(
            [Column("Id"), Column("Qty", nullable: false, defaultDefinition: "((1))")],
            [Column("Id")]);

        Assert.Contains("ADD [Qty] int NOT NULL DEFAULT ((1));", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Adding_not_null_column_without_default_to_populated_table_is_marked()
    {
        var script = Change([Column("Id"), Column("Req", nullable: false)], [Column("Id")], rows: 100);

        Assert.Contains("ADD [Req] int NOT NULL;", script.Sql);
        var action = Assert.Single(script.DataLossActions);
        Assert.Equal("Req", action.Column);
        Assert.Contains("100 rows", action.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_not_null_column_to_empty_table_is_not_marked()
    {
        var script = Change([Column("Id"), Column("Req", nullable: false)], [Column("Id")], rows: 0);

        Assert.Contains("ADD [Req] int NOT NULL;", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Added_identity_column_keeps_identity_and_replication_flag()
    {
        var identity = Column("Id", nullable: false, identity: true) with
        {
            IdentitySeed = "1", IdentityIncrement = "1", IdentityNotForReplication = true,
        };

        var script = Change([Column("Other"), identity], [Column("Other")], rows: 0);

        Assert.Contains("ADD [Id] int IDENTITY(1, 1) NOT FOR REPLICATION NOT NULL;", script.Sql);
    }

    [Fact]
    public void Added_sparse_column_keeps_the_attribute()
    {
        var script = Change([Column("Id"), Column("C") with { IsSparse = true }], [Column("Id")]);

        Assert.Contains("ADD [C] int SPARSE NULL;", script.Sql);
    }

    [Fact]
    public void Added_rowguidcol_column_keeps_the_attribute()
    {
        var guid = Column("G", typeName: "uniqueidentifier", maxLength: 16) with { IsRowGuidCol = true };

        var script = Change([Column("Id"), guid], [Column("Id")]);

        Assert.Contains("ADD [G] uniqueidentifier ROWGUIDCOL NULL;", script.Sql);
    }

    [Fact]
    public void Added_typed_xml_column_keeps_its_collection()
    {
        var xml = Column("Payload", typeName: "xml", maxLength: -1) with { XmlCollection = "[dbo].[OrderSchema]" };

        var script = Change([Column("Id"), xml], [Column("Id")]);

        Assert.Contains("ADD [Payload] xml(CONTENT [dbo].[OrderSchema]) NULL;", script.Sql);
    }

    [Fact]
    public void Added_character_column_keeps_its_collation()
    {
        var script = Change(
            [Column("Id"), Column("Name", typeName: "varchar", maxLength: 30, collation: "Latin1_General_CS_AS")],
            [Column("Id")]);

        Assert.Contains("ADD [Name] varchar(30) COLLATE Latin1_General_CS_AS NULL;", script.Sql);
    }

    [Fact]
    public void Added_computed_column_is_skipped_because_its_expression_is_unknown()
    {
        // The snapshot keeps no computed expression for ALTER. Writing "AS NULL" would create a
        // different column, so the column is reported instead.
        var script = Change([Column("Id"), Column("Total", computed: true)], [Column("Id")]);

        Assert.DoesNotContain("[Total]", script.Sql);
        Assert.Contains(script.Skipped, s => s.Reason.Contains("[Total]", StringComparison.Ordinal));
    }

    // DROP COLUMN

    [Fact]
    public void Dropping_column_is_written_and_marked_as_data_loss()
    {
        var script = Change([Column("Id")], [Column("Id"), Column("Old")]);

        Assert.Contains("ALTER TABLE [dbo].[T] DROP COLUMN [Old];", script.Sql);
        var action = Assert.Single(script.DataLossActions);
        Assert.Equal("Old", action.Column);
        Assert.Equal("ALTER TABLE [dbo].[T] DROP COLUMN [Old];", action.Sql);
    }

    [Fact]
    public void Drop_column_is_emitted_before_add_column()
    {
        // Dropping first keeps a wide table under the row size and column count limits while
        // the change runs. SSDT orders them the same way.
        var script = Change([Column("Id"), Column("NewCol")], [Column("Id"), Column("OldCol")], rows: 100);

        var dropAt = At(script.Sql, "DROP COLUMN [OldCol]");
        var addAt = At(script.Sql, "ADD [NewCol]");
        Assert.True(dropAt >= 0 && addAt >= 0, "both DROP COLUMN and ADD are expected");
        Assert.True(dropAt < addAt, "DROP COLUMN must come before ADD");
    }

    // ALTER COLUMN

    [Fact]
    public void Widening_column_is_emitted_as_alter()
    {
        var script = Change(
            [Column("Name", typeName: "varchar", maxLength: 200)],
            [Column("Name", typeName: "varchar", maxLength: 50)]);

        Assert.Contains("ALTER TABLE [dbo].[T] ALTER COLUMN [Name] varchar(200) NULL;", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Widening_to_max_is_safe()
    {
        var script = Change(
            [Column("Name", typeName: "nvarchar", maxLength: -1)],
            [Column("Name", typeName: "nvarchar", maxLength: 100)]);

        Assert.Contains("ALTER COLUMN [Name] nvarchar(max) NULL;", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Narrowing_column_is_written_and_marked_as_data_loss()
    {
        var script = Change(
            [Column("Name", typeName: "varchar", maxLength: 20)],
            [Column("Name", typeName: "varchar", maxLength: 200)]);

        Assert.Contains("ALTER COLUMN [Name] varchar(20) NULL;", script.Sql);
        var action = Assert.Single(script.DataLossActions);
        Assert.Equal("Name", action.Column);
        Assert.Contains("varchar(200)", action.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Lower_decimal_precision_is_marked_as_data_loss()
    {
        var script = Change(
            [Column("Amount", typeName: "decimal", maxLength: 5, precision: 9, scale: 2)],
            [Column("Amount", typeName: "decimal", maxLength: 9, precision: 18, scale: 2)]);

        Assert.Contains("ALTER COLUMN [Amount] decimal(9,2) NULL;", script.Sql);
        Assert.Single(script.DataLossActions);
    }

    [Fact]
    public void Type_change_is_marked_as_data_loss()
    {
        var script = Change([Column("Code", typeName: "int")], [Column("Code", typeName: "bigint", maxLength: 8)]);

        Assert.Contains("ALTER COLUMN [Code] int NULL;", script.Sql);
        Assert.Single(script.DataLossActions);
    }

    [Fact]
    public void Making_column_not_null_on_populated_table_is_marked()
    {
        var script = Change([Column("C", nullable: false)], [Column("C", nullable: true)], rows: 3);

        Assert.Contains("ALTER COLUMN [C] int NOT NULL;", script.Sql);
        Assert.Contains(script.DataLossActions, a => a.Column == "C");
    }

    [Fact]
    public void Making_column_not_null_on_empty_table_is_not_marked()
    {
        var script = Change([Column("C", nullable: false)], [Column("C", nullable: true)], rows: 0);

        Assert.Contains("ALTER COLUMN [C] int NOT NULL;", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Identity_change_is_skipped_not_altered()
    {
        var script = Change([Column("Id", identity: true)], [Column("Id", identity: false)]);

        Assert.DoesNotContain("ALTER COLUMN [Id]", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == Table("T") && s.Reason.Contains("IDENTITY", StringComparison.Ordinal));
    }

    // Column attributes

    [Fact]
    public void Sparse_is_turned_on_and_off_with_alter_column()
    {
        var sparse = Column("C") with { IsSparse = true };

        Assert.Contains("ALTER TABLE [dbo].[T] ALTER COLUMN [C] ADD SPARSE;", Change([sparse], [Column("C")]).Sql);
        Assert.Contains("ALTER TABLE [dbo].[T] ALTER COLUMN [C] DROP SPARSE;", Change([Column("C")], [sparse]).Sql);
    }

    [Fact]
    public void Rowguidcol_is_toggled_with_add_and_drop()
    {
        var plain = Column("G", typeName: "uniqueidentifier", maxLength: 16);
        var guid = plain with { IsRowGuidCol = true };

        Assert.Contains("ALTER COLUMN [G] ADD ROWGUIDCOL;", Change([guid], [plain]).Sql);
        Assert.Contains("ALTER COLUMN [G] DROP ROWGUIDCOL;", Change([plain], [guid]).Sql);
    }

    [Fact]
    public void Retyped_sparse_column_carries_sparse_in_the_alter_and_is_not_added_twice()
    {
        var source = Column("C", typeName: "varchar", maxLength: 20) with { IsSparse = true };
        var target = Column("C", typeName: "varchar", maxLength: 10);

        var sql = Change([source], [target]).Sql;

        Assert.Contains("ALTER COLUMN [C] varchar(20) NULL SPARSE;", sql);
        Assert.DoesNotContain("ADD SPARSE", sql);
    }

    [Fact]
    public void Dropping_sparse_while_retyping_still_emits_drop_sparse()
    {
        // Leaving SPARSE out of the ALTER does not remove it, so the removal is written explicitly.
        var source = Column("C", typeName: "varchar", maxLength: 20);
        var target = Column("C", typeName: "varchar", maxLength: 10) with { IsSparse = true };

        var sql = Change([source], [target]).Sql;

        Assert.Contains("ALTER COLUMN [C] varchar(20) NULL;", sql);
        Assert.Contains("ALTER COLUMN [C] DROP SPARSE;", sql);
    }

    [Theory]
    [InlineData("FILESTREAM")]
    [InlineData("COLUMN_SET")]
    [InlineData("NOT FOR REPLICATION")]
    public void Attribute_that_alter_cannot_change_is_skipped_with_a_reason(string attribute)
    {
        var plain = Column("D", typeName: "varbinary", maxLength: -1, identity: attribute == "NOT FOR REPLICATION");
        var flagged = attribute switch
        {
            "FILESTREAM" => plain with { IsFileStream = true },
            "COLUMN_SET" => plain with { IsColumnSet = true },
            _ => plain with { IdentityNotForReplication = true },
        };

        var script = Change([flagged], [plain]);

        Assert.DoesNotContain(attribute, script.Sql);
        Assert.Contains(script.Skipped, s => s.Reason.Contains(attribute, StringComparison.Ordinal));
    }

    // Renames

    [Fact]
    public void Column_case_only_change_emits_sp_rename_when_case_sensitive_columns_on()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1,
            columns: [Column("Id"), Column("TranDate", typeName: "date")])], caseSensitiveColumns: true);
        var target = Database("prod", [Obj(key, 2,
            columns: [Column("Id"), Column("Trandate", typeName: "date")], rowCount: 1000)], caseSensitiveColumns: true);

        var script = Generate(source, target);

        Assert.Contains(key, script.Included);
        Assert.Contains("EXEC sp_rename N'[dbo].[T].[Trandate]', N'TranDate', N'COLUMN';", script.Sql);
        Assert.Empty(script.DataLossActions);
        Assert.DoesNotContain("ALTER COLUMN", script.Sql);
    }

    [Fact]
    public void Column_case_only_change_does_not_emit_sp_rename_by_default()
    {
        var script = Change(
            [Column("Id"), Column("TranDate", typeName: "date")],
            [Column("Id"), Column("Trandate", typeName: "date")]);

        Assert.DoesNotContain("sp_rename", script.Sql);
        Assert.DoesNotContain("ALTER COLUMN", script.Sql);
    }

    [Fact]
    public void Rename_escapes_quotes_in_the_new_name()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("O'Brien")])], caseSensitiveColumns: true);
        var target = Database("prod", [Obj(key, 2, columns: [Column("o'brien")])], caseSensitiveColumns: true);

        var sql = Generate(source, target).Sql;

        Assert.Contains("EXEC sp_rename N'[dbo].[T].[o''brien]', N'O''Brien', N'COLUMN';", sql);
    }

    // Changes without a column level script

    [Fact]
    public void Column_order_only_difference_is_reported_specifically()
    {
        var script = Change([Column("A"), Column("B"), Column("C")], [Column("A"), Column("C"), Column("B")]);

        Assert.DoesNotContain("ALTER TABLE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == Table("T") && s.Reason.Contains("column order", StringComparison.Ordinal));
    }

    [Fact]
    public void Table_without_column_information_is_skipped()
    {
        var key = Table("T");
        var script = Generate(Database("dev", [Obj(key, 1)]), Database("prod", [Obj(key, 2)]));

        Assert.Empty(script.Included);
        Assert.Contains(script.Skipped, s => s.Key == key);
    }

    [Fact]
    public void Alters_of_several_tables_are_written_in_name_order()
    {
        var a = Table("A");
        var b = Table("B");
        var source = Database("dev",
        [
            Obj(b, 1, columns: [Column("Id"), Column("X")]),
            Obj(a, 1, columns: [Column("Id"), Column("X")]),
        ]);
        var target = Database("prod", [Obj(b, 2, columns: [Column("Id")]), Obj(a, 2, columns: [Column("Id")])]);

        var sql = Generate(source, target).Sql;

        Assert.True(At(sql, "ALTER TABLE [dbo].[A]") < At(sql, "ALTER TABLE [dbo].[B]"));
    }

    // System versioning

    private const int CustomerId = 100;

    private static ColumnRow Col(int id, string name) => new(
        CustomerId, id, name, "sys", "int", 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null);

    private static ColumnRow PeriodCol(int id, string name, byte generatedAlwaysType) => new(
        CustomerId, id, name, "sys", "datetime2", 8, 27, 7, false, null, false, false,
        null, null, null, null, null, null, null, generatedAlwaysType, true);

    private static DatabaseSnapshot Customer(bool temporal)
    {
        var catalog = new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
            Columns = temporal
                ? [Col(1, "Id"), PeriodCol(2, "ValidFrom", 1), PeriodCol(3, "ValidTo", 2)]
                : [Col(1, "Id"), PeriodCol(2, "ValidFrom", 0), PeriodCol(3, "ValidTo", 0)],
            Temporal = temporal ? [new TemporalRow(CustomerId, "dbo", "CustomerHistory", "ValidFrom", "ValidTo")] : [],
        };
        return SnapshotBuilder.Build(catalog, new ExtractionReport(), SnapshotOptions.Default with { KeepDisplayScripts = true });
    }

    [Fact]
    public void Disabling_versioning_emits_set_off_and_drop_period()
    {
        var sql = Generate(Customer(temporal: false), Customer(temporal: true)).Sql;

        Assert.Contains("ALTER TABLE [dbo].[Customer] SET (SYSTEM_VERSIONING = OFF);", sql);
        Assert.Contains("ALTER TABLE [dbo].[Customer] DROP PERIOD FOR SYSTEM_TIME;", sql);
        Assert.True(At(sql, "SYSTEM_VERSIONING = OFF") < At(sql, "DROP PERIOD"));
    }

    [Fact]
    public void Enabling_versioning_is_flagged_for_manual_apply()
    {
        var script = Generate(Customer(temporal: true), Customer(temporal: false));

        Assert.Contains(script.Skipped, s => s.Reason.Contains("temporal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("SYSTEM_VERSIONING = ON", script.Sql);
    }
}
