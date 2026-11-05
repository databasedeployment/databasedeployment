using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SchemaDiff.Core.Execution;

/// <summary>
/// One line of the SSMS style "Messages" tab, or one "Results" grid. Kind is one of
/// info, rows, error, fail, done or result. For result, <see cref="Columns"/> and <see cref="Rows"/>
/// are set and Text is empty. TranslationKey and Variables let the UI show the text in EN or TR.
/// </summary>
public sealed record ExecutionMessage(
    string Kind, string Text, int? Number, int? Level, int? State, int? Line,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<IReadOnlyList<string?>>? Rows = null,
    bool Truncated = false,
    string? TranslationKey = null,
    IReadOnlyDictionary<string, string>? Variables = null)
{
    public static ExecutionMessage App(string kind, string text, string key, Dictionary<string, string>? variables = null) =>
        new(kind, text, null, null, null, null, TranslationKey: key, Variables: variables);

    public static ExecutionMessage CompletionTime()
    {
        var time = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture);
        return App("info", $"Completion time: {time}", "execCompletionTime", new() { ["time"] = time });
    }

    public static ExecutionMessage Info(string text) => new("info", text, null, null, null, null);

    public static ExecutionMessage Simple(string kind, string text) => new(kind, text, null, null, null, null);

    public static ExecutionMessage Result(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows, bool truncated) =>
        new("result", string.Empty, null, null, null, null, columns, rows, truncated);

    /// <param name="firstLine">Script line where the batch starts. SQL Server reports lines relative to the batch.</param>
    public static ExecutionMessage Server(string kind, SqlError error, int firstLine = 1) => new(
        kind,
        error.Message,
        error.Number == 0 ? null : error.Number,
        error.Class,
        error.State,
        error.LineNumber > 0 ? firstLine + error.LineNumber - 1 : null);
}
