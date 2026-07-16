using System.Text;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Compiled details-grid filter. Plain text retains the original single-pattern behavior. A query
/// becomes an expression when it contains a recognized column qualifier or an uppercase boolean
/// operator (AND, OR, NOT).
/// </summary>
public sealed class LibraryFilterQuery
{
    private readonly QueryNode? _root;
    private readonly PatternMatcher? _simpleMatcher;

    private LibraryFilterQuery(QueryNode? root, PatternMatcher? simpleMatcher,
        bool isAdvanced, bool isValid, bool isEmpty, string? error)
    {
        _root = root;
        _simpleMatcher = simpleMatcher;
        IsAdvanced = isAdvanced;
        IsValid = isValid;
        IsEmpty = isEmpty;
        Error = error;
    }

    public bool IsAdvanced { get; }
    public bool IsValid { get; }
    public bool IsEmpty { get; }
    public string? Error { get; }

    public static LibraryFilterQuery Create(string? text, FilterMode mode)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(null, PatternMatcher.Create(null, mode), false, true, true, null);

        IReadOnlyList<QueryToken> tokens;
        try
        {
            tokens = Tokenize(text);
        }
        catch (QueryParseException error)
        {
            return Invalid(isAdvanced: true, error.Message);
        }

        bool advanced = tokens.Any(token =>
            token.Kind is QueryTokenKind.And or QueryTokenKind.Or or QueryTokenKind.Not ||
            token.Kind == QueryTokenKind.Atom &&
            (TrySplitField(token.Text, out _, out _) || HasFieldSyntax(token.Text)));
        if (!advanced)
        {
            PatternMatcher matcher = PatternMatcher.Create(text, mode);
            return new(null, matcher, false, matcher.IsValid, matcher.IsEmpty,
                matcher.IsValid ? null : "Invalid pattern.");
        }

