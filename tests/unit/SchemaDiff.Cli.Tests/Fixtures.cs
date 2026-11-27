using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Cli.Tests;

/// <summary>Small snapshots and consoles for the command tests. No database is involved.</summary>
internal static class Fixtures
{
    public const string Dev = "Server=dev01;Database=Sales;Integrated Security=true";
    public const string Prod = "Server=prod01;Database=Sales;Integrated Security=true";

    public static ObjectSnapshot Obj(ObjectKey key, UInt128 hash, string? script = null, bool incomparable = false) => new()
    {
        Key = key,
        Hash = hash,
        DisplayScript = script,
        Incomparable = incomparable,
        IncomparableReason = incomparable ? "definition is not readable" : null,
        UsesAnsiNulls = key.Kind.IsModule() ? true : null,
        UsesQuotedIdentifier = key.Kind.IsModule() ? true : null,
    };

    public static DatabaseSnapshot Database(
        string name, IEnumerable<ObjectSnapshot> objects, Dictionary<ObjectKey, List<ObjectKey>>? references = null)
    {
        var dict = new Dictionary<ObjectKey, ObjectSnapshot>(ObjectKeyComparer.CaseInsensitive);
        foreach (var o in objects) dict[o.Key] = o;
        return new DatabaseSnapshot { Server = "srv", Database = name, Objects = dict, References = references ?? [] };
    }

    public static ObjectKey Table(string name) => new("dbo", name, ObjectKind.Table);
    public static ObjectKey View(string name) => new("dbo", name, ObjectKind.View);
    public static ObjectKey Proc(string name) => new("dbo", name, ObjectKind.Procedure);
    public static ObjectKey Schema(string name) => new(name, name, ObjectKind.Schema);

    /// <summary>Sales on dev has a new table and a new view, a changed procedure and drops an old one.</summary>
    public static CompareResult SalesComparison()
    {
        var source = Database("Sales",
        [
            Obj(Table("Customer"), 1, "CREATE TABLE [dbo].[Customer] ([Id] int NOT NULL);"),
            Obj(Table("Invoice"), 2, "CREATE TABLE [dbo].[Invoice] ([Id] int NOT NULL);"),
            Obj(View("OpenInvoices"), 3, "CREATE VIEW dbo.OpenInvoices AS SELECT Id FROM dbo.Invoice"),
            Obj(Proc("GetCustomer"), 4, "CREATE PROCEDURE dbo.GetCustomer AS SELECT 1"),
        ],
        new() { [View("OpenInvoices")] = [Table("Invoice")] });

        var target = Database("Sales",
        [
            Obj(Table("Customer"), 1),
            Obj(Proc("GetCustomer"), 40),
            Obj(Proc("LegacyReport"), 5),
        ]);

        return SchemaComparer.Compare(source, target);
    }

    public static (CliConsole Console, StringWriter Out, StringWriter Error) Console(
        string input = "", bool inputRedirected = true)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        return (new CliConsole(new StringReader(input), output, error, inputRedirected), output, error);
    }
}
