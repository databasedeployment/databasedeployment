using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SchemaDiff.Core.Normalization;

public sealed record NormalizationOptions(
    bool IgnoreWhitespace = true,
    bool IgnoreComments = true,
    bool IgnoreSemicolons = false,
    bool IgnoreKeywordCasing = false,
    // Unify identifier quoting so [a], "a" and a are treated the same while casing is kept.
    // This is semantically safe because brackets do not change identity. On by default.
    bool NormalizeIdentifierQuoting = true,
    // Lowercase identifiers. In a case-insensitive comparison (caseSensitiveNames off) [Foo]
    // and [foo] point to the same object or column, so a body must treat them as equal too.
    // Only applied to identifiers, never to string literals.
    bool FoldIdentifierCase = false)
{
    public static readonly NormalizationOptions Default = new();
}

/// <summary>
/// Reduces T-SQL bodies to a canonical form. It works on the ScriptDom token stream and not
/// with regular expressions, otherwise a '--' or '/*' inside a string literal would be taken
/// for a comment and the body would be corrupted.
/// </summary>
public static class TSqlNormalizer
{
    /// <param name="quotedIdentifiers">The uses_quoted_identifier value of the object in sys.sql_modules.</param>
    public static string Normalize(string? sql, bool quotedIdentifiers = true, NormalizationOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
        options ??= NormalizationOptions.Default;

        var parser = new TSql160Parser(quotedIdentifiers);
        IList<ParseError> errors;
        IList<TSqlParserToken> tokens;
        using (var reader = new StringReader(sql))
        {
            tokens = parser.GetTokenStream(reader, out errors);
        }

        // Fall back to a rough normalization instead of corrupting a body that cannot be tokenized.
        if (errors.Count > 0 || tokens.Count == 0) return FallbackNormalize(sql);

        var sb = new StringBuilder(sql.Length);

        // When whitespace is ignored, a separating space is only needed between two "word"
        // characters (letter, digit, _, @, #, $), otherwise "SELECT" and "1" would merge into
        // "SELECT1". Space around punctuation and operators carries no meaning and is dropped,
        // so "a, b" equals "a,b", "a >= 1" equals "a>=1", "dbo . T" equals "dbo.T" and "( a )" equals "(a)".
        var pendingSpace = false;
        void Emit(string piece)
        {
            if (piece.Length == 0) return;
            if (pendingSpace && sb.Length > 0 && IsWordChar(sb[^1]) && IsWordChar(piece[0]))
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(piece);
        }

        foreach (var token in tokens)
        {
            switch (token.TokenType)
            {
                case TSqlTokenType.EndOfFile:
                    break;

                case TSqlTokenType.WhiteSpace:
                    if (options.IgnoreWhitespace) pendingSpace = true;   // becomes a separator if needed
                    else { sb.Append(token.Text); pendingSpace = false; }
                    break;

                case TSqlTokenType.SingleLineComment:
                case TSqlTokenType.MultilineComment:
                    // A removed comment may still need a separator, same rule as whitespace.
                    if (options.IgnoreComments) pendingSpace = true;
                    else Emit(token.Text);
                    break;

                case TSqlTokenType.Semicolon:
                    if (!options.IgnoreSemicolons) Emit(token.Text);
                    // when ignored nothing is written, surrounding whitespace already sets pendingSpace
                    break;

                case TSqlTokenType.Identifier:
                {
                    var text = options.FoldIdentifierCase ? token.Text.ToLowerInvariant() : token.Text;
                    Emit(options.NormalizeIdentifierQuoting ? Bracket(text) : text);
                    break;
                }

                case TSqlTokenType.QuotedIdentifier:
                {
                    var inner = Unquote(token.Text);
                    if (options.FoldIdentifierCase) inner = inner.ToLowerInvariant();
                    Emit(options.NormalizeIdentifierQuoting
                        ? Bracket(inner)
                        : (options.FoldIdentifierCase ? token.Text.ToLowerInvariant() : token.Text));
                    break;
                }

                default:   // keywords, operators, commas, literals
                    Emit(options.IgnoreKeywordCasing ? UpperIfKeyword(token) : token.Text);
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Tokens that carry a value (identifiers, literals, variables) are never changed.
    /// 'select' and 'SELECT' are the same thing but 'Ali' and 'ALI' are not.
    /// </summary>
    private static string UpperIfKeyword(TSqlParserToken token)
    {
        if (token.Text is null || token.Text.Length == 0) return token.Text ?? string.Empty;

        var carriesValue = token.TokenType is
            TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier or
            TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral or
            TSqlTokenType.Integer or TSqlTokenType.Numeric or TSqlTokenType.Real or
            TSqlTokenType.Money or TSqlTokenType.HexLiteral or
            TSqlTokenType.Variable or TSqlTokenType.Label;

        if (carriesValue) return token.Text;

        foreach (var ch in token.Text)
            if (!char.IsAsciiLetter(ch) && ch != '_') return token.Text;

        return token.Text.ToUpperInvariant();
    }

    /// <summary>
    /// A "word" character is one that would merge into a single token with its neighbour
    /// (letter, digit, underscore, variable, temp table or money prefix). Only two word
    /// characters need a separating space. Space around punctuation and operators is meaningless.
    /// </summary>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    /// <summary>Brings an identifier to the canonical [name] form and escapes inner ]. Casing is kept.</summary>
    private static string Bracket(string inner) => $"[{inner.Replace("]", "]]")}]";

    /// <summary>Reduces an [a] or "a" quoted identifier to its inner value and resolves escapes.</summary>
    private static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
            return text[1..^1].Replace("]]", "]");
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            return text[1..^1].Replace("\"\"", "\"");
        return text;
    }

    /// <summary>For text that cannot be parsed: collapse line breaks and whitespace.</summary>
    private static string FallbackNormalize(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var lastWasSpace = false;
        foreach (var ch in sql)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
