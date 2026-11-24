using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Missing permissions and older servers do not make SQL Server fail. Definitions come back as NULL
/// and optional catalog views are simply not there. These tests pin down that such gaps are reported
/// and that an object we could not read is never treated as equal.
/// </summary>
public class PermissionCheckTests
{
    private const int CustomerId = 100;
    private const int ProcId = 300;
    private const int ViewId = 400;

    private static ColumnRow Col(int columnId, string name, string? defaultName = null,
        string? defaultDefinition = null, bool computed = false, string? computedDefinition = null) =>
        new(CustomerId, columnId, name, "sys", "int", 4, 10, 0, true, null, false, computed,
            defaultName, defaultDefinition, defaultName is null ? null : false,
            computedDefinition, computed ? false : null, null, null);

    private static CatalogSet Catalog(Action<CatalogSet>? change = null)
    {
        var catalog = new CatalogSet
        {
            ServerName = "TESTSRV",
            DatabaseName = "Sales",
            HasViewDefinition = true,
            Objects =
            [
                new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
                new ObjectRow(ProcId, "dbo", "GetCustomer", "P", DateTime.UnixEpoch, 0),
                new ObjectRow(ViewId, "dbo", "ActiveCustomer", "V", DateTime.UnixEpoch, 0),
            ],
            Columns =
            [
                Col(1, "Id"),
                Col(2, "Status", defaultName: "DF_Customer_Status", defaultDefinition: "((1))"),
                Col(3, "Total", computed: true, computedDefinition: "([Id]*(2))"),
            ],
            CheckConstraints = [new CheckConstraintRow(CustomerId, "CK_Customer_Status", false, "([Status]>(0))", false, false)],
            Modules =
            [
                new ModuleRow(ProcId, "CREATE PROCEDURE dbo.GetCustomer AS SELECT Id FROM dbo.Customer", true, true),
                new ModuleRow(ViewId, "CREATE VIEW dbo.ActiveCustomer AS SELECT Id FROM dbo.Customer WHERE Status = 1", true, true),
            ],
        };
        change?.Invoke(catalog);
        return catalog;
    }

    private static ExtractionReport Verify(CatalogSet catalog)
    {
        var report = new ExtractionReport();
        PermissionCheck.VerifyDefinitionsVisible(catalog, report);
        return report;
    }

    private static ObjectSnapshot Customer(CatalogSet catalog, ExtractionReport? report = null) =>
        SnapshotBuilder.Build(catalog, report ?? new ExtractionReport(), SnapshotOptions.Default)
            .Objects[TestFactory.Table("Customer")];

    // VIEW DEFINITION detection

    [Fact]
    public void Readable_definitions_with_permission_give_no_warning()
    {
        Assert.Empty(Verify(Catalog()).Warnings);
    }

