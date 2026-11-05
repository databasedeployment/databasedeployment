using System.Reflection;
using SchemaDiff.Core.Extraction;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Structural checks on the catalog queries. The queries can only really be verified against a
/// live SQL Server, so everything that can be checked without a server is checked here.
///
/// The most important rule is read-only access. The tool connects to production databases and
/// promises to only read the catalog. If a writing statement ever slipped into a query, the
/// compiler would not notice it. Only this test would.
/// </summary>
public class CatalogQueryTests
{
    /// <summary>All query constants in the Sql class as (name, text).</summary>
    public static TheoryData<string, string> Queries
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var constant in typeof(Sql)
                         .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                         .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string)))
            {
                data.Add(constant.Name, (string)constant.GetRawConstantValue()!);
            }
            return data;
        }
    }

    private static readonly string[] WritingKeywords =
    [
        "INSERT ", "UPDATE ", "DELETE ", "MERGE ", "DROP ", "ALTER ", "CREATE ",
        "TRUNCATE ", "EXEC ", "EXECUTE ", "GRANT ", "REVOKE ", "DENY ", "BACKUP ", "RESTORE ",
    ];

    [Fact]
    public void All_queries_are_discovered()
    {
        // Queries are collected by reflection. Finding none would make every theory below pass silently.
        Assert.True(Queries.Count >= 20, $"fewer queries than expected: {Queries.Count}");
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_is_read_only(string name, string sql)
    {
        Assert.Null(WritingKeywordIn(sql, name));
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE dbo.T;")]
    [InlineData("SELECT name FROM sys.objects; DELETE FROM sys.x;")]
    [InlineData("SELECT 1\nEXEC sp_who")]
    public void Read_only_check_rejects_a_writing_statement(string sql)
    {
        // A check that cannot catch anything is not a check.
        Assert.NotNull(WritingKeywordIn(sql, "sample"));
    }

    /// <summary>Returns an error message when the query contains a writing statement, otherwise null.</summary>
    private static string? WritingKeywordIn(string sql, string name)
    {
        var upper = " " + sql.ToUpperInvariant().Replace('\n', ' ').Replace('\r', ' ');

        foreach (var keyword in WritingKeywords)
            if (upper.Contains(" " + keyword, StringComparison.Ordinal))
                return $"{name}: must be read-only but contains '{keyword.Trim()}'";

        return null;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_selects_and_terminates(string name, string sql)
    {
        var trimmed = sql.Trim();
        Assert.True(trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase), $"{name}: must start with SELECT");
        Assert.True(trimmed.EndsWith(';'), $"{name}: must end with a semicolon");
        // SELECT * depends on column order and the mappers read by ordinal, so it is not allowed.
        Assert.False(trimmed.Contains("SELECT *", StringComparison.OrdinalIgnoreCase), $"{name}: must not use SELECT *");
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_parentheses_are_balanced(string name, string sql)
    {
        var depth = 0;
        foreach (var c in sql)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            Assert.True(depth >= 0, $"{name}: extra closing parenthesis");
        }
        Assert.Equal(0, depth);
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_reads_only_system_catalog(string name, string sql)
    {
        // FROM and JOIN targets must be sys.* only. User tables are never touched.
        foreach (var keyword in new[] { "FROM ", "JOIN " })
        {
            var index = 0;
            while ((index = sql.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var rest = sql[(index + keyword.Length)..].TrimStart();
                index += keyword.Length;
                if (rest.StartsWith('(')) continue; // subquery, its own FROM is checked separately

                Assert.True(rest.StartsWith("sys.", StringComparison.OrdinalIgnoreCase),
                    $"{name}: may only read sys.*, but reads '{rest.Split(' ')[0]}'");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_skips_microsoft_shipped_objects_when_it_reads_objects(string name, string sql)
    {
        // Every query that joins sys.objects must leave out system objects, otherwise the report fills
        // up with Microsoft objects. Queries that do not use sys.objects skip this test.
        if (!sql.Contains("sys.objects", StringComparison.OrdinalIgnoreCase)) return;
        if (name is nameof(Sql.Objects)) return; // has it in its own WHERE, tested below

        Assert.Contains("is_ms_shipped = 0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Objects_query_excludes_system_and_history_tables()
    {
        Assert.Contains("is_ms_shipped = 0", Sql.Objects, StringComparison.Ordinal);
        Assert.Contains("temporal_type = 1", Sql.Objects, StringComparison.Ordinal);
    }
}
