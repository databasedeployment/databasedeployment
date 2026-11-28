using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Core.Tests.TestFactory;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// User-defined alias types and table types. They run before the tables and modules that use
/// them, new ones are created behind a TYPE_ID check, and a type cannot be altered.
/// </summary>
public class TypeScriptGeneratorTests
{
    private static ObjectKey Alias(string name, string schema = "dbo") => new(schema, name, ObjectKind.UserDefinedType);

    private static ObjectKey TableType(string name, string schema = "dbo") => new(schema, name, ObjectKind.TableType);

    private const string MoneyScript = "CREATE TYPE [dbo].[Money] FROM [decimal](19,4) NOT NULL;";
    private const string IdListScript = "CREATE TYPE [dbo].[IdList] AS TABLE (\n    [Id] [int] NOT NULL,\n    [Note] [varchar](50) NULL\n);";

    private static TypeScriptResult Generate(
        DatabaseSnapshot source, DatabaseSnapshot target, TypeScriptOptions? options = null) =>
        TypeScriptGenerator.Generate(FixtureCompare.Compare(source, target), options);

    private static int At(string sql, string text) => sql.IndexOf(text, StringComparison.Ordinal);

    [Fact]
    public void Added_alias_type_is_created_behind_a_type_id_check()
    {
        var script = Generate(Database("dev", [Obj(Alias("Money"), 1, MoneyScript)]), Database("prod", []));

        Assert.Contains("IF TYPE_ID(N'[dbo].[Money]') IS NULL" + Environment.NewLine + "BEGIN", script.Sql);
        Assert.Contains("    " + MoneyScript, script.Sql);
        Assert.Contains("END" + Environment.NewLine + "GO", script.Sql);
        Assert.Contains(Alias("Money"), script.Included);
    }

    [Fact]
    public void Added_table_type_keeps_every_column_line()
    {
        var sql = Generate(Database("dev", [Obj(TableType("IdList"), 1, IdListScript)]), Database("prod", [])).Sql;

        Assert.Contains("    CREATE TYPE [dbo].[IdList] AS TABLE (", sql);
        Assert.Contains("        [Id] [int] NOT NULL,", sql);
        Assert.Contains("        [Note] [varchar](50) NULL", sql);
    }

    [Fact]
    public void Alias_types_are_created_before_table_types()
    {
        var source = Database("dev",
        [
            Obj(TableType("Rows"), 1, "CREATE TYPE [dbo].[Rows] AS TABLE ([Amount] [dbo].[Money] NULL);"),
            Obj(Alias("Money"), 2, MoneyScript),
        ]);

        var sql = Generate(source, Database("prod", [])).Sql;

        Assert.True(At(sql, "[dbo].[Money]") < At(sql, "[dbo].[Rows]"), "a table type can use an alias type, so aliases go first");
    }

    [Fact]
    public void Types_of_the_same_kind_are_created_in_schema_and_name_order()
    {
        var source = Database("dev",
        [
            Obj(Alias("Code", "sales"), 1, "CREATE TYPE [sales].[Code] FROM [varchar](10) NULL;"),
            Obj(Alias("Name"), 2, "CREATE TYPE [dbo].[Name] FROM [nvarchar](100) NULL;"),
            Obj(Alias("Amount"), 3, "CREATE TYPE [dbo].[Amount] FROM [decimal](19,4) NULL;"),
        ]);

        var sql = Generate(source, Database("prod", [])).Sql;

        Assert.True(At(sql, "[dbo].[Amount]") < At(sql, "[dbo].[Name]"));
        Assert.True(At(sql, "[dbo].[Name]") < At(sql, "[sales].[Code]"));
    }

    [Fact]
    public void Changed_alias_type_is_skipped_not_altered()
    {
        var source = Database("dev", [Obj(Alias("Money"), 1, MoneyScript)]);
        var target = Database("prod", [Obj(Alias("Money"), 2, "CREATE TYPE [dbo].[Money] FROM [decimal](10,2) NOT NULL;")]);

        var script = Generate(source, target);

        Assert.Equal(string.Empty, script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == Alias("Money") && s.Reason.Contains("cannot be altered", StringComparison.Ordinal));
    }

