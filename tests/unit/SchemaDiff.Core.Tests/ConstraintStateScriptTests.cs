using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// State of a constraint: disabled and not trusted. A CHECK that was switched off on purpose in
/// the source must not arrive enabled on the target. The three states are written differently
/// in T-SQL, and a state only change must not drop and recreate the constraint.
/// </summary>
public class ConstraintStateScriptTests
{
    private const int CustomerId = 100;
    private const int OrderId = 200;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int objectId, int id, string name) => new(
        objectId, id, name, "sys", "int", 4, 10, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    private static CheckConstraintRow Check(bool disabled = false, bool notTrusted = false) =>
        new(CustomerId, "CK_Positive", false, "([Id]>(0))", disabled, notTrusted);

    private static ForeignKeyRow ForeignKey(bool disabled = false, bool notTrusted = false) =>
        new(900, OrderId, "FK_Order_Customer", false, CustomerId, 0, 0, disabled, notTrusted);

    private static CatalogSet Tables(List<CheckConstraintRow>? checks = null, List<ForeignKeyRow>? foreignKeys = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects =
        [
            new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
            new ObjectRow(OrderId, "dbo", "Order", "U", DateTime.UnixEpoch, 0),
        ],
        Columns = [Col(CustomerId, 1, "Id"), Col(OrderId, 1, "CustomerId")],
        CheckConstraints = checks ?? [],
        ForeignKeys = foreignKeys ?? [],
        ForeignKeyColumns = foreignKeys is { Count: > 0 } ? [new ForeignKeyColumnRow(900, 1, OrderId, 1, CustomerId, 1)] : [],
    };

    private static string Script(CatalogSet source, CatalogSet target) =>
        TableScriptGenerator.Generate(FixtureCompare.Compare(
            SnapshotBuilder.Build(source, new ExtractionReport(), WithScripts),
            SnapshotBuilder.Build(target, new ExtractionReport(), WithScripts))).Sql;

    private static int At(string sql, string text) => sql.IndexOf(text, StringComparison.Ordinal);

    // Same definition, different state

    [Fact]
    public void Disabling_an_existing_check_uses_nocheck_constraint()
    {
        var sql = Script(Tables(checks: [Check(disabled: true)]), Tables(checks: [Check()]));

        Assert.Contains("ALTER TABLE [dbo].[Customer] NOCHECK CONSTRAINT [CK_Positive];", sql);
    }

    [Fact]
    public void State_only_change_does_not_drop_and_recreate()
    {
        var sql = Script(Tables(checks: [Check(disabled: true)]), Tables(checks: [Check()]));

        Assert.DoesNotContain("DROP CONSTRAINT", sql);
        Assert.DoesNotContain("ADD CONSTRAINT", sql);
    }

    [Fact]
    public void Enabling_a_trusted_check_validates_existing_data()
    {
        var sql = Script(Tables(checks: [Check()]), Tables(checks: [Check(disabled: true, notTrusted: true)]));

        Assert.Contains("ALTER TABLE [dbo].[Customer] WITH CHECK CHECK CONSTRAINT [CK_Positive];", sql);
    }

    [Fact]
    public void Enabling_an_untrusted_check_does_not_validate()
    {
        var sql = Script(Tables(checks: [Check(notTrusted: true)]), Tables(checks: [Check(disabled: true, notTrusted: true)]));

        Assert.Contains("ALTER TABLE [dbo].[Customer] CHECK CONSTRAINT [CK_Positive];", sql);
        Assert.DoesNotContain("WITH CHECK CHECK", sql);
    }

    [Fact]
    public void Trust_only_change_is_scripted()
    {
        var sql = Script(Tables(checks: [Check()]), Tables(checks: [Check(notTrusted: true)]));

        Assert.Contains("WITH CHECK CHECK CONSTRAINT [CK_Positive];", sql);
    }

    [Fact]
    public void Identical_state_produces_no_statement()
    {
        var sql = Script(Tables(checks: [Check(disabled: true)]), Tables(checks: [Check(disabled: true)]));

        Assert.DoesNotContain("CK_Positive", sql);
    }

    // Newly added constraints

    [Fact]
    public void Newly_added_disabled_check_is_added_without_validation_then_disabled()
    {
        // A disabled constraint is often off because existing rows break it, so WITH CHECK would fail.
        var sql = Script(Tables(checks: [Check(disabled: true)]), Tables());

        var add = At(sql, "WITH NOCHECK ADD CONSTRAINT [CK_Positive]");
        var disable = At(sql, "ALTER TABLE [dbo].[Customer] NOCHECK CONSTRAINT [CK_Positive];");
        Assert.True(add >= 0 && disable > add, "the constraint is added first and disabled afterwards");
    }

    [Fact]
    public void Newly_added_untrusted_check_is_added_with_nocheck()
    {
        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [CK_Positive]", Script(Tables(checks: [Check(notTrusted: true)]), Tables()));
    }

    [Fact]
    public void Newly_added_trusted_check_is_validated()
    {
        Assert.Contains("WITH CHECK ADD CONSTRAINT [CK_Positive]", Script(Tables(checks: [Check()]), Tables()));
    }

    // Foreign keys

    [Fact]
    public void Disabling_an_existing_foreign_key_uses_nocheck_constraint()
    {
        var sql = Script(Tables(foreignKeys: [ForeignKey(disabled: true)]), Tables(foreignKeys: [ForeignKey()]));

        Assert.Contains("ALTER TABLE [dbo].[Order] NOCHECK CONSTRAINT [FK_Order_Customer];", sql);
        Assert.DoesNotContain("DROP CONSTRAINT", sql);
    }

    [Fact]
    public void Newly_added_disabled_foreign_key_is_added_then_disabled()
    {
        var sql = Script(Tables(foreignKeys: [ForeignKey(disabled: true)]), Tables());

        var add = At(sql, "ADD CONSTRAINT [FK_Order_Customer]");
        var disable = At(sql, "NOCHECK CONSTRAINT [FK_Order_Customer];");
        Assert.True(add >= 0 && disable > add, "the foreign key is added first and disabled afterwards");
    }

    [Fact]
    public void Untrusted_foreign_key_is_added_without_validation()
    {
        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [FK_Order_Customer]",
            Script(Tables(foreignKeys: [ForeignKey(notTrusted: true)]), Tables()));
    }
}
