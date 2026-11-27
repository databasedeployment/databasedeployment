using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using static SchemaDiff.Cli.Tests.Fixtures;

namespace SchemaDiff.Cli.Tests;

public class MissingObjectScriptTests
{
    [Fact]
    public void Missing_objects_are_created_after_what_they_reference()
    {
        var script = MissingObjectScript.Build(SalesComparison());

        Assert.Equal([Table("Invoice"), View("OpenInvoices")], script.Included);
        Assert.True(script.Sql.IndexOf("CREATE TABLE [dbo].[Invoice]", StringComparison.Ordinal)
                    < script.Sql.IndexOf("CREATE VIEW dbo.OpenInvoices", StringComparison.Ordinal));
    }

    [Fact]
    public void The_script_selects_the_target_database_first()
    {
        var script = MissingObjectScript.Build(SalesComparison());

        Assert.StartsWith("USE [Sales];", script.Sql);
    }

    [Fact]
    public void Every_object_is_its_own_batch_and_modules_keep_their_set_options()
    {
        var sql = MissingObjectScript.Build(SalesComparison()).Sql;

        Assert.Contains("SET ANSI_NULLS ON;\nSET QUOTED_IDENTIFIER ON;\nGO\nCREATE VIEW", sql.ReplaceLineEndings("\n"));
        Assert.Contains("CREATE TABLE [dbo].[Invoice] ([Id] int NOT NULL);\nGO", sql.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("SET ANSI_NULLS ON;\nSET QUOTED_IDENTIFIER ON;\nGO\nCREATE TABLE", sql.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Changed_and_removed_objects_are_named_as_out_of_scope()
    {
        var script = MissingObjectScript.Build(SalesComparison());

        Assert.Equal(["GetCustomer", "LegacyReport"], script.OutOfScope.Select(k => k.Name).Order());
        Assert.DoesNotContain("LegacyReport", script.Sql);
        Assert.DoesNotContain("GetCustomer", script.Sql);
    }

    [Fact]
    public void Objects_without_a_definition_or_an_unreadable_side_are_skipped_by_name()
    {
        var source = Database("Sales",
        [
            Obj(Proc("NoText"), 1),
            Obj(Proc("Encrypted"), 2, incomparable: true),
        ]);
        var target = Database("Sales", [Obj(Proc("Encrypted"), 2, incomparable: true)]);

        var script = MissingObjectScript.Build(SchemaComparer.Compare(source, target));

        Assert.Empty(script.Included);
        Assert.Equal(string.Empty, script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == Proc("NoText") && s.Reason.Contains("not available"));
        Assert.Contains(script.Skipped, s => s.Key == Proc("Encrypted") && s.Reason.Contains("could not be compared"));
    }

    [Fact]
    public void New_schemas_come_before_the_objects_in_them()
    {
        var report = new ObjectKey("report", "Daily", ObjectKind.View);
        var source = Database("Sales",
        [
            Obj(report, 1, "CREATE VIEW report.Daily AS SELECT 1 AS One"),
            Obj(Schema("report"), 2, "CREATE SCHEMA [report];"),
        ]);

        var script = MissingObjectScript.Build(SchemaComparer.Compare(source, Database("Sales", [])));

        Assert.Equal([Schema("report"), report], script.Included);
    }
}