    [Fact]
    public void All_module_definitions_null_is_reported_as_critical()
    {
        var report = Verify(Catalog(c => c.Modules = [.. c.Modules.Select(m => m with { Definition = null })]));

        var warning = Assert.Single(report.Warnings);
        Assert.StartsWith("CRITICAL", warning, StringComparison.Ordinal);
        Assert.Contains("VIEW DEFINITION", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Some_module_definitions_null_is_reported_with_the_count()
    {
        var report = Verify(Catalog(c => c.Modules[0] = c.Modules[0] with { Definition = null }));

        var warning = Assert.Single(report.Warnings);
        Assert.Contains("1 of 2 modules", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_database_level_permission_is_reported()
    {
        var report = Verify(Catalog(c => c.HasViewDefinition = false));

        Assert.Contains(report.Warnings, w => w.Contains("VIEW DEFINITION at database level", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_permission_is_reported_even_without_modules()
    {
        var report = Verify(Catalog(c =>
        {
            c.Modules = [];
            c.HasViewDefinition = false;
        }));

        Assert.Single(report.Warnings);
    }

    // Optional queries

    [Fact]
    public void Failed_optional_query_is_recorded_and_warned_once()
    {
        var report = new ExtractionReport();

        PermissionCheck.RecordOptionalFailure(report, "rowCounts",
            new InvalidOperationException("VIEW DATABASE STATE permission denied\nat line 1"));

        Assert.Contains("rowCounts", report.FailedQueries);
        var warning = Assert.Single(report.Warnings);
        Assert.Contains("'rowCounts'", warning, StringComparison.Ordinal);
        Assert.Contains("VIEW DATABASE STATE permission denied", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("at line 1", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_index_extras_are_left_out_instead_of_read_as_off()
    {
        // A 2019 source against a 2016 target. If the failed query counted as "all off",
        // every index with OPTIMIZE_FOR_SEQUENTIAL_KEY would show a false difference.
        var withIndex = Catalog(c =>
        {
            c.Indexes = [new IndexRow(CustomerId, 2, "IX_Status", "NONCLUSTERED", false, false, false, 0, false, false, null)];
            c.IndexColumns = [new IndexColumnRow(CustomerId, 2, 1, 2, 1, false, false)];
            c.IndexExtras = [new IndexExtraRow(CustomerId, 2, true, false)];
        });
        var failed = new ExtractionReport();
        failed.FailedQueries.Add("indexExtras");

        Assert.Contains("seqKey", Customer(withIndex).PartCanonical["indexes"], StringComparison.Ordinal);
        Assert.DoesNotContain("seqKey", Customer(withIndex, failed).PartCanonical["indexes"], StringComparison.Ordinal);
        Assert.Contains(failed.Warnings, w => w.Contains("OPTIMIZE_FOR_SEQUENTIAL_KEY", StringComparison.Ordinal));
    }

    // Unreadable objects become uncertain

    [Fact]
    public void Readable_table_stays_comparable()
    {
        var table = Customer(Catalog());

        Assert.False(table.Incomparable);
        Assert.Null(table.IncomparableReason);
    }

    [Fact]
    public void Module_without_definition_is_uncertain_and_says_why()
    {
        var catalog = Catalog(c => c.Modules[1] = c.Modules[1] with { Definition = null });

        var view = SnapshotBuilder.Build(catalog, new ExtractionReport(), SnapshotOptions.Default)
            .Objects[TestFactory.View("ActiveCustomer")];

        Assert.True(view.Incomparable);
        Assert.Contains("VIEW DEFINITION", view.IncomparableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_with_unreadable_check_definition_is_uncertain()
    {
        var catalog = Catalog(c => c.CheckConstraints[0] = c.CheckConstraints[0] with { Definition = null });

        var table = Customer(catalog);

        Assert.True(table.Incomparable);
        Assert.Contains("CK_Customer_Status", table.IncomparableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_with_unreadable_default_definition_is_uncertain()
    {
        var catalog = Catalog(c => c.Columns[1] = c.Columns[1] with { DefaultDefinition = null });

        var table = Customer(catalog);

        Assert.True(table.Incomparable);
        Assert.Contains("DF_Customer_Status", table.IncomparableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_with_unreadable_computed_column_is_uncertain()
    {
        var catalog = Catalog(c => c.Columns[2] = c.Columns[2] with { ComputedDefinition = null });

        var table = Customer(catalog);

        Assert.True(table.Incomparable);
        Assert.Contains("Total", table.IncomparableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_unreadable_tables_never_share_the_hash_of_a_readable_one()
    {
        // Without the mark both sides would drop the hidden CHECK and look identical to each other,
        // and a readable table would look like it lost the constraint.
        var hidden = Catalog(c => c.CheckConstraints[0] = c.CheckConstraints[0] with { Definition = null });

        Assert.NotEqual(Customer(Catalog()).Hash, Customer(hidden).Hash);
        Assert.True(Customer(hidden).Incomparable);
    }

    [Fact]
    public void Unreadable_tables_are_summarized_in_the_report()
    {
        var report = new ExtractionReport();
        var catalog = Catalog(c => c.CheckConstraints[0] = c.CheckConstraints[0] with { Definition = null });

        SnapshotBuilder.Build(catalog, report, SnapshotOptions.Default);

        Assert.Contains(report.Warnings, w => w.Contains("1 table", StringComparison.Ordinal)
                                              && w.Contains("uncertain", StringComparison.Ordinal));
    }

    // Optional details that feed the hash

    private static ExtractionReport Failed(string query)
    {
        var report = new ExtractionReport();
        report.FailedQueries.Add(query);
        return report;
    }

    private static CatalogSet WithTypedXml() => Catalog(c =>
    {
        c.Columns.Add(Col(4, "Profile") with { TypeName = "xml", MaxLength = -1, XmlCollectionId = 65536 });
        c.XmlSchemaCollections = [new XmlSchemaCollectionRow(65536, "dbo", "ProfileSchema")];
    });

    private static CatalogSet WithPeriod() => Catalog(c =>
    {
        c.Columns.Add(Col(4, "ValidFrom") with { TypeName = "datetime2", GeneratedAlwaysType = 1 });
        c.Columns.Add(Col(5, "ValidTo") with { TypeName = "datetime2", GeneratedAlwaysType = 2 });
        c.Temporal = [new TemporalRow(CustomerId, "dbo", "CustomerHistory", "ValidFrom", "ValidTo")];
    });

    [Fact]
    public void Typed_xml_column_is_uncertain_when_collections_could_not_be_read()
    {
        var catalog = WithTypedXml();
        catalog.XmlSchemaCollections = [];

        var table = Customer(catalog, Failed("xmlSchemaCollections"));

        Assert.True(table.Incomparable);
        Assert.Contains("Profile", table.IncomparableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_xml_column_stays_comparable_when_collections_were_read()
    {
        Assert.False(Customer(WithTypedXml()).Incomparable);
    }

    [Fact]
    public void Failed_collection_query_does_not_touch_tables_without_typed_xml()
    {
        Assert.False(Customer(Catalog(), Failed("xmlSchemaCollections")).Incomparable);
    }

    [Fact]
    public void Period_columns_are_uncertain_when_system_versioning_could_not_be_read()
    {
        var catalog = WithPeriod();
        catalog.Temporal = [];

        var table = Customer(catalog, Failed("temporal"));

        Assert.True(table.Incomparable);
        Assert.Contains("system versioning", table.IncomparableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Temporal_table_stays_comparable_when_system_versioning_was_read()
    {
        var table = Customer(WithPeriod());

        Assert.False(table.Incomparable);
        Assert.Contains("temporal", table.Parts.Keys);
    }

    [Fact]
    public void Every_unreadable_detail_is_listed_in_the_reason()
    {
        var catalog = WithTypedXml();
        catalog.CheckConstraints[0] = catalog.CheckConstraints[0] with { Definition = null };

        var reason = PermissionCheck.UnreadableTableReason(
            catalog.Columns, catalog.CheckConstraints, new HashSet<string> { "xmlSchemaCollections" });

        Assert.NotNull(reason);
        Assert.Contains("CK_Customer_Status", reason, StringComparison.Ordinal);
        Assert.Contains("Profile", reason, StringComparison.Ordinal);
    }
}
