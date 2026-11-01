using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>
/// The single header at the top of the combined deployment script. Section headers do not
/// repeat this information, so the script starts with one short and tidy block.
/// </summary>
public static class DeploymentHeader
{
    /// <param name="runOnDatabase">
    /// Database the script runs on. A <c>USE [db]</c> line is written at the top. When null no
    /// USE is written, for example in a mixed forward and reverse script where each section sets its own.
    /// </param>
    public static string Master(CompareResult result, string? generatedAt, bool allowDataLoss,
        string? runOnDatabase = null)
    {
        // Only select the target database. This prevents running the script against the wrong one,
        // such as master. No description block is written, so the script stays clean for production.
        if (runOnDatabase is null) return string.Empty;
        return UseDatabase(runOnDatabase).TrimEnd() + Environment.NewLine + Environment.NewLine;
    }

    /// <summary>A <c>USE [db]; GO</c> block that selects the database of a section, with the name safely bracketed.</summary>
    public static string UseDatabase(string database)
    {
        var safe = database.Replace("]", "]]");
        return $"USE [{safe}];{Environment.NewLine}GO{Environment.NewLine}";
    }
}
