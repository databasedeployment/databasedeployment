using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Shortcuts for building model objects in tests. Real snapshots come from the catalog,
/// these helpers let comparison and scripting logic be tested in isolation.
/// </summary>
internal static class TestFactory
{
    public static ObjectKey Table(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.Table);

    public static ObjectKey View(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.View);

    public static ObjectKey Proc(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.Procedure);

    public static ObjectKey Trigger(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.Trigger);

    public static ObjectKey Schema(string name) =>
        new(string.Empty, name, ObjectKind.Schema);
}