    [Fact]
    public void Removed_type_is_not_dropped_by_default()
    {
        var script = Generate(Database("dev", []), Database("prod", [Obj(Alias("Legacy"), 1, "CREATE TYPE [dbo].[Legacy] FROM [int] NULL;")]));

        Assert.DoesNotContain("DROP TYPE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == Alias("Legacy"));
    }

    [Fact]
    public void Removed_type_is_reported_even_when_other_types_are_created()
    {
        var source = Database("dev", [Obj(Alias("Money"), 1, MoneyScript)]);
        var target = Database("prod", [Obj(Alias("Legacy"), 2, "CREATE TYPE [dbo].[Legacy] FROM [int] NULL;")]);

        var script = Generate(source, target);

        Assert.Contains(Alias("Money"), script.Included);
        Assert.Contains(script.Skipped, s => s.Key == Alias("Legacy"));
    }

    [Fact]
    public void Removed_type_is_dropped_when_enabled()
    {
        var script = Generate(Database("dev", []), Database("prod", [Obj(Alias("Legacy"), 1)]),
            new TypeScriptOptions { IncludeDrops = true });

        Assert.Contains("IF TYPE_ID(N'[dbo].[Legacy]') IS NOT NULL", script.Sql);
        Assert.Contains("    DROP TYPE [dbo].[Legacy];", script.Sql);
        Assert.Contains(Alias("Legacy"), script.Included);
    }

    [Fact]
    public void Table_types_are_dropped_before_alias_types()
    {
        var target = Database("prod", [Obj(Alias("Money"), 1), Obj(TableType("Rows"), 2)]);

        var sql = Generate(Database("dev", []), target, new TypeScriptOptions { IncludeDrops = true }).Sql;

        Assert.True(At(sql, "DROP TYPE [dbo].[Rows]") < At(sql, "DROP TYPE [dbo].[Money]"));
    }

    [Fact]
    public void Added_type_without_display_script_is_skipped()
    {
        var script = Generate(Database("dev", [Obj(Alias("Money"), 1)]), Database("prod", []));

        Assert.Equal(string.Empty, script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == Alias("Money"));
    }

    [Fact]
    public void Type_that_could_not_be_compared_is_skipped()
    {
        var source = Database("dev", [Obj(Alias("Money"), 1, MoneyScript, incomparable: true)]);
        var target = Database("prod", [Obj(Alias("Money"), 1, MoneyScript)]);

        var script = Generate(source, target);

        Assert.Empty(script.Included);
        Assert.Contains(script.Skipped, s => s.Key == Alias("Money"));
    }

    [Fact]
    public void Quotes_in_type_names_are_escaped_in_the_check()
    {
        var sql = Generate(Database("dev", [Obj(Alias("O'Type"), 1, "CREATE TYPE [dbo].[O'Type] FROM [int] NULL;")]), Database("prod", [])).Sql;

        Assert.Contains("IF TYPE_ID(N'[dbo].[O''Type]') IS NULL", sql);
    }

    [Fact]
    public void Tables_and_modules_are_left_to_their_own_generators()
    {
        var source = Database("dev",
        [
            Obj(Table("T"), 1, "CREATE TABLE [dbo].[T] ([Id] INT NOT NULL);"),
            Obj(Proc("P"), 2, "CREATE PROCEDURE [dbo].[P] AS SELECT 1"),
        ]);

        var script = Generate(source, Database("prod", []));

        Assert.Equal(string.Empty, script.Sql);
        Assert.True(script.IsEmpty);
    }

    [Fact]
    public void Handled_kinds_are_alias_and_table_types()
    {
        Assert.Equal(
            new HashSet<ObjectKind> { ObjectKind.UserDefinedType, ObjectKind.TableType },
            TypeScriptGenerator.HandledKinds);
    }
}
