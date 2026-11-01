using SchemaDiff.Core.Normalization;

namespace SchemaDiff.Core.Tests;

/// <summary>
/// Exercises every case of the "ignore whitespace", "ignore comments" and "ignore semicolons"
/// options. Each row is one case where the two bodies differ only in that dimension.
/// Rule: with the option on both bodies reduce to the same canonical text, with it off they stay different.
/// </summary>
public class NormalizerOptionMatrixTests
{
    private static string N(string sql, NormalizationOptions o) => TSqlNormalizer.Normalize(sql, true, o);

    private static readonly NormalizationOptions WsOn  = NormalizationOptions.Default;                              // IgnoreWhitespace=true
    private static readonly NormalizationOptions WsOff = NormalizationOptions.Default with { IgnoreWhitespace = false };
    private static readonly NormalizationOptions CmtOn  = NormalizationOptions.Default;                             // IgnoreComments=true
    private static readonly NormalizationOptions CmtOff = NormalizationOptions.Default with { IgnoreComments = false };
    private static readonly NormalizationOptions SemiOn  = NormalizationOptions.Default with { IgnoreSemicolons = true };
    private static readonly NormalizationOptions SemiOff = NormalizationOptions.Default;                            // IgnoreSemicolons=false

    // Whitespace

    public static readonly TheoryData<string, string> WhitespacePairs = new()
    {
        { "SELECT 1",                 "SELECT      1" },               // several spaces
        { "SELECT 1",                 "SELECT\t1" },                   // tab
        { "SELECT 1",                 "SELECT\n1" },                   // line break
        { "SELECT 1",                 "SELECT\r\n\t 1" },              // mixed
        { "SELECT a, b FROM t",       "SELECT a,b FROM t" },           // space after the comma
        { "SELECT a, b FROM t",       "SELECT a , b FROM t" },         // space before and after the comma
        { "SELECT a,b,c",             "SELECT a , b , c" },            // several commas
        { "SELECT (a) FROM t",        "SELECT ( a) FROM t" },          // after '('
        { "SELECT (a) FROM t",        "SELECT (a ) FROM t" },          // before ')'
        { "SELECT (a) FROM t",        "SELECT ( a ) FROM t" },         // both sides inside parentheses
        { "WHERE a=1",                "WHERE a = 1" },                 // around '='
        { "WHERE a>=1",               "WHERE a >= 1" },                // around '>='
        { "WHERE a<b",                "WHERE a < b" },                 // around '<'
        { "SELECT a+b",               "SELECT a + b" },                // around '+'
        { "EXEC(@s)",                 "EXEC (@s)" },                   // call parenthesis
        { "dbo.T",                    "dbo . T" },                     // qualifier dot
        { "SELECT 1;",                "SELECT 1 ;" },                  // before ';' (the ';' is kept, the space is not)
    };

    [Theory]
    [MemberData(nameof(WhitespacePairs))]
    public void Whitespace_ignored_makes_pair_equal(string a, string b) =>
        Assert.Equal(N(a, WsOn), N(b, WsOn));

    [Theory]
    [MemberData(nameof(WhitespacePairs))]
    public void Whitespace_significant_keeps_pair_different(string a, string b) =>
        Assert.NotEqual(N(a, WsOff), N(b, WsOff));

    [Fact]
    public void Leading_and_trailing_whitespace_is_always_trimmed()
    {
        // Leading and trailing whitespace of a body never matters, whether whitespace is ignored or not.
        Assert.Equal(N("SELECT 1 AS X", WsOn),  N("  SELECT 1 AS X  ", WsOn));
        Assert.Equal(N("SELECT 1 AS X", WsOff), N("  SELECT 1 AS X  ", WsOff));
    }

    // Comments

    public static readonly TheoryData<string, string> CommentPairs = new()
    {
        { "SELECT 1 AS X",            "SELECT 1 AS X /* note */" },    // trailing block comment
        { "SELECT 1 AS X",            "SELECT 1 AS X -- note\n" },     // trailing line comment
        { "SELECT 1 AS X",            "SELECT /* mid */ 1 AS X" },     // block comment in the middle
        { "SELECT a, b FROM t",       "SELECT a /* c */, b FROM t" },  // comment before a comma
        { "SELECT a FROM t",          "SELECT a FROM t -- end\n" },    // trailing line comment after FROM
        { "SELECT 1",                 "/* head */ SELECT 1" },         // leading comment
        { "SELECT 1 AS X",            "SELECT 1 /* a */ /* b */ AS X" }, // two comments
        { "SELECT 1\nFROM t",         "SELECT 1 -- line\nFROM t" },   // line comment in the middle of a line
        { "SELECT 1 AS X",            "SELECT 1 AS X /* line1\n line2\n line3 */" }, // multi-line block at the end
        { "SELECT a, b FROM t",       "SELECT a,\n/* multi\n   line\n   comment */ b FROM t" }, // multi-line block in the middle
        { "SELECT 1",                 "SELECT 1 /* outer /* inner */ outer */" }, // nested block comment (T-SQL allows it)
        { "SELECT 1 AS X",            "/* head\n multi line */ SELECT 1 AS X" }, // multi-line block at the start
        { "SELECT 1",                 "-- l1\n-- l2\nSELECT 1" },      // consecutive line comments
    };

    [Theory]
    [MemberData(nameof(CommentPairs))]
    public void Comments_ignored_makes_pair_equal(string a, string b) =>
        Assert.Equal(N(a, CmtOn), N(b, CmtOn));

    [Theory]
    [MemberData(nameof(CommentPairs))]
    public void Comments_significant_keeps_pair_different(string a, string b) =>
        Assert.NotEqual(N(a, CmtOff), N(b, CmtOff));

    [Fact]
    public void Comment_marker_inside_string_literal_is_never_stripped()
    {
        // '--' and '/*' inside a string literal are not comments and must not be removed.
        Assert.Contains("-- not", N("SELECT '-- not' AS n", CmtOn));
        Assert.Contains("/* not", N("SELECT '/* not */' AS n", CmtOn));
    }

    // Semicolons

    public static readonly TheoryData<string, string> SemicolonPairs = new()
    {
        { "SELECT 1",                            "SELECT 1;" },                       // trailing ';'
        { "BEGIN SELECT 1 SELECT 2 END",         "BEGIN SELECT 1; SELECT 2; END" },   // between statements
        { "DECLARE @i INT SET @i = 1",           "DECLARE @i INT; SET @i = 1;" },     // declare/set
        { "SELECT 1",                            "SELECT 1;;" },                      // double ';'
        { "SELECT 1 SELECT 2",                   "SELECT 1 ; SELECT 2 ;" },           // ';' with spaces
        { "SET NOCOUNT ON SELECT 1",             "SET NOCOUNT ON; SELECT 1" },        // set + select
    };

    [Theory]
    [MemberData(nameof(SemicolonPairs))]
    public void Semicolons_ignored_makes_pair_equal(string a, string b) =>
        Assert.Equal(N(a, SemiOn), N(b, SemiOn));

    [Theory]
    [MemberData(nameof(SemicolonPairs))]
    public void Semicolons_significant_keeps_pair_different(string a, string b) =>
        Assert.NotEqual(N(a, SemiOff), N(b, SemiOff));
}
