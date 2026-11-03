using System.Text.RegularExpressions;

namespace SchemaDiff.Core.Execution;

/// <summary>A batch of a script and the line of the script where it starts (1-based).</summary>
public sealed record ScriptBatch(string Sql, int FirstLine);

/// <summary>
/// Splits a script into batches at <c>GO</c> lines. Microsoft.Data.SqlClient does not understand
/// GO, it is a client tool convention (sqlcmd, SSMS), so the executor has to split the script itself.
/// </summary>
public static class ScriptBatches
{
    // GO on its own line, with an optional repeat count and an optional trailing comment.
    // A GO inside a string or a comment is not split, because generated scripts always put GO on its own line.
    private static readonly Regex GoLine = new(
        @"^[ \t]*GO[ \t]*(\d+)?[ \t]*(?:--[^\n]*)?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Splits the script into batches. Empty batches are skipped and "GO n" repeats a batch n times.</summary>
    public static List<string> Split(string sql) => SplitWithLines(sql).Select(b => b.Sql).ToList();

    /// <summary>Like <see cref="Split"/>, but also returns the first script line of every batch so
    /// error line numbers reported by SQL Server can be mapped back to the whole script.</summary>
    public static List<ScriptBatch> SplitWithLines(string sql)
    {
        // Normalize line endings so the GO pattern works the same for \r\n and \n.
        sql = sql.Replace("\r\n", "\n").Replace("\r", "\n");
        var batches = new List<ScriptBatch>();
        var last = 0;
        foreach (Match m in GoLine.Matches(sql))
        {
            Add(sql[last..m.Index], last, m.Groups[1].Success ? m.Groups[1].Value : null);
            last = Math.Min(sql.Length, m.Index + m.Length + 1);
        }
        Add(sql[last..], last, null);
        return batches;

        void Add(string batch, int offset, string? repeat)
        {
            if (string.IsNullOrWhiteSpace(batch)) return;
            // The batch starts at its first non-blank line, which is where SQL Server counts line 1.
            var leading = batch.Length - batch.TrimStart('\n').Length;
            var firstLine = LineOf(sql, offset + leading);
            var count = repeat is not null && int.TryParse(repeat, out var c) ? Math.Max(1, c) : 1;
            for (var i = 0; i < count; i++) batches.Add(new ScriptBatch(batch[leading..], firstLine));
        }
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
