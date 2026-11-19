using System.Globalization;

namespace SchemaDiff.Core.Jobs;

/// <summary>
/// Turns a user selection into a list of jobs. The same expression is used for the interactive
/// prompt and for the command line argument, so a selection made by hand can be copied into a script.
///
/// Accepted forms: "1,3"  "2-5"  "EDWSTG,EDW"  "1,4-6,EDWDM"  "a" / "all" / "*"
/// </summary>
public static class JobSelector
{
    /// <summary>
    /// Invisible characters. When the selection comes through a pipe or a redirected file it may start
    /// with a BOM (U+FEFF), and without removing it "1" cannot be parsed as a number.
    /// </summary>
    private static readonly char[] Invisible = ['﻿', '​', '‎', '‏'];

    public static IReadOnlyList<ComparisonJob> Select(IReadOnlyList<ComparisonJob> jobs, string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return [];

        expression = string.Concat(expression.Where(c => !Invisible.Contains(c)));
        if (string.IsNullOrWhiteSpace(expression)) return [];

        var trimmed = expression.Trim();
        if (trimmed is "a" or "A" or "*" || trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
            return jobs;

        var selected = new HashSet<int>();
        var tokens = trimmed.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var dash = token.IndexOf('-');

            // A "4-6" range only when both sides are numbers. Otherwise it may be a name such as
            // "EDW-ARC", so matching by name takes over.
            if (dash > 0 && dash < token.Length - 1
                && int.TryParse(token[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
                && int.TryParse(token[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
            {
                if (from > to) (from, to) = (to, from);
                for (var i = from; i <= to; i++) selected.Add(RequireIndex(jobs, i, token));
                continue;
            }

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                selected.Add(RequireIndex(jobs, index, token));
                continue;
            }

            var match = jobs
                .Select((job, i) => (job, i))
                .Where(x => string.Equals(x.job.Name, token, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.i)
                .ToList();

            if (match.Count == 0)
                throw new ArgumentException(
                    $"'{token}' is neither a position nor a defined job name. " +
                    $"Valid names: {string.Join(", ", jobs.Select(j => j.Name))}");

            selected.Add(match[0]);
        }

        // Definition order is kept instead of selection order, so report order is predictable.
        return [.. selected.Order().Select(i => jobs[i])];
    }

    private static int RequireIndex(IReadOnlyList<ComparisonJob> jobs, int oneBased, string token)
    {
        if (oneBased < 1 || oneBased > jobs.Count)
            throw new ArgumentException($"'{token}' is not valid: positions go from 1 to {jobs.Count}.");

        return oneBased - 1;
    }
}
