using System.Text;
using SchemaDiff.Core.Model;
using static SchemaDiff.Core.Scripting.SqlName;

namespace SchemaDiff.Core.Scripting;

/// <summary>
/// CREATE and DROP STATISTICS text. It lives in one place so every script that touches user
/// statistics writes them the same way.
/// </summary>
internal static class StatisticsScript
{
    /// <param name="qualified">Two part name of the table, <c>[schema].[name]</c>.</param>
    public static string Create(string qualified, StatisticsDefinition statistic)
    {
        var sb = new StringBuilder(128);
        sb.Append("CREATE STATISTICS ").Append(Id(statistic.Name)).Append(" ON ").Append(qualified)
          .Append(" (").Append(string.Join(", ", statistic.Columns.Select(Id))).Append(')');

        if (statistic.FilterDefinition is not null) sb.Append(" WHERE ").Append(statistic.FilterDefinition);

        // The sampling rate is not kept in the catalog, so only the lasting settings are written.
        var with = new List<string>(2);
        if (statistic.NoRecompute) with.Add("NORECOMPUTE");
        if (statistic.IsIncremental) with.Add("INCREMENTAL = ON");
        if (with.Count > 0) sb.Append(" WITH ").Append(string.Join(", ", with));

        return sb.Append(';').ToString();
    }

    /// <summary>DROP STATISTICS names the table in front of the statistic: <c>[schema].[table].[statistic]</c>.</summary>
    public static string Drop(string qualified, string name) => $"DROP STATISTICS {qualified}.{Id(name)};";
}
