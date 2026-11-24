using System.Globalization;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Hashing;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// The two sides of a comparison are hashed in separate runs, often on different machines. A hash
/// that depends on the process, the thread schedule or the culture of the machine would report
/// differences that do not exist. These tests pin the hash values and run the builder under
/// conditions that must not matter.
/// </summary>
public class HashStabilityTests
{
    [Fact]
    public void Text_hash_matches_the_pinned_value()
    {
        Assert.Equal("8acb683dc63256cb40a3c18b2659f77d", Hash.ToHex(Hash.Of("col|id|sys.int")));
    }

    [Fact]
    public void Combined_hash_matches_the_pinned_value()
    {
        var combined = Hash.Combine([Hash.Of("columns"), Hash.Of("indexes")]);

        Assert.Equal("ff10f594b7b6edd67c6f0dbe2f673563", Hash.ToHex(combined));
    }

    [Fact]
    public void Object_hash_matches_the_pinned_value()
    {
        var snapshot = new ObjectSnapshot { Key = new ObjectKey("dbo", "Customer", ObjectKind.Table), Hash = UInt128.Zero };
        PartHasher.Set(snapshot, "columns", "col|id|sys.int");
        PartHasher.Set(snapshot, "indexes", "idx|pk|1");
        PartHasher.Seal(snapshot);

        Assert.Equal("3853e4424f16473beb5e5eb0f7ab9bb5", Hash.ToHex(snapshot.Hash));
    }

    [Fact]
    public void Text_is_hashed_as_utf8_so_similar_letters_stay_apart()
    {
        // Dotless and dotted i are different letters. The hash must see the bytes, not a folded form.
        Assert.NotEqual(Hash.Of("col|kısa"), Hash.Of("col|kisa"));
        Assert.Equal(Hash.Of("col|kısa"), Hash.Of("col|kısa"));
    }

    [Fact]
    public void The_culture_of_the_machine_does_not_change_any_hash()
    {
        var invariant = WithCulture(CultureInfo.InvariantCulture, () => Build(Catalog()));
        var turkish = WithCulture(CultureInfo.GetCultureInfo("tr-TR"), () => Build(Catalog()));

        Assert.Equal(Hashes(invariant), Hashes(turkish));
    }

    [Fact]
    public void Turkish_culture_still_folds_identifier_casing()
    {
        // In tr-TR "I".ToLower() is a dotless i. Folding must use the invariant culture, otherwise
        // ID and id would not match on a Turkish machine.
        var upper = WithCulture(CultureInfo.GetCultureInfo("tr-TR"), () => Build(Catalog()));
        var lower = WithCulture(CultureInfo.GetCultureInfo("tr-TR"),
            () => Build(Catalog(c => c.Columns[0] = c.Columns[0] with { Name = "id" })));

        Assert.Equal(Hashes(upper), Hashes(lower));
    }

    [Fact]
    public void Parallel_normalization_gives_the_same_hashes_on_every_run()
    {
        var catalog = Catalog(c =>
        {
            for (var i = 0; i < 200; i++)
            {
                var id = 1000 + i;
                c.Objects.Add(new ObjectRow(id, "dbo", $"Proc{i}", "P", DateTime.UnixEpoch, 0));
                c.Modules.Add(new ModuleRow(id, $"CREATE PROCEDURE dbo.Proc{i} AS SELECT {i} -- note {i}", true, true));
            }
        });

        var first = Hashes(Build(catalog));
        for (var run = 0; run < 5; run++)
            Assert.Equal(first, Hashes(Build(catalog)));
    }

    [Fact]
    public void Every_object_hash_is_the_combination_of_its_parts()
    {
        foreach (var snapshot in Build(Catalog()).Objects.Values)
        {
            Assert.NotEmpty(snapshot.Parts);
            var ordered = snapshot.Parts.OrderBy(p => p.Key, StringComparer.Ordinal);
            Assert.Equal(PartHasher.Combine(ordered), snapshot.Hash);
        }
    }

    [Fact]
    public void Tables_and_modules_get_the_expected_parts()
    {
        var snapshot = Build(Catalog());

        var table = snapshot.Objects[new ObjectKey("dbo", "Invoice", ObjectKind.Table)];
        Assert.Equal(["columns", "indexes"], table.Parts.Keys.Order(StringComparer.Ordinal));

        var proc = snapshot.Objects[new ObjectKey("dbo", "GetInvoice", ObjectKind.Procedure)];
        Assert.Equal(["body", "setOptions"], proc.Parts.Keys.Order(StringComparer.Ordinal));
    }

    private const int InvoiceId = 100;
    private const int ProcId = 200;

    private static ColumnRow Col(int columnId, string name, string type = "int", short maxLength = 4) =>
        new(InvoiceId, columnId, name, "sys", type, maxLength, 10, 0, false, null, false, false,
            null, null, null, null, null, null, null);

    private static CatalogSet Catalog(Action<CatalogSet>? change = null)
    {
        var catalog = new CatalogSet
        {
            ServerName = "TESTSRV",
            DatabaseName = "Billing",
            Schemas = [new SchemaRow(5, "Billing")],
            Objects =
            [
                new ObjectRow(InvoiceId, "dbo", "Invoice", "U", DateTime.UnixEpoch, 0),
                new ObjectRow(ProcId, "dbo", "GetInvoice", "P", DateTime.UnixEpoch, 0),
            ],
            Columns = [Col(1, "ID"), Col(2, "Title", "nvarchar", 200)],
            Indexes = [new IndexRow(InvoiceId, 1, "IX_Invoice_ID", "CLUSTERED", true, false, false, 0, false, false, null)],
            IndexColumns = [new IndexColumnRow(InvoiceId, 1, 1, 1, 1, false, false)],
            Modules = [new ModuleRow(ProcId, "CREATE PROCEDURE dbo.GetInvoice AS SELECT ID, Title FROM dbo.Invoice", true, true)],
        };
        change?.Invoke(catalog);
        return catalog;
    }

    private static DatabaseSnapshot Build(CatalogSet catalog) =>
        SnapshotBuilder.Build(catalog, new ExtractionReport(), SnapshotOptions.Default);

    private static List<string> Hashes(DatabaseSnapshot snapshot) =>
        [.. snapshot.Objects.Values
            .OrderBy(o => o.Key.ToString(), StringComparer.Ordinal)
            .Select(o => $"{o.Key} {Hash.ToHex(o.Hash)}")];

    private static T WithCulture<T>(CultureInfo culture, Func<T> action)
    {
        var current = CultureInfo.CurrentCulture;
        var currentUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
            CultureInfo.CurrentUICulture = currentUi;
        }
    }
}
