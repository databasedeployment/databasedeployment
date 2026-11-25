using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using static SchemaDiff.Core.Tests.TestFactory;

namespace SchemaDiff.Core.Tests;

public class SchemaComparerTests
{
    [Fact]
    public void Object_only_in_source_is_Added()
    {
        // Source is what the schema should look like (dev), target is what gets deployed to (prod).
        // In the source but not in the target means it will be created on the target.
        var source = Database("dev", [Obj(View("NewView"), 1)]);
        var target = Database("prod", []);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Added, diff.Kind);
        Assert.Equal("NewView", diff.Key.Name);
    }

    [Fact]
    public void Object_only_in_target_is_Removed()
    {
        var source = Database("dev", []);
        var target = Database("prod", [Obj(Proc("LegacyProc"), 1)]);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Removed, diff.Kind);
        Assert.Equal("LegacyProc", diff.Key.Name);
    }

    [Fact]
    public void Equal_hash_produces_no_difference_and_counts_as_equal()
    {
        var key = View("SameView");
        var source = Database("dev", [Obj(key, 42)]);
        var target = Database("prod", [Obj(key, 42)]);

        var result = SchemaComparer.Compare(source, target);

        Assert.Empty(result.Differences);
        Assert.Equal(1, result.EqualCount);
    }

    [Fact]
    public void Different_hash_produces_Changed()
    {
        var key = Proc("P");
        var source = Database("dev", [Obj(key, 1)]);
        var target = Database("prod", [Obj(key, 2)]);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
    }

    [Fact]
    public void Same_name_with_a_different_kind_is_two_objects()
    {
        var source = Database("dev", [Obj(View("Report"), 1)]);
        var target = Database("prod", [Obj(Proc("Report"), 1)]);

        var result = SchemaComparer.Compare(source, target);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.EqualCount);
    }

    [Fact]
    public void Incomparable_object_is_Indeterminate_not_Equal()
    {
        // A silent "equal" is the most dangerous result. An unreadable object is reported on its own.
        var key = Proc("Encrypted");
        var source = Database("dev", [Obj(key, 1, incomparable: true)]);
        var target = Database("prod", [Obj(key, 1, incomparable: true)]);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Indeterminate, diff.Kind);
        Assert.Equal(0, result.EqualCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void One_unreadable_side_is_enough_to_make_it_Indeterminate(bool sourceUnreadable, bool targetUnreadable)
    {
        var key = View("Secret");
        var source = Database("dev", [Obj(key, 7, incomparable: sourceUnreadable)]);
        var target = Database("prod", [Obj(key, 7, incomparable: targetUnreadable)]);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Indeterminate, diff.Kind);
        Assert.Equal(["definition is not readable"], diff.ChangedParts);
    }

    [Fact]
    public void Indeterminate_objects_are_counted_in_the_total()
    {
        var source = Database("dev", [Obj(Proc("Encrypted"), 1, incomparable: true), Obj(View("V"), 2)]);
        var target = Database("prod", [Obj(Proc("Encrypted"), 1), Obj(View("V"), 2)]);

        var result = SchemaComparer.Compare(source, target);

        Assert.Equal(1, result.IndeterminateCount);
        Assert.Equal(1, result.EqualCount);
        Assert.Equal(2, result.TotalCompared());
    }

    [Fact]
    public void Changed_parts_are_reported_from_subpart_hashes()
    {
        var key = Table("T");
        var src = Obj(key, 1);
        src.Parts["columns"] = 10;
        src.Parts["indexes"] = 20;
        var tgt = Obj(key, 2);
        tgt.Parts["columns"] = 10;   // same
        tgt.Parts["indexes"] = 99;   // different

        var source = Database("dev", [src]);
        var target = Database("prod", [tgt]);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Equal(["indexes"], diff.ChangedParts);
    }

    [Fact]
    public void A_part_present_on_one_side_only_is_a_changed_part()
    {
        var key = Table("T");
        var src = Obj(key, 1);
        src.Parts["columns"] = 10;
        src.Parts["checks"] = 30;
        var tgt = Obj(key, 2);
        tgt.Parts["columns"] = 10;
        tgt.Parts["foreignKeys"] = 40;

        var result = SchemaComparer.Compare(Database("dev", [src]), Database("prod", [tgt]));

        var diff = Assert.Single(result.Differences);
        Assert.Equal(["checks", "foreignKeys"], diff.ChangedParts);
    }

    [Fact]
    public void Name_casing_is_ignored_by_default()
    {
        var source = Database("dev", [Obj(Proc("GetCustomer"), 5)]);
        var target = Database("prod", [Obj(Proc("getcustomer"), 5)]);

        var result = SchemaComparer.Compare(source, target);

        Assert.Empty(result.Differences);
        Assert.Equal(1, result.EqualCount);
    }

    [Fact]
    public void Name_casing_is_a_change_with_case_sensitive_names()
    {
        var source = Database("dev", [Obj(Proc("GetCustomer"), 5)], caseSensitive: true);
        var target = Database("prod", [Obj(Proc("getcustomer"), 5)], caseSensitive: true);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Equal(["name"], diff.ChangedParts);
    }

    [Fact]
    public void Differences_are_grouped_by_kind_added_before_removed()
    {
        var source = Database("dev", [Obj(View("A"), 1)]);
        var target = Database("prod", [Obj(Proc("Z"), 1)]);

        var result = SchemaComparer.Compare(source, target);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        // Added comes before Removed in the DiffKind order.
        Assert.Equal(DiffKind.Added, result.Differences[0].Kind);
    }

    [Fact]
    public void Differences_of_one_kind_are_sorted_by_object_type_schema_and_name()
    {
        var source = Database("dev",
        [
            Obj(View("b"), 1), Obj(Table("Z"), 1), Obj(View("A"), 1), Obj(View("C", schema: "app"), 1),
        ]);

        var result = SchemaComparer.Compare(source, Database("prod", []));

        Assert.Equal(
            ["Table [dbo].[Z]", "View [app].[C]", "View [dbo].[A]", "View [dbo].[b]"],
            result.Differences.Select(d => d.Key.ToString()));
    }

    [Fact]
    public void The_result_keeps_both_snapshots()
    {
        var source = Database("dev", [Obj(View("A"), 1)]);
        var target = Database("prod", [Obj(View("A"), 1)]);

        var result = SchemaComparer.Compare(source, target);

        Assert.Same(source, result.Source);
        Assert.Same(target, result.Target);
        Assert.True(result.CompareTime >= TimeSpan.Zero);
    }
}
