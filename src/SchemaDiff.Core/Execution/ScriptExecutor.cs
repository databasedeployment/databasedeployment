using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SchemaDiff.Core.Execution;

/// <summary>
/// Runs a generated deployment script against the target and reports SSMS style messages: PRINT
/// output, "(N rows affected)" and "Msg ... Level ... State ... Line ..." errors. The script is split
/// at GO lines and the batches run one after another on a single connection, so a
/// <c>BEGIN TRAN ... COMMIT</c> in the script stays valid across batches.
/// </summary>
public static class ScriptExecutor
{
    /// <summary>Result grids are capped so a huge result set cannot freeze the browser.</summary>
    public const int MaxResultRows = 10_000;

    public static async Task RunAsync(string connectionString, string sql, ExecutionRun run, CancellationToken ct)
    {
        var batches = ScriptBatches.SplitWithLines(sql);
        run.SetTotal(batches.Count);
        if (batches.Count == 0)
        {
            run.Push(ExecutionMessage.App("info", "(Nothing to run, the script is empty.)", "execNoStatements"));
            run.Complete(success: true);
            return;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            var current = batches[0];

            // PRINT output and low severity server messages (class 10 or less) arrive here,
            // the same lines SSMS shows in its Messages tab.
            connection.InfoMessage += (_, e) =>
            {
                foreach (SqlError error in e.Errors)
                    run.Push(ExecutionMessage.Server("info", error, current.FirstLine));
            };

            await connection.OpenAsync(ct);

            var producedOutput = false;   // a result set or a "(N rows affected)" line was produced

            foreach (var batch in batches)
            {
                ct.ThrowIfCancellationRequested();
                current = batch;
                await using var command = new SqlCommand(batch.Sql, connection) { CommandTimeout = 0 };  // no limit, like SSMS

                // Without SET NOCOUNT ON every statement reports its "(N rows affected)" line here.
                command.StatementCompleted += (_, e) =>
                {
                    if (e.RecordCount < 0) return;
                    producedOutput = true;
                    run.Push(ExecutionMessage.App("rows",
                        e.RecordCount == 1 ? "(1 row affected)" : $"({e.RecordCount} rows affected)",
                        e.RecordCount == 1 ? "execRowAffected" : "execRowsAffected",
                        new() { ["n"] = e.RecordCount.ToString(CultureInfo.InvariantCulture) }));
                };

                try
                {
                    // ExecuteReader captures result grids and messages, as SSMS does.
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    do
                    {
                        if (reader.FieldCount > 0)
                        {
                            producedOutput = true;
                            run.Push(await ReadResultSetAsync(reader, ct));
                        }
                    }
                    while (await reader.NextResultAsync(ct));
                }
                catch (SqlException ex)
                {
                    // A terminating error is reported in SSMS form (Msg, Level, State, Line) and the run stops.
                    foreach (SqlError error in ex.Errors)
                        run.Push(ExecutionMessage.Server("error", error, batch.FirstLine));
                    run.Push(ExecutionMessage.App("fail",
                        "The run failed. The open transaction was rolled back, earlier committed steps such as schema creation may remain.",
                        "execFailedRollback"));
                    run.Push(ExecutionMessage.CompletionTime());
                    run.Complete(success: false);
                    return;
                }

                run.BatchDone();
            }

            // Like SSMS: without results or row counts, report "Commands completed successfully."
            if (!producedOutput)
                run.Push(ExecutionMessage.App("done", "Commands completed successfully.", "execCompleted"));
            run.Push(ExecutionMessage.CompletionTime());
            run.Complete(success: true);
        }
        catch (OperationCanceledException)
        {
            run.Push(ExecutionMessage.App("fail",
                "The run was stopped. The open transaction was rolled back, earlier committed steps such as schema creation may remain.",
                "execCancelledRollback"));
            run.Push(ExecutionMessage.CompletionTime());
            run.Complete(success: false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            run.Push(ExecutionMessage.Simple("error", ex.Message.Split('\n')[0].Trim()));
            run.Complete(success: false);
        }
    }

    // Reads one result set (the SSMS Results grid) with column names and rows.
    private static async Task<ExecutionMessage> ReadResultSetAsync(SqlDataReader reader, CancellationToken ct)
    {
        var columns = new string[reader.FieldCount];
        for (var c = 0; c < reader.FieldCount; c++)
            columns[c] = reader.GetName(c) is { Length: > 0 } name ? name : "(No column name)";

        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            var row = new string?[reader.FieldCount];
            for (var c = 0; c < reader.FieldCount; c++)
                row[c] = await reader.IsDBNullAsync(c, ct) ? null : Format(reader.GetValue(c));
            rows.Add(row);
            if (rows.Count >= MaxResultRows) { truncated = true; break; }
        }
        return ExecutionMessage.Result(columns, rows, truncated);
    }

    /// <summary>Cell formats used by the SSMS grid: bit as 0 or 1, dates with milliseconds, binary as 0xHEX.</summary>
    internal static string Format(object value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        bool b => b ? "1" : "0",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
