using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Core.Tests.TestFactory;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Rebuilding a changed table type: drop the modules that use it, drop and create the type, then
/// create the modules again. The dangerous part is dropping a procedure that cannot be restored,
/// so the option is off by default and even when on every dependent has to be readable.
/// </summary>
public class TableTypeRecreateTests
{
    private static readonly ObjectKey TypeKey = new("dbo", "IdList", ObjectKind.TableType);
    private static readonly ObjectKey ProcKey = Proc("UseList");
    private static readonly TypeScriptOptions Recreate = new() { RecreateChangedTableTypes = true };

    private const string OldType = "CREATE TYPE [dbo].[IdList] AS TABLE ([Id] INT NULL);";
    private const string NewType = "CREATE TYPE [dbo].[IdList] AS TABLE ([Id] INT NULL, [Name] INT NULL);";
    private const string ProcBody = "CREATE PROCEDURE dbo.UseList @List dbo.IdList READONLY AS SELECT 1";

    private static ObjectSnapshot Type(UInt128 hash, string script, bool withDependent)
    {
        var snapshot = Obj(TypeKey, hash, script);
        snapshot.DependentModules = withDependent ? [ProcKey] : [];
        return snapshot;
    }

    private static DatabaseSnapshot Source(
        bool withDependent = true, bool readableProc = true, bool ansiNulls = true) => Database("dev",
        withDependent
            ? [Type(1, NewType, true), Obj(ProcKey, 5, readableProc ? ProcBody : null, incomparable: !readableProc, ansiNulls: ansiNulls)]
            : [Type(1, NewType, false)]);

    private static DatabaseSnapshot Target(bool withDependent = true) => Database("prod",
        withDependent
            ? [Type(2, OldType, true), Obj(ProcKey, 5, ProcBody)]
            : [Type(2, OldType, false)]);

    private static TypeScriptResult Script(DatabaseSnapshot source, DatabaseSnapshot target, TypeScriptOptions? options = null) =>
        TypeScriptGenerator.Generate(FixtureCompare.Compare(source, target), options);

    private static int At(string sql, string text) => sql.IndexOf(text, StringComparison.Ordinal);

    // Off by default

    [Fact]
    public void Option_is_off_by_default()
    {
        // A script that drops procedures must never be produced without asking.
        Assert.False(TypeScriptOptions.Default.RecreateChangedTableTypes);
    }

    [Fact]
    public void Changed_type_is_skipped_by_default()
    {
        var script = Script(Source(), Target());

        Assert.DoesNotContain("DROP TYPE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == TypeKey);
    }

    [Fact]
    public void Skip_message_points_at_the_option()
    {
        var skip = Assert.Single(Script(Source(), Target()).Skipped, s => s.Key == TypeKey);

        Assert.Contains(nameof(TypeScriptOptions.RecreateChangedTableTypes), skip.Reason, StringComparison.Ordinal);
    }

    // On: full rebuild

    [Fact]
    public void Recreate_drops_the_dependent_then_the_type_then_restores_both()
    {
        var sql = Script(Source(), Target(), Recreate).Sql;

        var dropProc = At(sql, "DROP PROCEDURE [dbo].[UseList];");
        var dropType = At(sql, "DROP TYPE [dbo].[IdList];");
        var createType = At(sql, "CREATE TYPE [dbo].[IdList] AS TABLE");
        var createProc = At(sql, ProcBody);

        Assert.True(dropProc >= 0, "the dependent procedure has to be dropped");
        Assert.True(dropProc < dropType, "the procedure goes before the type");
        Assert.True(dropType < createType, "the type is dropped first and created afterwards");
        Assert.True(createType < createProc, "the procedure comes back after the type exists");
    }

    [Fact]
    public void Recreated_type_carries_the_new_shape()
    {
        Assert.Contains(NewType, Script(Source(), Target(), Recreate).Sql);
    }

    [Fact]
    public void Restored_module_keeps_its_set_options()
    {
        // Without the original SET options the module would quietly behave differently.
        var sql = Script(Source(ansiNulls: false), Target(), Recreate).Sql;

        Assert.Contains("SET ANSI_NULLS OFF;", sql);
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", sql);
        Assert.True(At(sql, "SET ANSI_NULLS OFF;") < At(sql, ProcBody));
    }

    [Fact]
    public void Drops_are_guarded_by_existence_checks()
    {
        var sql = Script(Source(), Target(), Recreate).Sql;

        Assert.Contains("IF OBJECT_ID(N'[dbo].[UseList]') IS NOT NULL", sql);
        Assert.Contains("IF TYPE_ID(N'[dbo].[IdList]') IS NOT NULL", sql);
    }

    [Fact]
    public void Recreated_type_counts_as_included()
    {
        Assert.Contains(TypeKey, Script(Source(), Target(), Recreate).Included);
    }

    [Fact]
    public void Type_without_dependents_is_simply_dropped_and_recreated()
    {
        var sql = Script(Source(withDependent: false), Target(withDependent: false), Recreate).Sql;

        Assert.Contains("DROP TYPE [dbo].[IdList];", sql);
        Assert.Contains(NewType, sql);
        Assert.DoesNotContain("DROP PROCEDURE", sql);
    }

    // Precondition: every dependent can be restored

    [Fact]
    public void Nothing_is_produced_when_a_dependent_cannot_be_restored()
    {
        // An encrypted or unreadable procedure cannot be created again once it is dropped.
        var script = Script(Source(readableProc: false), Target(), Recreate);

        Assert.DoesNotContain("DROP TYPE", script.Sql);
        Assert.DoesNotContain("DROP PROCEDURE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == TypeKey && s.Reason.Contains("cannot be restored", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_dependent_is_named_in_the_reason()
    {
        var skip = Assert.Single(Script(Source(readableProc: false), Target(), Recreate).Skipped, s => s.Key == TypeKey);

        Assert.Contains("UseList", skip.Reason, StringComparison.Ordinal);
    }
}
