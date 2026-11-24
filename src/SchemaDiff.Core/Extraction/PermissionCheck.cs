using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Extraction;

/// <summary>
/// Finds metadata that could not be read. SQL Server does not raise an error when a permission is
/// missing. Without VIEW DEFINITION the definition columns of sys.sql_modules, sys.check_constraints,
/// sys.default_constraints and sys.computed_columns simply return NULL. On an older version a catalog
/// view may not exist, which only fails the optional query that reads it.
/// The rule everywhere is the same: what could not be read is reported as uncertain, never as equal or empty.
/// </summary>
internal static class PermissionCheck
{
    /// <summary>
    /// Without VIEW DEFINITION, sys.sql_modules.definition silently returns NULL and every module
    /// looks "equal". That is the most dangerous wrong answer this tool could give.
    /// </summary>
    public static void VerifyDefinitionsVisible(CatalogSet catalog, ExtractionReport report)
    {
        var nullDefinitions = catalog.Modules.Count(m => m.Definition is null);
        if (nullDefinitions > 0 && nullDefinitions == catalog.Modules.Count)
        {
            report.Warnings.Add(
                $"CRITICAL: the definition is NULL for all {catalog.Modules.Count} modules. " +
                "VIEW DEFINITION is missing and the comparison result cannot be trusted.");
        }
        else if (nullDefinitions > 0)
        {
            report.Warnings.Add(
                $"WARNING: the definition is NULL for {nullDefinitions} of {catalog.Modules.Count} modules " +
                "(encrypted objects or partial permissions). These objects are treated as not comparable.");
        }

        // Checked even when there are no modules. CHECK, DEFAULT and computed column
        // definitions on tables depend on the same permission.
        if (!catalog.HasViewDefinition)
        {
            report.Warnings.Add("The connected user does not appear to have VIEW DEFINITION at database level.");
        }
    }

    /// <summary>
    /// Records an optional query that could not run. Its name goes to <see cref="ExtractionReport.FailedQueries"/>
    /// so later steps can tell "could not read" apart from "there is none". Only the first line of the
    /// error is kept because SqlException messages can carry long stack details.
    /// </summary>
    public static void RecordOptionalFailure(ExtractionReport report, string query, Exception error)
    {
        report.FailedQueries.Add(query);
        report.Warnings.Add(
            $"Query '{query}' could not run ({error.Message.Split('\n')[0].Trim()}). " +
            "The comparison continues, but analyses that rely on this data will be incomplete.");
    }

    /// <summary>
    /// Returns why the definition of a table cannot be trusted, or null when everything was readable.
    /// A CHECK, DEFAULT or computed column whose text is NULL would otherwise drop out of the canonical
    /// text. Two such tables would look equal, and a readable one would look like it lost the constraint.
    /// </summary>
    public static string? UnreadableTableReason(
        IReadOnlyList<ColumnRow>? columns, IReadOnlyList<CheckConstraintRow>? checks)
    {
        var hidden = new List<string>();
        foreach (var c in columns ?? [])
        {
            if (c.DefaultName is not null && c.DefaultDefinition is null) hidden.Add($"default {c.DefaultName}");
            if (c.IsComputed && c.ComputedDefinition is null) hidden.Add($"computed column {c.Name}");
        }
        foreach (var check in checks ?? [])
        {
            if (check.Definition is null) hidden.Add($"check {check.Name}");
        }

        return hidden.Count == 0
            ? null
            : $"definition is NULL for {string.Join(", ", hidden)} (VIEW DEFINITION is missing)";
    }
}
