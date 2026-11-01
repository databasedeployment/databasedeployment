using SchemaDiff.Core.Normalization;

namespace SchemaDiff.Core.Tests;

public class TSqlNormalizerTests
{
    [Fact]
    public void Collapses_whitespace_between_tokens()
    {
        var a = TSqlNormalizer.Normalize("SELECT   a,\n\t b   FROM  t");
        var b = TSqlNormalizer.Normalize("SELECT a, b FROM t");
        Assert.Equal(b, a);
    }

    [Fact]
    public void Strips_line_and_block_comments_by_default()
    {
        var withComments = TSqlNormalizer.Normalize(
            "SELECT a -- line comment\nFROM t /* block comment */ WHERE a = 1");
        var without = TSqlNormalizer.Normalize("SELECT a FROM t WHERE a = 1");
        Assert.Equal(without, withComments);
    }

    [Fact]
    public void Does_not_treat_dash_dash_inside_string_literal_as_comment()
    {
        // A regex based normalizer would corrupt the body here, the token stream does not.
        var sql = "SELECT '-- not a comment' AS note";
        var normalized = TSqlNormalizer.Normalize(sql);
        Assert.Contains("-- not a comment", normalized);
    }

    [Fact]
    public void Preserves_string_literal_casing_but_not_keyword_casing()
    {
        var options = new NormalizationOptions(IgnoreKeywordCasing: true);
        var a = TSqlNormalizer.Normalize("select 'Ali' from t", options: options);
        var b = TSqlNormalizer.Normalize("SELECT 'Ali' FROM t", options: options);
        Assert.Equal(b, a);
        Assert.Contains("'Ali'", a);
    }

    [Fact]
    public void Keyword_casing_difference_is_significant_when_not_ignored()
    {
        // By default keyword casing is kept, so 'select' and 'SELECT' are different text.
        var a = TSqlNormalizer.Normalize("select a from t");
        var b = TSqlNormalizer.Normalize("SELECT a FROM t");
        Assert.NotEqual(b, a);
    }

    [Fact]
    public void Distinct_identifier_casing_is_never_collapsed()
    {
        var options = new NormalizationOptions(IgnoreKeywordCasing: true);
        var a = TSqlNormalizer.Normalize("SELECT ColumnName FROM t", options: options);
        var b = TSqlNormalizer.Normalize("SELECT columnname FROM t", options: options);
        Assert.NotEqual(b, a);
    }

    [Fact]
    public void Bracketed_and_unbracketed_identifiers_are_equal()
    {
        var a = TSqlNormalizer.Normalize("SELECT [a], [b] FROM [dbo].[t]");
        var b = TSqlNormalizer.Normalize("SELECT a, b FROM dbo.t");
        Assert.Equal(b, a);
    }

    [Fact]
    public void Identifier_casing_still_distinguished_after_bracket_normalization()
    {
        // Brackets are unified but casing never is, the collation may be case-sensitive.
        var a = TSqlNormalizer.Normalize("SELECT [Name] FROM t");
        var b = TSqlNormalizer.Normalize("SELECT name FROM t");
        Assert.NotEqual(b, a);
    }

    [Fact]
    public void Identifier_quoting_normalization_can_be_disabled()
    {
        var options = new NormalizationOptions(NormalizeIdentifierQuoting: false);
        var a = TSqlNormalizer.Normalize("SELECT [a] FROM t", options: options);
        var b = TSqlNormalizer.Normalize("SELECT a FROM t", options: options);
        Assert.NotEqual(b, a);
    }

    [Fact]
    public void Null_or_whitespace_returns_empty()
    {
        Assert.Equal(string.Empty, TSqlNormalizer.Normalize(null));
        Assert.Equal(string.Empty, TSqlNormalizer.Normalize("   \n\t "));
    }

    [Fact]
    public void Unparseable_text_falls_back_without_throwing()
    {
        var garbage = "))) not sql at all ''' /* unterminated";
        var result = TSqlNormalizer.Normalize(garbage);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void Comment_before_semicolon_does_not_leave_stray_space()
    {
        // The separator left before ';' after removing a comment must not break equality.
        var withComment = TSqlNormalizer.Normalize("SELECT 1 AS X /* not */;");
        var without = TSqlNormalizer.Normalize("SELECT 1 AS X;");
        Assert.Equal(without, withComment);
    }

    [Fact]
    public void Whitespace_before_semicolon_is_ignored()
    {
        Assert.Equal(TSqlNormalizer.Normalize("SELECT 1;"), TSqlNormalizer.Normalize("SELECT 1 ;"));
    }

    [Fact]
    public void Comment_before_comma_does_not_leave_stray_space()
    {
        var withComment = TSqlNormalizer.Normalize("SELECT a /* c */, b FROM t");
        var without = TSqlNormalizer.Normalize("SELECT a, b FROM t");
        Assert.Equal(without, withComment);
    }

    [Fact]
    public void Whitespace_after_comma_is_ignored()
    {
        // "a, b" and "a,b" (space after the comma) must be equal.
        Assert.Equal(TSqlNormalizer.Normalize("SELECT a,b FROM t"),
                     TSqlNormalizer.Normalize("SELECT a, b FROM t"));
        // Whitespace after a parenthesis and after ';' carries no meaning either.
        Assert.Equal(TSqlNormalizer.Normalize("SELECT (a) FROM t"),
                     TSqlNormalizer.Normalize("SELECT ( a) FROM t"));
    }

    [Fact]
    public void Fold_identifier_case_makes_case_only_name_difference_equal()
    {
        // In case-insensitive mode a casing difference in a module body must disappear.
        var options = new NormalizationOptions(FoldIdentifierCase: true);
        var a = TSqlNormalizer.Normalize("CREATE PROCEDURE [dbo].[GetCustomer] AS SELECT [Id] FROM [T]", options: options);
        var b = TSqlNormalizer.Normalize("CREATE PROCEDURE [dbo].[getcustomer] AS SELECT [id] FROM [t]", options: options);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Identifier_case_is_significant_by_default_without_fold()
    {
        var a = TSqlNormalizer.Normalize("SELECT [Foo] FROM t");
        var b = TSqlNormalizer.Normalize("SELECT [foo] FROM t");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fold_identifier_case_does_not_touch_string_literals()
    {
        // Folding only applies to identifiers. 'Ali' and 'ali' are still different data.
        var options = new NormalizationOptions(FoldIdentifierCase: true);
        var a = TSqlNormalizer.Normalize("SELECT 'Ali' AS [Name]", options: options);
        var b = TSqlNormalizer.Normalize("SELECT 'ali' AS [Name]", options: options);
        Assert.NotEqual(a, b);
        Assert.Contains("'Ali'", a);
    }
}
