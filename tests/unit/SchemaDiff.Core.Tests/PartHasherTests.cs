using SchemaDiff.Core.Hashing;
using SchemaDiff.Core.Model;
using static SchemaDiff.Core.Tests.TestFactory;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Rules for sub-part hashes and the object hash built from them. The comparison trusts these
/// hashes blindly, so each rule is pinned down here on its own.
/// </summary>
public class PartHasherTests
{
    private static ObjectSnapshot Sealed(params (string Part, string Canonical)[] parts)
    {
        var snapshot = Obj(Table("Customer"), UInt128.Zero);
        foreach (var (part, canonical) in parts) PartHasher.Set(snapshot, part, canonical);
        PartHasher.Seal(snapshot);
        return snapshot;
    }

    [Fact]
    public void A_part_stores_its_canonical_text_and_its_hash()
    {
        var snapshot = Sealed(("columns", "col|id|sys.int"));

        Assert.Equal("col|id|sys.int", snapshot.PartCanonical["columns"]);
        Assert.Equal(Hash.Of("col|id|sys.int"), snapshot.Parts["columns"]);
    }

    [Fact]
    public void An_empty_part_is_not_stored()
    {
        var withEmpty = Sealed(("columns", "col|id|sys.int"), ("checks", string.Empty));
        var without = Sealed(("columns", "col|id|sys.int"));

        Assert.False(withEmpty.Parts.ContainsKey("checks"));
        Assert.Equal(without.Hash, withEmpty.Hash);
    }

    [Fact]
    public void The_order_in_which_parts_are_set_does_not_change_the_hash()
    {
        var first = Sealed(("columns", "col|id|sys.int"), ("indexes", "idx|ix_name|1"), ("body", "select 1"));
        var second = Sealed(("body", "select 1"), ("indexes", "idx|ix_name|1"), ("columns", "col|id|sys.int"));

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.Canonical, second.Canonical);
    }

    [Fact]
    public void Changing_one_part_changes_only_that_part_and_the_object_hash()
    {
        var before = Sealed(("columns", "col|id|sys.int"), ("indexes", "idx|ix_name|1"));
        var after = Sealed(("columns", "col|id|sys.bigint"), ("indexes", "idx|ix_name|1"));

        Assert.NotEqual(before.Hash, after.Hash);
        Assert.NotEqual(before.Parts["columns"], after.Parts["columns"]);
        Assert.Equal(before.Parts["indexes"], after.Parts["indexes"]);
    }

    [Fact]
    public void The_same_text_under_another_part_name_is_a_different_object()
    {
        var asChecks = Sealed(("checks", "chk|ck_amount|([amount]>(0))"));
        var asIndexes = Sealed(("indexes", "chk|ck_amount|([amount]>(0))"));

        Assert.NotEqual(asChecks.Hash, asIndexes.Hash);
    }

    [Fact]
    public void Text_cannot_move_between_parts_without_a_change()
    {
        var a = Sealed(("columns", "col|id"), ("indexes", "idx|pk"));
        var b = Sealed(("columns", "idx|pk"), ("indexes", "col|id"));

        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Canonical_text_lists_the_parts_in_name_order()
    {
        var snapshot = Sealed(("indexes", "idx|ix_name|1"), ("columns", "col|id|sys.int"));

        Assert.Equal("-- [columns]\ncol|id|sys.int\n-- [indexes]\nidx|ix_name|1", snapshot.Canonical);
    }

    [Fact]
    public void Folding_names_ignores_name_casing_but_keeps_the_raw_text()
    {
        var upper = Obj(Table("Customer"), UInt128.Zero);
        var lower = Obj(Table("Customer"), UInt128.Zero);

        PartHasher.SetFoldingNames(upper, "indexes", "idx|IX_Customer_Name|name", caseSensitiveNames: false);
        PartHasher.SetFoldingNames(lower, "indexes", "idx|ix_customer_name|name", caseSensitiveNames: false);

        Assert.Equal(upper.Parts["indexes"], lower.Parts["indexes"]);
        Assert.Equal("idx|IX_Customer_Name|name", upper.PartCanonical["indexes"]);
    }

    [Fact]
    public void Folding_names_never_folds_values()
    {
        var a = Obj(Table("Customer"), UInt128.Zero);
        var b = Obj(Table("Customer"), UInt128.Zero);

        PartHasher.SetFoldingNames(a, "checks", "chk|CK_Status|([Status]='A')", caseSensitiveNames: false);
        PartHasher.SetFoldingNames(b, "checks", "chk|CK_Status|([Status]='a')", caseSensitiveNames: false);

        Assert.NotEqual(a.Parts["checks"], b.Parts["checks"]);
    }

    [Fact]
    public void Case_sensitive_names_keep_name_casing_in_the_hash()
    {
        var upper = Obj(Table("Customer"), UInt128.Zero);
        var lower = Obj(Table("Customer"), UInt128.Zero);

        PartHasher.SetFoldingNames(upper, "indexes", "idx|IX_Name|name", caseSensitiveNames: true);
        PartHasher.SetFoldingNames(lower, "indexes", "idx|ix_name|name", caseSensitiveNames: true);

        Assert.NotEqual(upper.Parts["indexes"], lower.Parts["indexes"]);
    }

    [Theory]
    [InlineData("idx|IX_A|name\nidx|IX_B|id", "idx|ix_a|name\nidx|ix_b|id")]
    [InlineData("single", "single")]
    [InlineData("idx||name", "idx||name")]
    public void Only_the_name_field_of_each_line_is_lowercased(string canonical, string expected)
    {
        Assert.Equal(expected, PartHasher.FoldLineNames(canonical));
    }
}
