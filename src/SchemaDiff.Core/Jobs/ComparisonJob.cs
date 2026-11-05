using Microsoft.Data.SqlClient;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Jobs;

/// <summary>
/// The unit of a comparison: a named source and target pair.
///
/// There is deliberately no assumption that both sides use the same server or the same database
/// name. Which pairs are compared belongs to the configuration, not to the code.
/// </summary>
public sealed record ComparisonJob(string Name, string SourceConnectionString, string TargetConnectionString)
{
    /// <summary>Printable description of the source. Credentials in the connection string never leak.</summary>
    public string SourceLabel => Describe(SourceConnectionString);

    public string TargetLabel => Describe(TargetConnectionString);

    /// <summary>Returns the connection string with a different database.</summary>
    public static string WithDatabase(string connectionString, string database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;

    /// <summary>Shortcut for the common case of the same database name on two servers.</summary>
    public static ComparisonJob ForDatabase(
        string sourceConnectionString, string targetConnectionString, string database, string? name = null) =>
        new(name ?? database,
            WithDatabase(sourceConnectionString, database),
            WithDatabase(targetConnectionString, database));

    /// <summary>
    /// Only the server and database name are shown. A connection string may contain a password,
    /// so it must never be printed as is.
    /// </summary>
    private static string Describe(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var server = string.IsNullOrEmpty(builder.DataSource) ? "?" : builder.DataSource;
            var database = string.IsNullOrEmpty(builder.InitialCatalog) ? "(default)" : builder.InitialCatalog;
            return $"{server}.{database}";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return "(invalid connection string)";
        }
    }
}

/// <summary>The result of one job, or the reason it could not run.</summary>
public sealed record JobResult(
    ComparisonJob Job,
    CompareResult? Comparison,
    string? Error,
    TimeSpan Duration)
{
    public bool Succeeded => Comparison is not null;

    public string Name => Job.Name;
}
