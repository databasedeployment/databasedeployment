using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Index and constraint changes on tables that exist on both sides. The snapshot only carries
/// the structural index and constraint definitions when display scripts are kept.
/// </summary>
public class TableIndexConstraintTests
{
    private const int CustomerId = 100;
    private const int OrderId = 200;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name, int objectId = CustomerId, bool nullable = true) => new(
        objectId, id, name, "sys", "int", 4, 10, 0, nullable, null, false, false, null, null, null, null, null, null, null);

    private static IndexRow Idx(int indexId, string name, bool pk = false, bool uq = false, string? filter = null,
        string? compression = null, bool rowLocks = true, bool pageLocks = true, int dataSpaceId = 0) =>
        new(CustomerId, indexId, name, pk ? "CLUSTERED" : "NONCLUSTERED", pk || uq, pk, uq, 0, false, false, filter,
            compression, rowLocks, pageLocks, dataSpaceId);

    private static IndexColumnRow Key(int indexId, int columnId, byte ordinal, bool included = false) =>
        new(CustomerId, indexId, columnId, columnId, included ? (byte)0 : ordinal, false, included);

    private static CatalogSet Customer(
        List<IndexRow>? indexes = null, List<IndexColumnRow>? indexColumns = null,
        List<CheckConstraintRow>? checks = null, List<IndexExtraRow>? extras = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "Name"), Col(3, "Email")],
        Indexes = indexes ?? [],
        IndexColumns = indexColumns ?? [],
        CheckConstraints = checks ?? [],
        IndexExtras = extras ?? [],
        DataSpaces = [new DataSpaceRow(1, "PRIMARY", "FG"), new DataSpaceRow(2, "IDXFG", "FG")],
    };

    private static DatabaseSnapshot Build(CatalogSet catalog) =>
        SnapshotBuilder.Build(catalog, new ExtractionReport(), WithScripts);

    private static TableScriptResult Generate(CatalogSet source, CatalogSet target, TableScriptOptions? options = null) =>
        TableScriptGenerator.Generate(FixtureCompare.Compare(Build(source), Build(target)), options);

    private static string Script(CatalogSet source, CatalogSet target) => Generate(source, target).Sql;

    private static int At(string sql, string text) => sql.IndexOf(text, StringComparison.Ordinal);

    // Indexes

    [Fact]
    public void Changed_index_include_is_dropped_and_recreated()
    {
        var sql = Script(
            Customer([Idx(2, "IX_Customer_Email")], [Key(2, 3, 1), Key(2, 2, 0, included: true)]),
            Customer([Idx(2, "IX_Customer_Email")], [Key(2, 3, 1)]));

        Assert.Contains("DROP INDEX [IX_Customer_Email] ON [dbo].[Customer];", sql);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Customer_Email] ON [dbo].[Customer] ([Email] ASC) INCLUDE ([Name]);", sql);
        Assert.True(At(sql, "DROP INDEX") < At(sql, "CREATE NONCLUSTERED INDEX"));
    }

    [Fact]
    public void Added_index_is_created_without_drop()
    {
        var sql = Script(Customer([Idx(2, "IX_New")], [Key(2, 3, 1)]), Customer());

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_New] ON [dbo].[Customer] ([Email] ASC);", sql);
        Assert.DoesNotContain("DROP INDEX", sql);
    }

    [Fact]
    public void Removed_index_is_dropped_without_create()
    {
        var sql = Script(Customer(), Customer([Idx(2, "IX_Old")], [Key(2, 3, 1)]));

        Assert.Contains("DROP INDEX [IX_Old] ON [dbo].[Customer];", sql);
        Assert.DoesNotContain("CREATE NONCLUSTERED INDEX", sql);
    }

    [Fact]
    public void Filtered_index_keeps_its_where_clause()
    {
        var sql = Script(Customer([Idx(2, "IX_Active", filter: "([Id]>(0))")], [Key(2, 3, 1)]), Customer());

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Active] ON [dbo].[Customer] ([Email] ASC) WHERE ([Id]>(0));", sql);
    }

    [Fact]
    public void Index_only_change_is_included_not_reported_for_review()
    {
        var script = Generate(Customer([Idx(2, "IX_New")], [Key(2, 3, 1)]), Customer());

        Assert.Contains(TestFactory.Table("Customer"), script.Included);
        Assert.DoesNotContain(script.Skipped, s => s.Key == TestFactory.Table("Customer"));
    }

    [Fact]
    public void Primary_key_change_uses_alter_table_constraint()
    {
        var sql = Script(
            Customer([Idx(1, "PK_Customer", pk: true)], [Key(1, 1, 1)]),
            Customer([Idx(1, "PK_Customer", pk: true)], [Key(1, 2, 1)]));

        Assert.Contains("ALTER TABLE [dbo].[Customer] DROP CONSTRAINT [PK_Customer];", sql);
        Assert.Contains("ALTER TABLE [dbo].[Customer] ADD CONSTRAINT [PK_Customer] PRIMARY KEY CLUSTERED ([Id] ASC);", sql);
    }

    [Fact]
    public void Added_unique_constraint_is_written_as_constraint()
    {
        var sql = Script(Customer([Idx(2, "UQ_Customer_Email", uq: true)], [Key(2, 3, 1)]), Customer());

        Assert.Contains("ALTER TABLE [dbo].[Customer] ADD CONSTRAINT [UQ_Customer_Email] UNIQUE NONCLUSTERED ([Email] ASC);", sql);
    }

    [Fact]
    public void Index_placement_is_kept()
    {
        var sql = Script(Customer([Idx(2, "IX_C", dataSpaceId: 2)], [Key(2, 3, 1)]), Customer());

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_C] ON [dbo].[Customer] ([Email] ASC) ON [IDXFG];", sql);
    }

    // Physical options

    [Fact]
    public void Added_index_with_page_compression_emits_with_clause()
    {
        var sql = Script(Customer([Idx(2, "IX_C", compression: "PAGE")], [Key(2, 3, 1)]), Customer());

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_C] ON [dbo].[Customer] ([Email] ASC) WITH (DATA_COMPRESSION = PAGE);", sql);
    }

    [Fact]
    public void Compression_change_recreates_the_index()
    {
        var sql = Script(
            Customer([Idx(2, "IX_C", compression: "PAGE")], [Key(2, 3, 1)]),
            Customer([Idx(2, "IX_C", compression: "NONE")], [Key(2, 3, 1)]));

        Assert.Contains("DROP INDEX [IX_C] ON [dbo].[Customer];", sql);
        Assert.Contains("WITH (DATA_COMPRESSION = PAGE)", sql);
    }

    [Fact]
    public void Primary_key_with_page_compression_emits_with_clause()
    {
        var sql = Script(Customer([Idx(1, "PK_Customer", pk: true, compression: "PAGE")], [Key(1, 1, 1)]), Customer());

        Assert.Contains("ADD CONSTRAINT [PK_Customer] PRIMARY KEY CLUSTERED ([Id] ASC) WITH (DATA_COMPRESSION = PAGE);", sql);
    }

    [Fact]
    public void Compression_and_lock_options_share_one_with_clause()
    {
        // Two separate WITH (...) lists would be invalid T-SQL.
        var sql = Script(Customer([Idx(2, "IX_A", compression: "PAGE", pageLocks: false)], [Key(2, 1, 1)]), Customer());

        Assert.Contains("WITH (DATA_COMPRESSION = PAGE, ALLOW_PAGE_LOCKS = OFF);", sql);
    }

    [Fact]
    public void Lock_option_difference_recreates_the_index()
    {
        var sql = Script(
            Customer([Idx(2, "IX_A", pageLocks: false)], [Key(2, 1, 1)]),
            Customer([Idx(2, "IX_A")], [Key(2, 1, 1)]));

        Assert.Contains("DROP INDEX [IX_A] ON [dbo].[Customer];", sql);
        Assert.Contains("WITH (ALLOW_PAGE_LOCKS = OFF);", sql);
    }

    [Fact]
    public void Sequential_key_and_norecompute_share_one_with_clause()
    {
        var sql = Script(
            Customer([Idx(2, "IX_A")], [Key(2, 2, 1)], extras: [new IndexExtraRow(CustomerId, 2, true, true)]),
            Customer());

        Assert.Contains("WITH (STATISTICS_NORECOMPUTE = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);", sql);
    }

    [Fact]
    public void Default_index_options_are_not_written()
    {
        var sql = Script(Customer([Idx(2, "IX_A")], [Key(2, 2, 1)]), Customer());

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_A]", sql);
        Assert.DoesNotContain("WITH (", sql);
    }

    // Columnstore

    [Fact]
    public void Plain_columnstore_is_not_written_as_explicit_compression()
    {
        var columnstore = new IndexRow(CustomerId, 2, "NCCI_C", "NONCLUSTERED COLUMNSTORE", false, false, false, 0, false, false, null, "COLUMNSTORE");

        var sql = Script(Customer([columnstore], [Key(2, 3, 0)]), Customer());

        Assert.DoesNotContain("DATA_COMPRESSION = COLUMNSTORE", sql);
    }

    [Fact]
    public void Clustered_columnstore_create_has_no_column_list()
    {
        // A clustered columnstore index covers the whole table. Lock options are reported as off
        // for columnstore and are not valid there either.
        var index = new IndexRow(CustomerId, 2, "CCI_Customer", "CLUSTERED COLUMNSTORE", false, false, false, 0, false, false, null, null, false, false);

        var sql = Script(Customer([index]), Customer());

        Assert.Contains("CREATE CLUSTERED COLUMNSTORE INDEX [CCI_Customer] ON [dbo].[Customer];", sql);
        Assert.DoesNotContain("[CCI_Customer] ON [dbo].[Customer] (", sql);
        Assert.DoesNotContain("ALLOW_ROW_LOCKS", sql);
    }

    [Fact]
    public void Nonclustered_columnstore_create_lists_columns()
    {
        var index = new IndexRow(CustomerId, 2, "NCCI_Customer", "NONCLUSTERED COLUMNSTORE", false, false, false, 0, false, false, null);

        var sql = Script(Customer([index], [Key(2, 3, 0)]), Customer());

        Assert.Contains("CREATE NONCLUSTERED COLUMNSTORE INDEX [NCCI_Customer] ON [dbo].[Customer] ([Email]);", sql);
    }

    // Check constraints

    [Fact]
    public void Added_check_constraint_is_emitted()
    {
        var sql = Script(Customer(checks: [new CheckConstraintRow(CustomerId, "CK_Positive", false, "([Id]>(0))", false, false)]), Customer());

        Assert.Contains("ALTER TABLE [dbo].[Customer] WITH CHECK ADD CONSTRAINT [CK_Positive] CHECK ([Id]>(0));", sql);
    }

    [Fact]
    public void Changed_check_constraint_is_dropped_and_readded()
    {
        var sql = Script(
            Customer(checks: [new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(10))", false, false)]),
            Customer(checks: [new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(0))", false, false)]));

        Assert.Contains("ALTER TABLE [dbo].[Customer] DROP CONSTRAINT [CK_X];", sql);
        Assert.Contains("ADD CONSTRAINT [CK_X] CHECK ([Id]>(10));", sql);
    }

    [Fact]
    public void Check_replication_flag_change_recreates_it()
    {
        var sql = Script(
            Customer(checks: [new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(0))", false, false, true)]),
            Customer(checks: [new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(0))", false, false)]));

        Assert.Contains("DROP CONSTRAINT [CK_X];", sql);
        Assert.Contains("ADD CONSTRAINT [CK_X] CHECK NOT FOR REPLICATION ([Id]>(0));", sql);
    }

    [Fact]
    public void New_constraints_are_added_with_nocheck_when_validation_is_off()
    {
        var script = Generate(
            Customer(checks: [new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(0))", false, false)]),
            Customer(),
            new TableScriptOptions { ValidateNewConstraints = false });

        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [CK_X]", script.Sql);
    }

    // Defaults on existing columns

    [Fact]
    public void Default_added_to_existing_column_emits_add_constraint()
    {
        var key = TestFactory.Table("T");
        var script = TableScriptGenerator.Generate(FixtureCompare.Compare(
            TestFactory.Database("dev", [TestFactory.Obj(key, 1,
                columns: [TestFactory.Column("Amount", defaultDefinition: "((0))", defaultName: "DF_T_Amount")])]),
            TestFactory.Database("prod", [TestFactory.Obj(key, 2, columns: [TestFactory.Column("Amount")], rowCount: 10)])));

        Assert.Contains("ALTER TABLE [dbo].[T] ADD CONSTRAINT [DF_T_Amount] DEFAULT ((0)) FOR [Amount];", script.Sql);
        Assert.Contains(key, script.Included);
    }

    [Fact]
    public void Default_removed_from_existing_column_emits_drop_constraint()
    {
        var key = TestFactory.Table("T");
        var script = TableScriptGenerator.Generate(FixtureCompare.Compare(
            TestFactory.Database("dev", [TestFactory.Obj(key, 1, columns: [TestFactory.Column("Amount")])]),
            TestFactory.Database("prod", [TestFactory.Obj(key, 2,
                columns: [TestFactory.Column("Amount", defaultDefinition: "((0))", defaultName: "DF_T_Amount")], rowCount: 10)])));

        Assert.Contains("ALTER TABLE [dbo].[T] DROP CONSTRAINT [DF_T_Amount];", script.Sql);
    }

    [Fact]
    public void Changed_default_drops_old_and_adds_new()
    {
        var key = TestFactory.Table("T");
        var script = TableScriptGenerator.Generate(FixtureCompare.Compare(
            TestFactory.Database("dev", [TestFactory.Obj(key, 1,
                columns: [TestFactory.Column("Amount", defaultDefinition: "((100))", defaultName: "DF_new")])]),
            TestFactory.Database("prod", [TestFactory.Obj(key, 2,
                columns: [TestFactory.Column("Amount", defaultDefinition: "((0))", defaultName: "DF_old")], rowCount: 10)])));

        Assert.Contains("DROP CONSTRAINT [DF_old];", script.Sql);
        Assert.Contains("ADD CONSTRAINT [DF_new] DEFAULT ((100)) FOR [Amount];", script.Sql);
        Assert.True(At(script.Sql, "DROP CONSTRAINT [DF_old]") < At(script.Sql, "ADD CONSTRAINT [DF_new]"));
    }

    [Fact]
    public void System_named_default_is_added_without_a_name()
    {
        var key = TestFactory.Table("T");
        var script = TableScriptGenerator.Generate(FixtureCompare.Compare(
            TestFactory.Database("dev", [TestFactory.Obj(key, 1, columns:
                [TestFactory.Column("Flag", defaultDefinition: "((1))", defaultName: "DF__T__Flag__ABC", defaultIsSystemNamed: true)])]),
            TestFactory.Database("prod", [TestFactory.Obj(key, 2, columns: [TestFactory.Column("Flag")], rowCount: 10)])));

        Assert.Contains("ALTER TABLE [dbo].[T] ADD DEFAULT ((1)) FOR [Flag];", script.Sql);
        Assert.DoesNotContain("DF__T__Flag__ABC", script.Sql);
    }

    // Foreign keys

    private static CatalogSet WithOrder(bool hasForeignKey, bool withNote = false, byte deleteAction = 0, bool notForReplication = false) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects =
        [
            new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
            new ObjectRow(OrderId, "dbo", "Order", "U", DateTime.UnixEpoch, 0),
        ],
        Columns = withNote
            ? [Col(1, "Id"), Col(1, "CustomerId", OrderId, nullable: false), Col(2, "Note", OrderId)]
            : [Col(1, "Id"), Col(1, "CustomerId", OrderId, nullable: false)],
        ForeignKeys = hasForeignKey
            ? [new ForeignKeyRow(900, OrderId, "FK_Order_Customer", false, CustomerId, deleteAction, 0, false, false, notForReplication)]
            : [],
        ForeignKeyColumns = hasForeignKey ? [new ForeignKeyColumnRow(900, 1, OrderId, 1, CustomerId, 1)] : [],
    };

    [Fact]
    public void Added_foreign_key_is_written_with_validation()
    {
        var script = Generate(WithOrder(true), WithOrder(false));

        Assert.Contains("ALTER TABLE [dbo].[Order] WITH CHECK ADD CONSTRAINT [FK_Order_Customer] FOREIGN KEY ([CustomerId]) " +
                        "REFERENCES [dbo].[Customer] ([Id]);", script.Sql);
        Assert.Contains(TestFactory.Table("Order"), script.Included);
        Assert.DoesNotContain(script.Skipped, s => s.Key == TestFactory.Table("Order"));
    }

    [Fact]
    public void Foreign_key_keeps_referential_actions_and_replication_flag()
    {
        var sql = Script(WithOrder(true, deleteAction: 1, notForReplication: true), WithOrder(false));

        Assert.Contains("REFERENCES [dbo].[Customer] ([Id]) ON DELETE CASCADE NOT FOR REPLICATION;", sql);
    }

    [Fact]
    public void Removed_foreign_key_is_dropped_before_table_changes()
    {
        var sql = Script(WithOrder(false, withNote: true), WithOrder(true));

        var dropAt = At(sql, "ALTER TABLE [dbo].[Order] DROP CONSTRAINT [FK_Order_Customer];");
        var addAt = At(sql, "ADD [Note]");
        Assert.True(dropAt >= 0 && addAt >= 0 && dropAt < addAt, "foreign key drops run before the table changes");
    }

    [Fact]
    public void Foreign_key_add_is_emitted_after_table_changes()
    {
        var sql = Script(WithOrder(true, withNote: true), WithOrder(false));

        var addColumn = At(sql, "ADD [Note]");
        var addForeignKey = At(sql, "ADD CONSTRAINT [FK_Order_Customer]");
        Assert.True(addColumn >= 0 && addForeignKey >= 0 && addColumn < addForeignKey, "foreign key adds run after the table changes");
    }

    [Fact]
    public void Foreign_key_adds_run_after_new_tables_are_created()
    {
        // The changed table gets a foreign key to a brand new table. The add must wait until
        // the new table exists.
        var key = TestFactory.Table("Order");
        var source = TestFactory.Obj(key, 1, columns: [TestFactory.Column("Id"), TestFactory.Column("RegionId")]);
        source.ForeignKeyDefinitions =
            [new ForeignKeyDefinition("FK_Order_Region", false, "dbo", "Region", [new ForeignKeyColumnPair("RegionId", "Id")], 0, 0)];
        var region = TestFactory.Obj(TestFactory.Table("Region"), 3, displayScript: "CREATE TABLE [dbo].[Region] ([Id] INT NOT NULL);");
        var target = TestFactory.Obj(key, 2, columns: [TestFactory.Column("Id"), TestFactory.Column("RegionId")]);

        var sql = TableScriptGenerator.Generate(FixtureCompare.Compare(
            TestFactory.Database("dev", [source, region]), TestFactory.Database("prod", [target]))).Sql;

        Assert.True(At(sql, "CREATE TABLE [dbo].[Region]") < At(sql, "ADD CONSTRAINT [FK_Order_Region]"));
    }
}