        try
        {
            var parser = new Parser(tokens, mode);
            QueryNode root = parser.Parse();
            return new(root, null, true, parser.IsValid, false,
                parser.IsValid ? null : "One or more query patterns are invalid.");
        }
        catch (QueryParseException error)
        {
            return Invalid(isAdvanced: true, error.Message);
        }
    }

    public bool IsMatch(DetailsRow row, string defaultText)
    {
        if (!IsValid)
            return false;
        if (IsEmpty)
            return true;
        return IsAdvanced
            ? _root!.IsMatch(row, defaultText)
            : _simpleMatcher!.IsMatch(defaultText);
    }

    private static LibraryFilterQuery Invalid(bool isAdvanced, string error) =>
        new(null, null, isAdvanced, false, false, error);

    private static readonly Dictionary<string, string> ColumnAliases = BuildColumnAliases();

    private static Dictionary<string, string> BuildColumnAliases()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DetailsColumn column in DetailsColumns.All)
        {
            result[NormalizeColumnName(column.Key)] = column.Key;
            result[NormalizeColumnName(column.Header)] = column.Key;
        }
        return result;
    }

    private static string NormalizeColumnName(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
            if (char.IsLetterOrDigit(character))
                result.Append(character);
        return result.ToString();
    }

    private static bool TrySplitField(string atom, out string? key, out string value)
    {
        int colon = atom.IndexOf(':');
        if (colon <= 0)
        {
            key = null;
            value = atom;
            return false;
        }

        string alias = NormalizeColumnName(atom[..colon]);
        if (!ColumnAliases.TryGetValue(alias, out key))
        {
            value = atom;
            return false;
        }

        value = atom[(colon + 1)..];
        return true;
    }

    private static bool HasFieldSyntax(string atom)
    {
        int colon = atom.IndexOf(':');
        if (colon <= 1)
            return false; // Preserve drive-qualified path searches such as C:\Music.
        return atom[..colon].All(character =>
            char.IsLetterOrDigit(character) || character is ' ' or '-' or '_');
    }

    private static IReadOnlyList<QueryToken> Tokenize(string text)
    {
        var tokens = new List<QueryToken>();
        int index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;
            if (index == text.Length)
                break;

            if (text[index] == '(')
            {
                tokens.Add(new(QueryTokenKind.LeftParenthesis, "("));
                index++;
                continue;
            }
            if (text[index] == ')')
            {
                tokens.Add(new(QueryTokenKind.RightParenthesis, ")"));
                index++;
                continue;
            }

            var atom = new StringBuilder();
            bool containedQuote = false;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) &&
                   text[index] is not '(' and not ')')
            {
                if (text[index] != '"')
                {
                    atom.Append(text[index++]);
                    continue;
                }

                containedQuote = true;
                index++;
                bool closed = false;
                while (index < text.Length)
                {
                    char character = text[index++];
                    if (character == '"')
                    {
                        closed = true;
                        break;
                    }
                    if (character == '\\' && index < text.Length &&
                        text[index] is '"' or '\\')
                        character = text[index++];
                    atom.Append(character);
                }
                if (!closed)
                    throw new QueryParseException("Unterminated quoted value.");
            }

            if (atom.Length == 0)
                throw new QueryParseException("Expected a filter term.");
            string value = atom.ToString();
            QueryTokenKind kind = !containedQuote
                ? value switch
                {
                    "AND" => QueryTokenKind.And,
                    "OR" => QueryTokenKind.Or,
                    "NOT" => QueryTokenKind.Not,
                    _ => QueryTokenKind.Atom,
                }
                : QueryTokenKind.Atom;
            tokens.Add(new(kind, value));
        }
        tokens.Add(new(QueryTokenKind.End, ""));
        return tokens;
    }

    private sealed class Parser(IReadOnlyList<QueryToken> tokens, FilterMode mode)
    {
        private int _position;
        public bool IsValid { get; private set; } = true;

        public QueryNode Parse()
        {
            QueryNode result = ParseOr();
            if (Current.Kind != QueryTokenKind.End)
                throw new QueryParseException($"Unexpected '{Current.Text}'.");
            return result;
        }

        private QueryNode ParseOr()
        {
            QueryNode left = ParseAnd();
            while (Match(QueryTokenKind.Or))
                left = new OrNode(left, ParseAnd());
            return left;
        }

        private QueryNode ParseAnd()
        {
            QueryNode left = ParseUnary();
            while (true)
            {
                if (Match(QueryTokenKind.And))
                {
                    left = new AndNode(left, ParseUnary());
                    continue;
                }

                // Adjacent terms imply AND: Artist:Miles Codec:FLAC.
                if (Current.Kind is QueryTokenKind.Atom or QueryTokenKind.Not or
                    QueryTokenKind.LeftParenthesis)
                {
                    left = new AndNode(left, ParseUnary());
                    continue;
                }
                return left;
            }
        }

        private QueryNode ParseUnary() =>
            Match(QueryTokenKind.Not) ? new NotNode(ParseUnary()) : ParsePrimary();

        private QueryNode ParsePrimary()
        {
            if (Match(QueryTokenKind.LeftParenthesis))
            {
                QueryNode nested = ParseOr();
                if (!Match(QueryTokenKind.RightParenthesis))
                    throw new QueryParseException("Missing closing parenthesis.");
                return nested;
            }

            if (Current.Kind != QueryTokenKind.Atom)
                throw new QueryParseException(Current.Kind == QueryTokenKind.End
                    ? "Expected a filter term."
                    : $"Unexpected '{Current.Text}'.");

            string atom = Advance().Text;
            string? key = null;
            string pattern = atom;
            if (TrySplitField(atom, out string? parsedKey, out string parsedPattern))
            {
                key = parsedKey;
                pattern = parsedPattern;
                if (pattern.Length == 0 && Current.Kind == QueryTokenKind.Atom)
                    pattern = Advance().Text;
                if (pattern.Length == 0)
                    throw new QueryParseException($"Column '{key}' requires a value.");
            }
            else if (HasFieldSyntax(atom))
            {
                throw new QueryParseException(
                    $"Unknown column '{atom[..atom.IndexOf(':')]}'.");
            }

            PatternMatcher matcher = PatternMatcher.Create(pattern, mode);
            IsValid &= matcher.IsValid;
            return new TermNode(key, matcher);
        }

        private QueryToken Current => tokens[_position];

        private QueryToken Advance() => tokens[_position++];

        private bool Match(QueryTokenKind kind)
        {
            if (Current.Kind != kind)
                return false;
            _position++;
            return true;
        }
    }

    private abstract record QueryNode
    {
        public abstract bool IsMatch(DetailsRow row, string defaultText);
    }

    private sealed record TermNode(string? Key, PatternMatcher Matcher) : QueryNode
    {
        public override bool IsMatch(DetailsRow row, string defaultText) =>
            Matcher.IsMatch(Key is null ? defaultText : row[Key]);
    }

    private sealed record AndNode(QueryNode Left, QueryNode Right) : QueryNode
    {
        public override bool IsMatch(DetailsRow row, string defaultText) =>
            Left.IsMatch(row, defaultText) && Right.IsMatch(row, defaultText);
    }

    private sealed record OrNode(QueryNode Left, QueryNode Right) : QueryNode
    {
        public override bool IsMatch(DetailsRow row, string defaultText) =>
            Left.IsMatch(row, defaultText) || Right.IsMatch(row, defaultText);
    }

    private sealed record NotNode(QueryNode Inner) : QueryNode
    {
        public override bool IsMatch(DetailsRow row, string defaultText) =>
            !Inner.IsMatch(row, defaultText);
    }

    private sealed record QueryToken(QueryTokenKind Kind, string Text);

    private enum QueryTokenKind
    {
        Atom,
        And,
        Or,
        Not,
        LeftParenthesis,
        RightParenthesis,
        End,
    }

    private sealed class QueryParseException(string message) : Exception(message);
}
