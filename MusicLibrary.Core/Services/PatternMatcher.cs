using System.Text;
using System.Text.RegularExpressions;

namespace MusicLibrary.Core.Services;

public enum FilterMode { Substring, Glob, Regex }

/// <summary>
/// Compiles a user filter (plain substring, glob, or regex) into a predicate for realtime row
/// filtering. Invalid regex/glob compiles to a matcher that matches nothing so the UI can flag it.
/// </summary>
public sealed class PatternMatcher
{
    private readonly Regex? _regex;
    private readonly string? _substring;
    public bool IsValid { get; }
    public bool IsEmpty { get; }

    private PatternMatcher(Regex? regex, string? substring, bool isValid, bool isEmpty)
    {
        _regex = regex;
        _substring = substring;
        IsValid = isValid;
        IsEmpty = isEmpty;
    }

    public static PatternMatcher Create(string? pattern, FilterMode mode)
    {
        if (string.IsNullOrEmpty(pattern))
            return new PatternMatcher(null, null, isValid: true, isEmpty: true);

        try
        {
            return mode switch
            {
                FilterMode.Substring => new PatternMatcher(null, pattern, true, false),
                FilterMode.Glob => new PatternMatcher(
                    new Regex(GlobToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null, true, false),
                _ => new PatternMatcher(
                    new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null, true, false),
            };
        }
        catch (ArgumentException)
        {
            // Malformed regex/glob: valid=false, matches nothing.
            return new PatternMatcher(null, null, isValid: false, isEmpty: false);
        }
    }

    public bool IsMatch(string text)
    {
        if (IsEmpty)
            return true;
        if (!IsValid)
            return false;
        if (_regex is not null)
            return _regex.IsMatch(text);
        return text.Contains(_substring!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Translate a glob (*, ?, [set]) into an anchored regex that can match anywhere.</summary>
    public static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        foreach (var c in glob)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                case '[': sb.Append('['); break;
                case ']': sb.Append(']'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.ToString();
    }
}
