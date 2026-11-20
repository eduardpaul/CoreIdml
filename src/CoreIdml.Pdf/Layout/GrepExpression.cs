using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// An InDesign GREP expression (Boost's Perl syntax plus InDesign's <c>~</c> metacharacters) as a .NET regular
/// expression, for GREP styles (<c>AllGREPStyles</c>).
/// <para>
/// Translated: <c>\x{HHHH}</c>, <c>\u</c> and <c>\l</c> (any upper / lower case letter), <c>\h</c> and <c>\H</c>,
/// <c>\&lt;</c> and <c>\&gt;</c> (start and end of a word), <c>\Q...\E</c>, <c>\K</c> at the top level of an expression
/// without top-level alternatives (as a lookbehind), POSIX classes (<c>[[:alpha:]]</c>, <c>[[:digit:]]</c>, ...),
/// <c>$</c> (end of the paragraph) and the <c>~</c> metacharacters for quotes, dashes, spaces, symbols and markers.
/// The rest of the syntax (groups, lookarounds, classes, quantifiers, inline options such as <c>(?i)</c>,
/// back-references) means the same in both.
/// </para>
/// <para>
/// Not supported, so the expression is skipped: possessive quantifiers (<c>a++</c>), equivalence and collating classes
/// (<c>[[=a=]]</c>), <c>\K</c> inside a group or with top-level alternatives, <c>~</c> metacharacters for variables,
/// breaks and markers CoreIdml does not keep in the text (footnotes, index), and anything .NET cannot parse.
/// </para>
/// Uses the regex interpreter only (no <c>RegexOptions.Compiled</c>, no source generator: expressions come from the
/// document), which is trim, AOT and WebAssembly safe; matching has a timeout.
/// </summary>
internal static class GrepExpression
{
    /// <summary>Longest a GREP style may take on one paragraph; past it the style is not applied there.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex LeadingOptions = new(@"^(?:\(\?[imnsx-]+\))+", RegexOptions.CultureInvariant);

    /// <summary>The expression compiled for matching, or null when it is not supported or not valid.</summary>
    public static Regex? Compile(string expression)
    {
        if (Translate(expression) is not { } pattern)
        {
            return null;
        }
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The .NET pattern for an InDesign GREP expression, or null when it uses something not supported.</summary>
    public static string? Translate(string expression)
    {
        var output = new StringBuilder(expression.Length + 16);
        var inClass = false;
        // Where the current class's members start (after "[" or "[^"): a "]" there is a member, not the end.
        var classStart = 0;
        var depth = 0;
        var topLevelAlternative = false;
        var keepAt = -1;
        // The last token was a quantifier: a "+" after it makes it possessive, a "?" makes it lazy.
        var quantified = false;
        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            var afterQuantifier = quantified;
            quantified = false;
            if (c == '\\')
            {
                if (++i >= expression.Length)
                {
                    return null;
                }
                var next = expression[i];
                switch (next)
                {
                    case 'x' when i + 1 < expression.Length && expression[i + 1] == '{':
                        var close = expression.IndexOf('}', i + 2);
                        if (close < 0 || !int.TryParse(expression.AsSpan(i + 2, close - i - 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                            || code > 0x10FFFF || !AppendCodePoint(output, code, inClass))
                        {
                            return null;
                        }
                        i = close;
                        break;
                    case 'u':
                        output.Append(@"\p{Lu}");
                        break;
                    case 'l':
                        output.Append(@"\p{Ll}");
                        break;
                    case 'h':
                        output.Append(inClass ? @"\t\p{Zs}" : @"[\t\p{Zs}]");
                        break;
                    case 'H' when !inClass:
                        output.Append(@"[^\t\p{Zs}]");
                        break;
                    case '<':
                        output.Append(inClass ? @"\<" : @"\b(?=\w)");
                        break;
                    case '>':
                        output.Append(inClass ? @"\>" : @"\b(?<=\w)");
                        break;
                    case 'Q':
                        var end = expression.IndexOf(@"\E", i + 1, StringComparison.Ordinal);
                        foreach (var literal in end < 0 ? expression[(i + 1)..] : expression[(i + 1)..end])
                        {
                            AppendLiteral(output, literal);
                        }
                        i = end < 0 ? expression.Length : end + 1;
                        break;
                    case 'E':
                        break;
                    case 'K':
                        if (inClass || depth > 0 || keepAt >= 0)
                        {
                            return null;
                        }
                        keepAt = output.Length;
                        break;
                    case 'H' or 'v' or 'V' or 'R' or 'X' or 'N' or 'C' or 'g':
                        // Boost-only classes and escapes: .NET reads some of them differently (\v) or not at all.
                        return null;
                    default:
                        output.Append('\\').Append(next);
                        break;
                }
                continue;
            }
            if (c == '~')
            {
                if (++i >= expression.Length || Metacharacter(expression[i]) is not { } members)
                {
                    return null;
                }
                if (!inClass && members.Length > 1)
                {
                    output.Append('[');
                }
                foreach (var member in members)
                {
                    AppendLiteral(output, member);
                }
                if (!inClass && members.Length > 1)
                {
                    output.Append(']');
                }
                continue;
            }
            if (inClass)
            {
                if (c == '[' && i + 1 < expression.Length && expression[i + 1] is ':' or '=' or '.')
                {
                    var kind = expression[i + 1];
                    var close = expression.IndexOf(kind + "]", i + 2, StringComparison.Ordinal);
                    if (kind != ':' || close < 0 || PosixClass(expression[(i + 2)..close]) is not { } members)
                    {
                        return null;
                    }
                    output.Append(members);
                    i = close + 1;
                }
                else if (c == ']' && output.Length > classStart)
                {
                    inClass = false;
                    output.Append(']');
                }
                else
                {
                    output.Append(c is '[' or ']' ? "\\" + c : c.ToString());
                }
                continue;
            }
            switch (c)
            {
                case '[':
                    inClass = true;
                    output.Append('[');
                    if (i + 1 < expression.Length && expression[i + 1] == '^')
                    {
                        output.Append('^');
                        i++;
                    }
                    classStart = output.Length;
                    break;
                case '(':
                    depth++;
                    output.Append('(');
                    if (i + 1 < expression.Length && expression[i + 1] == '?')
                    {
                        output.Append('?');
                        i++;
                    }
                    break;
                case ')':
                    depth--;
                    output.Append(')');
                    break;
                case '|':
                    topLevelAlternative |= depth == 0;
                    output.Append('|');
                    break;
                case '$':
                    // InDesign's $ is the end of the paragraph, and each paragraph is matched on its own.
                    output.Append(@"\z");
                    break;
                case '+' when afterQuantifier:
                    return null;
                case '?' when afterQuantifier:
                    output.Append('?');
                    break;
                case '*' or '+' or '?':
                    output.Append(c);
                    quantified = true;
                    break;
                case '{' when Repeat(expression, i) is { } repeat:
                    output.Append(repeat);
                    i += repeat.Length - 1;
                    quantified = true;
                    break;
                case '{' or '}':
                    output.Append('\\').Append(c);
                    break;
                default:
                    output.Append(c);
                    break;
            }
        }
        if (inClass || depth != 0)
        {
            return null;
        }
        var pattern = output.ToString();
        if (keepAt < 0)
        {
            return pattern;
        }
        if (topLevelAlternative)
        {
            return null;
        }
        // "a\Kb" matches b after a: a lookbehind, with the expression's leading options (?i) kept outside it.
        var options = LeadingOptions.Match(pattern[..keepAt]).Value;
        return options + "(?<=" + pattern[options.Length..keepAt] + ")" + pattern[keepAt..];
    }

    /// <summary>A counted repeat ("{2}", "{2,}", "{2,5}") starting at <paramref name="start"/>, or null.</summary>
    private static string? Repeat(string expression, int start)
    {
        var close = expression.IndexOf('}', start);
        if (close < 0)
        {
            return null;
        }
        var body = expression.AsSpan(start + 1, close - start - 1);
        var comma = body.IndexOf(',');
        var low = comma < 0 ? body : body[..comma];
        var high = comma < 0 ? [] : body[(comma + 1)..];
        return low.Length > 0 && IsDigits(low) && IsDigits(high) ? expression[start..(close + 1)] : null;

        static bool IsDigits(ReadOnlySpan<char> span)
        {
            foreach (var c in span)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }
            return true;
        }
    }

    private static bool AppendCodePoint(StringBuilder output, int code, bool inClass)
    {
        if (code <= 0xFFFF)
        {
            AppendLiteral(output, (char)code);
            return true;
        }
        // A pair of UTF-16 units cannot be a member of a .NET character class.
        if (inClass)
        {
            return false;
        }
        var text = char.ConvertFromUtf32(code);
        output.Append("(?:");
        AppendLiteral(output, text[0]);
        AppendLiteral(output, text[1]);
        output.Append(')');
        return true;
    }

    /// <summary>One character matched literally, inside or outside a class.</summary>
    private static void AppendLiteral(StringBuilder output, char c)
    {
        if (char.IsAsciiLetterOrDigit(c))
        {
            output.Append(c);
        }
        else
        {
            output.Append(@"\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The members of a POSIX class (<c>[:alpha:]</c>) inside a .NET class, or null for unknown ones.</summary>
    private static string? PosixClass(string name) => name switch
    {
        "alpha" => @"\p{L}",
        "digit" => @"\d",
        "alnum" => @"\p{L}\p{Nd}",
        "upper" => @"\p{Lu}",
        "lower" => @"\p{Ll}",
        "space" => @"\s",
        "blank" => @"\t\p{Zs}",
        "punct" => @"\p{P}",
        "word" => @"\w",
        "xdigit" => "0-9A-Fa-f",
        "cntrl" => @"\p{Cc}",
        _ => null,
    };

    /// <summary>
    /// The characters an InDesign GREP metacharacter (<c>~x</c>) stands for, as CoreIdml keeps them in a paragraph's
    /// text (special characters by their ACE code in IDML: page number 18, section marker 19, indent to here 7,
    /// right indent tab 8, end nested style 3; an anchored object is U+FFFC). Null when not supported.
    /// </summary>
    private static string? Metacharacter(char c) => c switch
    {
        '"' => "\u201C\u201D\"",
        '\'' => "\u2018\u2019'",
        '{' => "\u201C",
        '}' => "\u201D",
        '[' => "\u2018",
        ']' => "\u2019",
        '8' => "\u2022",
        '2' => "\u00A9",
        'e' => "\u2026",
        '7' => "\u00B6",
        'r' => "\u00AE",
        '6' => "\u00A7",
        'd' => "\u2122",
        '_' => "\u2014",
        '=' => "\u2013",
        '~' => "\u2011",
        '-' => "\u00AD",
        'm' => "\u2003",
        '>' => "\u2002",
        'f' => "\u2001",
        '|' => "\u200A",
        'S' => "\u00A0",
        's' => "\u202F",
        '<' => "\u2009",
        '/' => "\u2007",
        '.' => "\u2008",
        '3' => "\u2004",
        '4' => "\u2005",
        '%' => "\u2006",
        'k' => "\u200B",
        'j' => "\u200C",
        '#' or 'N' or 'X' or 'V' => "\u0012",
        'x' => "\u0013",
        'i' => "\u0007",
        'y' => "\u0008",
        'h' => "\u0003",
        'a' => "\uFFFC",
        _ => null,
    };
}
