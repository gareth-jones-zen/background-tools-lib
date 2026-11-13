using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PyCompat;

/// <summary>A pattern Python's re would accept but PyRegex won't run, because .NET would read it differently
/// (or Python would refuse it, so a rollback to the Python version would break).</summary>
public sealed class PyRegexUnsupportedException(string pattern, string why)
    : Exception($"{Py.ReprString(pattern)} uses {why}, which Python and .NET read differently; it isn't supported");

/// <summary>Python's re, on .NET's Regex. A tool's settings hold user-written patterns that its Python version
/// also runs, so a pattern must mean the same in both. Each pattern is translated from Python's dialect (first
/// written for mail-triage's port; rewritten for v0.3, 13 Nov 2026):
/// - every character class, category escape and literal is worked out as the set of code points Python's re
///   matches, from Python's own Unicode tables (PyUnicode.g.cs), and written so that a character beyond U+FFFF
///   (an emoji) is matched whole, as one character, as Python does: ".", \w, \W, \d, \D, \s, \S, [...], [^...];
/// - \b and \B use Python's word characters; \Z becomes \z (and 3.14's \z stays \z);
/// - IGNORECASE is applied as Python's re applies it (_sre's simple lower case and re._casefix's extra cases, so
///   "i" matches "İ" and "ı", "s" matches "ſ", "k" the Kelvin sign, in a pattern or in the text), not by .NET's
///   IgnoreCase;
/// - (?P&lt;name&gt;...) and (?P=name) become .NET's named groups, and PyMatch keeps Python's group numbering
///   (.NET numbers named groups last);
/// - {,n} and {,} are quantifiers, as in Python; global inline flags at the start, (?i) (?m) (?s) (?u), become
///   options; scoped ones, (?i:...) and (?-i:...), apply to their group; (?#...) is a comment; (?&gt;...) is
///   atomic in both.
/// Anything the two read differently, or Python refuses, is refused (the guard): \p{...}, possessive quantifiers,
/// mid-pattern global flags, (?x), (?a), (?L), numbered backreferences and octal escapes, \N{...}, conditional
/// groups, (?P=name) with IGNORECASE, and escapes Python lacks. Interpreted, never compiled: patterns are user
/// input, and Compiled is ignored under Native AOT. Each match has a time limit; Python's has none.
/// <para>Searching (Search, Find, FindIter, FindAll, Sub, Split) steps through the text as Python does: never
/// from the middle of a surrogate pair, and after an empty match a non-empty one may start at the same place
/// (Python 3.7's rule), where .NET's own Matches would move on.</para>
/// <para>Known gaps, both from the two engines rather than the translation: a group inside a repeated group whose
/// iterations can match empty may be reported as None where Python reports "" (found by fuzzing: 3 of 40,000
/// texts); and inside a lookbehind, .NET matches right to left, so a lone surrogate (half an emoji, never in
/// well-formed text) next to one may be read differently.</para></summary>
public sealed class PyRegex
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<(string, bool), PyRegex> Cache = new();

    private readonly Regex search;
    // Built when first needed: a translated pattern can be large (\w alone is several kilobytes).
    private readonly Lazy<Regex> guarded;
    private readonly Lazy<Regex> anchored;
    private readonly Lazy<Regex> nonEmpty;
    private readonly Lazy<Regex> full;
    private readonly string[] groupKeys;   // Python's group i (1-based) is .NET's group groupKeys[i - 1]

    private PyRegex(string pattern, bool ignoreCase)
    {
        Pattern = pattern;
        var t = new Translator(pattern, ignoreCase);
        var (translated, options) = t.Run();
        options |= RegexOptions.CultureInvariant;
        Translated = translated;
        groupKeys = [.. t.GroupKeys];
        GroupIndex = t.Names;
        try
        {
            search = new Regex(translated, options, Timeout);
        }
        catch (ArgumentException error)
        {
            throw new PyRegexUnsupportedException(pattern, $"something .NET can't read ({error.Message})");
        }
        // Never start between the halves of a surrogate pair (used only when the text has a surrogate).
        guarded = Build(@"(?!(?<=[\uD800-\uDBFF])[\uDC00-\uDFFF])(?:" + translated + ")", options);
        anchored = Build(@"\G(?:" + translated + ")", options);
        nonEmpty = Build(@"\G(?:" + translated + @")(?!\G)", options);
        full = Build(@"\G(?:" + translated + @")\z", options);
    }

    private static Lazy<Regex> Build(string pattern, RegexOptions options) => new(() => new Regex(pattern, options, Timeout));

    public string Pattern { get; }

    /// <summary>The .NET pattern it became.</summary>
    public string Translated { get; }

    /// <summary>Python's pattern.groups: the number of capturing groups.</summary>
    public int GroupCount => groupKeys.Length;

    /// <summary>Python's pattern.groupindex: each named group's number.</summary>
    public IReadOnlyDictionary<string, int> GroupIndex { get; }

    /// <summary>The pattern, translated and checked once. Throws PyRegexUnsupportedException.</summary>
    public static PyRegex Get(string pattern, bool ignoreCase = false) =>
        Cache.GetOrAdd((pattern, ignoreCase), key => new PyRegex(key.Item1, key.Item2));

    private static bool HasSurrogate(string text)
    {
        foreach (var c in text)
            if (char.IsSurrogate(c))
                return true;
        return false;
    }

    private Regex Scanner(string text) => HasSurrogate(text) ? guarded.Value : search;

    /// <summary>re.search: the first match anywhere, or null. Its group numbers are .NET's, which are Python's
    /// unless the pattern has named groups: use Find for Python's numbering.</summary>
    public Match? Search(string text)
    {
        var m = Scanner(text).Match(text, 0);
        return m.Success ? m : null;
    }

    public bool IsMatch(string text) => Scanner(text).Match(text, 0).Success;

    /// <summary>re.match: a match starting at the beginning, or null.</summary>
    public Match? MatchStart(string text)
    {
        var m = anchored.Value.Match(text, 0);
        return m.Success ? m : null;
    }

    /// <summary>re.fullmatch.</summary>
    public bool FullMatch(string text) => full.Value.Match(text, 0).Success;

    /// <summary>re.search, with Python's group numbering.</summary>
    public PyMatch? Find(string text) => Search(text) is { } m ? new PyMatch(m, this) : null;

    /// <summary>re.match, with Python's group numbering.</summary>
    public PyMatch? MatchGroups(string text) => MatchStart(text) is { } m ? new PyMatch(m, this) : null;

    /// <summary>re.fullmatch, with Python's group numbering.</summary>
    public PyMatch? FullMatchGroups(string text)
    {
        var m = full.Value.Match(text, 0);
        return m.Success ? new PyMatch(m, this) : null;
    }

    /// <summary>A .NET match from this pattern (Search, MatchStart) with Python's group numbering.</summary>
    public PyMatch Wrap(Match match) => new(match, this);

    /// <summary>re.finditer: every match, stepping through the text as Python does.</summary>
    public IEnumerable<PyMatch> FindIter(string text) => Matches(text).Select(m => new PyMatch(m, this));

    private IEnumerable<Match> Matches(string text)
    {
        var scan = Scanner(text);
        var pos = 0;
        var mustAdvance = false;
        while (pos <= text.Length)
        {
            Match m;
            if (mustAdvance)
            {
                // After an empty match, a non-empty one may start at the same place; otherwise search on from the
                // next character (a surrogate pair being one character).
                m = nonEmpty.Value.Match(text, pos);
                if (!m.Success)
                {
                    if (pos >= text.Length)
                        yield break;
                    m = scan.Match(text, pos + PyUnicode.At(text, pos).Width);
                }
            }
            else
                m = scan.Match(text, pos);
            if (!m.Success)
                yield break;
            yield return m;
            mustAdvance = m.Length == 0;
            pos = m.Index + m.Length;
        }
    }

    /// <summary>re.findall: every match's text, or for a pattern with one group, that group's ("" where it took no
    /// part). With more groups Python gives tuples: use FindIter.</summary>
    public List<string> FindAll(string text)
    {
        if (GroupCount > 1)
            throw new InvalidOperationException($"{Py.ReprString(Pattern)} has {GroupCount} groups, so findall would give tuples; use FindIter.");
        return GroupCount == 0
            ? Matches(text).Select(m => m.Value).ToList()
            : FindIter(text).Select(m => m.Group(1) ?? "").ToList();
    }

    /// <summary>re.sub with a literal replacement. A backslash is refused: Python would read it as an escape or a
    /// group reference.</summary>
    public string Sub(string replacement, string text)
    {
        if (replacement.Contains('\\'))
            throw new ArgumentException("Sub takes a literal replacement; Python would read a backslash as an escape or group reference.", nameof(replacement));
        var b = new StringBuilder();
        var last = 0;
        foreach (var m in Matches(text))
        {
            b.Append(text, last, m.Index - last).Append(replacement);
            last = m.Index + m.Length;
        }
        return b.Append(text, last, text.Length - last).ToString();
    }

    /// <summary>re.split, for a pattern with no groups (with groups, Python puts their text between the pieces).</summary>
    public string[] Split(string text)
    {
        if (GroupCount > 0)
            throw new InvalidOperationException($"{Py.ReprString(Pattern)} has groups, so re.split would put their text between the pieces.");
        var pieces = new List<string>();
        var last = 0;
        foreach (var m in Matches(text))
        {
            pieces.Add(text[last..m.Index]);
            last = m.Index + m.Length;
        }
        pieces.Add(text[last..]);
        return [.. pieces];
    }

    // Shortcuts for the calls the Python makes.
    public static Match? Search(string pattern, string text, bool ignoreCase = false) => Get(pattern, ignoreCase).Search(text);

    public static bool IsMatch(string pattern, string text, bool ignoreCase = false) => Get(pattern, ignoreCase).IsMatch(text);

    /// <summary>Checks a pattern as config.py's re.compile did, and the dialect guard. Null if fine, else why not.</summary>
    public static string? Problem(string pattern)
    {
        try
        {
            Get(pattern, true);
            Get(pattern, false);
            return null;
        }
        catch (PyRegexUnsupportedException error)
        {
            return error.Message;
        }
    }

    /// <summary>re.escape, as Python 3.7+ does it: only the special characters, and whitespace.</summary>
    public static string Escape(string text)
    {
        const string special = "()[]{}?*+-|^$\\.&~# \t\n\r\v\f";
        var b = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            if (special.Contains(c))
                b.Append('\\');
            b.Append(c);
        }
        return b.ToString();
    }

    /// <summary>The Python pattern as .NET reads it, and the options its leading flags give. Case-insensitive
    /// matching is written into the pattern, so the options never include IgnoreCase.</summary>
    public static (string Pattern, RegexOptions Options) Translate(string pattern, bool ignoreCase = false) =>
        new Translator(pattern, ignoreCase).Run();

    internal string GroupKey(int index) => groupKeys[index - 1];

    // ---- the translator ----

    private sealed class Translator(string pattern, bool ignoreCase)
    {
        private readonly StringBuilder b = new(pattern.Length * 2);
        private int i;
        private bool fold = ignoreCase;
        private bool dotAll;
        private readonly Stack<(bool Fold, bool DotAll)> scopes = new();
        private int unnamed;
        public readonly List<string> GroupKeys = [];
        public readonly Dictionary<string, int> Names = [];

        private void Refuse(string why) => throw new PyRegexUnsupportedException(pattern, why);

        public (string, RegexOptions) Run()
        {
            var options = RegexOptions.None;
            // Global flags: only at the start, as Python 3.11+ requires; several groups of them are allowed.
            while (Next("(?"))
            {
                var j = i + 2;
                while (j < pattern.Length && char.IsAsciiLetter(pattern[j]))
                    j++;
                if (j == i + 2 || j >= pattern.Length || pattern[j] != ')')
                    break;
                foreach (var f in pattern[(i + 2)..j])
                {
                    switch (f)
                    {
                        case 'i': fold = true; break;
                        case 'm': options |= RegexOptions.Multiline; break;
                        case 's': dotAll = true; break;
                        case 'u': break;   // Unicode matching: the default for str patterns
                        default: Refuse($"the inline flag {f}"); break;
                    }
                }
                i = j + 1;
            }
            while (i < pattern.Length)
                Step();
            if (scopes.Count > 0)
                Refuse("an unclosed group");
            return (b.ToString(), options);
        }

        private bool Next(string text) => string.CompareOrdinal(pattern, i, text, 0, text.Length) == 0;

        private void Step()
        {
            var c = pattern[i];
            switch (c)
            {
                case '(':
                    Group();
                    return;
                case ')':
                    if (scopes.Count == 0)
                        Refuse("an unbalanced parenthesis");
                    (fold, dotAll) = scopes.Pop();
                    b.Append(')');
                    i++;
                    return;
                case '|' or '^' or '$':
                    b.Append(c);
                    i++;
                    return;
                case '.':
                    b.Append(dotAll ? AnyPattern : DotPattern);
                    i++;
                    return;
                case '[':
                    b.Append(Class().ToPattern());
                    return;
                case '*' or '+' or '?':
                    b.Append(c);
                    i++;
                    Lazy();
                    return;
                case '{':
                    Brace();
                    return;
                case '\\':
                    Escape();
                    return;
                default:
                    var (cp, width) = PyUnicode.At(pattern, i);
                    i += width;
                    b.Append(Literal(cp));
                    return;
            }
        }

        private void Lazy()
        {
            if (i < pattern.Length && pattern[i] == '?')
            {
                b.Append('?');
                i++;
            }
            else if (i < pattern.Length && pattern[i] == '+')
                Refuse("a possessive quantifier");
        }

        /// <summary>{m,n}, {m}, {,n}, {m,} and {,} are quantifiers in Python (written out in full for .NET); any other
        /// "{" is a literal brace.</summary>
        private void Brace()
        {
            var j = i + 1;
            var lo = Digits(ref j);
            var comma = j < pattern.Length && pattern[j] == ',';
            var hi = lo;
            if (comma)
            {
                j++;
                hi = Digits(ref j);
            }
            if (!(j < pattern.Length && pattern[j] == '}' && (lo.Length > 0 || comma)))
            {
                b.Append(@"\{");
                i++;
                return;
            }
            if (comma && lo.Length > 0 && hi.Length > 0 && Compare(lo, hi) > 0)
                Refuse("a repeat whose minimum is greater than its maximum");
            b.Append('{').Append(lo.Length == 0 ? "0" : lo);
            if (comma)
                b.Append(',').Append(hi);
            b.Append('}');
            i = j + 1;
            Lazy();
        }

        private static int Compare(string a, string b)
        {
            a = a.TrimStart('0');
            b = b.TrimStart('0');
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        }

        private string Digits(ref int j)
        {
            var start = j;
            while (j < pattern.Length && char.IsAsciiDigit(pattern[j]))
                j++;
            return pattern[start..j];
        }

        private void Group()
        {
            if (!Next("(?"))
            {
                scopes.Push((fold, dotAll));
                unnamed++;
                GroupKeys.Add(unnamed.ToString(CultureInfo.InvariantCulture));
                b.Append('(');
                i++;
                return;
            }
            if (Next("(?:") || Next("(?=") || Next("(?!") || Next("(?>"))
            {
                scopes.Push((fold, dotAll));
                b.Append(pattern, i, 3);
                i += 3;
                return;
            }
            if (Next("(?<=") || Next("(?<!"))
            {
                scopes.Push((fold, dotAll));
                b.Append(pattern, i, 4);
                i += 4;
                return;
            }
            var nameStart = i + 4;
            if (Next("(?P<"))
            {
                var close = pattern.IndexOf('>', nameStart);
                if (close < 0)
                    Refuse("an unterminated group name");
                var name = pattern[nameStart..close];
                if (!IsAsciiIdentifier(name))
                    Refuse($"the group name {Py.ReprString(name)} (only ASCII identifiers are supported)");
                if (Names.ContainsKey(name))
                    Refuse($"the group name {Py.ReprString(name)} twice");
                scopes.Push((fold, dotAll));
                GroupKeys.Add(name);
                Names[name] = GroupKeys.Count;
                b.Append("(?<").Append(name).Append('>');
                i = close + 1;
                return;
            }
            if (Next("(?P="))
            {
                var close = pattern.IndexOf(')', nameStart);
                if (close < 0)
                    Refuse("an unterminated group reference");
                var name = pattern[nameStart..close];
                if (!Names.ContainsKey(name))
                    Refuse($"a reference to {Py.ReprString(name)}, which isn't a group before it");
                if (fold)
                    Refuse("a group reference with IGNORECASE");
                b.Append(@"\k<").Append(name).Append('>');
                i = close + 1;
                return;
            }
            if (Next("(?#"))
            {
                var close = pattern.IndexOf(')', i);
                if (close < 0)
                    Refuse("an unterminated comment");
                i = close + 1;
                return;
            }
            if (Next("(?P") || Next("(?<") || Next("(?'"))
                Refuse("a named group .NET reads differently");
            if (Next("(?("))
                Refuse("a conditional group");
            // Scoped flags: (?i:...), (?-i:...), (?s-i:...).
            var j = i + 2;
            while (j < pattern.Length && (char.IsAsciiLetter(pattern[j]) || pattern[j] == '-'))
                j++;
            var flags = pattern[(i + 2)..j];
            var end = j < pattern.Length ? pattern[j] : '\0';
            if (flags.Length == 0)
                Refuse("a group .NET reads differently");
            if (end == ')')
                Refuse("inline flags in the middle of the pattern");
            if (end != ':')
                Refuse("a group .NET reads differently");
            var dash = flags.IndexOf('-');
            var on = dash < 0 ? flags : flags[..dash];
            var off = dash < 0 ? "" : flags[(dash + 1)..];
            if (off.Contains('-') || (dash >= 0 && off.Length == 0))
                Refuse("a group .NET reads differently");
            var netOn = new StringBuilder();
            var netOff = new StringBuilder();
            scopes.Push((fold, dotAll));
            foreach (var (letters, value) in new[] { (on, true), (off, false) })
            {
                foreach (var f in letters)
                {
                    switch (f)
                    {
                        case 'i': fold = value; break;
                        case 's': dotAll = value; break;
                        case 'm': (value ? netOn : netOff).Append('m'); break;
                        case 'u' when value: break;
                        default: Refuse($"the inline flag {f}"); break;
                    }
                }
            }
            b.Append("(?").Append(netOn);
            if (netOff.Length > 0)
                b.Append('-').Append(netOff);
            b.Append(':');
            i = j + 1;
        }

        private static bool IsAsciiIdentifier(string name) =>
            name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

        private void Escape()
        {
            if (i + 1 >= pattern.Length)
                Refuse("a trailing backslash");
            var e = pattern[i + 1];
            switch (e)
            {
                case 'A': b.Append(@"\A"); i += 2; return;
                case 'Z' or 'z': b.Append(@"\z"); i += 2; return;
                case 'b': b.Append(Boundary); i += 2; return;
                case 'B': b.Append(NotBoundary); i += 2; return;
                case 'w' or 'W' or 'd' or 'D' or 's' or 'S':
                    b.Append(CategoryPattern(e));
                    i += 2;
                    return;
            }
            b.Append(Literal(EscapedCodePoint(false)));
        }

        /// <summary>A literal written as an escape (\n, \x41, \u00e9, \U0001F600, \.), from pattern[i] == '\\'.</summary>
        private int EscapedCodePoint(bool inClass)
        {
            var e = pattern[i + 1];
            i += 2;
            switch (e)
            {
                case 'a': return 7;
                case 'b' when inClass: return 8;
                case 'f': return 12;
                case 'n': return 10;
                case 'r': return 13;
                case 't': return 9;
                case 'v': return 11;
                case 'x': return Hex(2);
                case 'u': return Hex(4);
                case 'U':
                    var cp = Hex(8);
                    if (cp > CodePointSet.Max)
                        Refuse($@"\U{cp:x8}, which isn't a character");
                    return cp;
                case 'N': Refuse(@"\N{...}"); break;
            }
            if (char.IsAsciiDigit(e))
                Refuse("a backreference or octal escape");
            if (char.IsAsciiLetter(e))
                Refuse($@"\{e}");
            i--;
            var (other, width) = PyUnicode.At(pattern, i);
            i += width;
            return other;   // any other character escaped stands for itself
        }

        private int Hex(int count)
        {
            if (i + count > pattern.Length || !pattern.Substring(i, count).All(char.IsAsciiHexDigit))
                Refuse("an incomplete hex escape");
            var value = int.Parse(pattern.AsSpan(i, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            i += count;
            return value;
        }

        /// <summary>A literal character, case-folded as Python's re does when IGNORECASE is on.</summary>
        private string Literal(int cp)
        {
            if (fold && PyUnicode.IsSreCased(cp))
                return FoldedLiterals.GetOrAdd(cp, c => FoldLiteral(c).ToPattern());
            if (cp < 0x80 && (char.IsAsciiLetterOrDigit((char)cp) || cp == '_'))
                return ((char)cp).ToString();
            return CodePointSet.Of(cp).ToPattern();
        }

        /// <summary>A character class, from pattern[i] == '['. Python's reading: "]" first is literal, ranges of
        /// literals, the category escapes, and anything else literal (so "[[:alpha:]]" and "[a-z-[aeiou]]" are just
        /// characters, with no .NET meaning). Returns the set of code points it matches.</summary>
        private CodePointSet Class()
        {
            i++;
            var negate = false;
            if (i < pattern.Length && pattern[i] == '^')
            {
                negate = true;
                i++;
            }
            var literals = new List<int>();
            var ranges = new List<(int Lo, int Hi)>();
            var categories = new List<CodePointSet>();
            var first = true;
            while (true)
            {
                if (i >= pattern.Length)
                    Refuse("an unterminated character class");
                var c = pattern[i];
                if (c == ']' && !first)
                {
                    i++;
                    break;
                }
                first = false;
                int lo;
                if (c == '\\')
                {
                    if (i + 1 >= pattern.Length)
                        Refuse("a trailing backslash");
                    var e = pattern[i + 1];
                    if (e is 'w' or 'W' or 'd' or 'D' or 's' or 'S')
                    {
                        categories.Add(Category(e));
                        i += 2;
                        continue;
                    }
                    if (e is 'A' or 'B' or 'Z' or 'z')
                        Refuse($@"\{e} inside a character class");
                    lo = EscapedCodePoint(true);
                }
                else
                {
                    var (cp, width) = PyUnicode.At(pattern, i);
                    lo = cp;
                    i += width;
                }
                // A range lo-hi, unless the "-" is last ("[a-]").
                if (i + 1 < pattern.Length && pattern[i] == '-' && pattern[i + 1] != ']')
                {
                    i++;
                    int hi;
                    if (pattern[i] == '\\')
                    {
                        if (i + 1 < pattern.Length && pattern[i + 1] is 'w' or 'W' or 'd' or 'D' or 's' or 'S')
                            Refuse("a range ending in a category");
                        hi = EscapedCodePoint(true);
                    }
                    else
                    {
                        var (cp, width) = PyUnicode.At(pattern, i);
                        hi = cp;
                        i += width;
                    }
                    if (hi < lo)
                        Refuse("a bad character range");
                    ranges.Add((lo, hi));
                }
                else
                    literals.Add(lo);
            }
            var set = fold
                ? FoldClass(literals, ranges, categories)
                : CodePointSet.Union([CodePointSet.FromPoints(literals), .. ranges.Select(r => CodePointSet.Range(r.Lo, r.Hi)), .. categories]);
            return negate ? set.Complement() : set;
        }
    }

    // ---- sets and their patterns (cached: \w alone is several kilobytes) ----

    private static readonly ConcurrentDictionary<int, string> FoldedLiterals = new();
    private static readonly ConcurrentDictionary<char, string> Categories = new();

    private static string? dot, any, boundary, notBoundary, wordPattern;
    private static CodePointSet? word, digit, space;

    private static string DotPattern => dot ??= CodePointSet.Of('\n').Complement().ToPattern();

    private static string AnyPattern => any ??= CodePointSet.All.ToPattern();

    private static CodePointSet Word => word ??= CodePointSet.FromFlat(PyUnicode.WordRanges);

    private static CodePointSet Digit => digit ??= CodePointSet.FromFlat(PyUnicode.DigitRanges);

    private static CodePointSet Space => space ??= CodePointSet.FromFlat(PyUnicode.SpaceRanges);

    private static CodePointSet Category(char e) => e switch
    {
        'w' => Word,
        'W' => Word.Complement(),
        'd' => Digit,
        'D' => Digit.Complement(),
        's' => Space,
        _ => Space.Complement(),
    };

    private static string CategoryPattern(char e) => Categories.GetOrAdd(e, c => Category(c).ToPattern());

    private static string WordPattern => wordPattern ??= CategoryPattern('w');

    private static string Boundary => boundary ??=
        "(?:(?<=" + WordPattern + ")(?!" + WordPattern + ")|(?<!" + WordPattern + ")(?=" + WordPattern + "))";

    private static string NotBoundary => notBoundary ??=
        "(?:(?<=" + WordPattern + ")(?=" + WordPattern + ")|(?<!" + WordPattern + ")(?!" + WordPattern + "))";

    // ---- IGNORECASE, as re/_compiler.py and _sre do it ----

    private static CodePointSet? lowerKeys;

    /// <summary>The characters whose simple lower case isn't themselves.</summary>
    private static CodePointSet LowerKeys => lowerKeys ??= CodePointSet.FromPoints(EveryOther(PyUnicode.SreLowerPairs));

    private static IEnumerable<int> EveryOther(int[] pairs)
    {
        for (var k = 0; k < pairs.Length; k += 2)
            yield return pairs[k];
    }

    /// <summary>Every x whose simple lower case is in target: what _sre tests as lower(ch) in the set.</summary>
    private static CodePointSet LowerPreimage(CodePointSet target)
    {
        var mapped = new List<int>();
        var pairs = PyUnicode.SreLowerPairs;
        for (var k = 0; k < pairs.Length; k += 2)
            if (target.Contains(pairs[k + 1]))
                mapped.Add(pairs[k]);
        return target.Minus(LowerKeys).Union(CodePointSet.FromPoints(mapped));
    }

    /// <summary>A cased literal with IGNORECASE: LITERAL_UNI_IGNORE (lower(ch) == lower(c)), or IN_UNI_IGNORE over
    /// re._casefix's extra cases.</summary>
    internal static CodePointSet FoldLiteral(int cp)
    {
        var lo = PyUnicode.SreLower(cp);
        var targets = new List<int> { lo };
        if (PyUnicode.ExtraCaseMap.TryGetValue(lo, out var extra))
            targets.AddRange(extra);
        return LowerPreimage(CodePointSet.FromPoints(targets));
    }

    /// <summary>A class with IGNORECASE, as _optimize_charset builds it: literals and the BMP part of ranges lowered
    /// (with the extra cases), ranges reaching beyond the BMP kept as RANGE_UNI_IGNORE, categories as they are; then
    /// if anything was cased, ch matches when lower(ch) does, and otherwise ch itself.</summary>
    private static CodePointSet FoldClass(List<int> literals, List<(int Lo, int Hi)> ranges, List<CodePointSet> categories)
    {
        var lowered = new List<int>();
        var hasCased = false;
        var extraCases = PyUnicode.ExtraCaseMap;
        void Add(int l)
        {
            lowered.Add(l);
            if (extraCases.TryGetValue(l, out var extra))
                lowered.AddRange(extra);
        }
        foreach (var c in literals)
        {
            var l = PyUnicode.SreLower(c);
            Add(l);
            hasCased |= PyUnicode.IsSreCased(l);
        }
        var uniIgnore = new List<(int Lo, int Hi)>();
        foreach (var (lo, hi) in ranges)
        {
            for (var c = lo; c <= Math.Min(hi, 0xFFFF); c++)
                Add(PyUnicode.SreLower(c));
            if (hi > 0xFFFF)
            {
                uniIgnore.Add((lo, hi));
                hasCased = true;
            }
            else
            {
                for (var c = lo; c <= hi && !hasCased; c++)
                    hasCased = PyUnicode.IsSreCased(c);
            }
        }
        var charset = CodePointSet.Union([CodePointSet.FromPoints(lowered), .. categories]);
        if (!hasCased)
            return charset;
        var result = LowerPreimage(charset);
        foreach (var (lo, hi) in uniIgnore)
        {
            // RANGE_UNI_IGNORE: lower(ch) in the range, or upper(lower(ch)) in it.
            var range = CodePointSet.Range(lo, hi);
            var uppers = new List<int>();
            var pairs = PyUnicode.SreUpperPairs;
            for (var k = 0; k < pairs.Length; k += 2)
                if (range.Contains(pairs[k + 1]))
                    uppers.Add(pairs[k]);
            result = result.Union(LowerPreimage(range.Union(CodePointSet.FromPoints(uppers))));
        }
        return result;
    }
}

/// <summary>A match with Python's group numbering (re.Match): .NET numbers named groups after the unnamed ones,
/// where Python numbers every group in order. Positions are UTF-16 indexes into the .NET string.</summary>
public sealed class PyMatch
{
    private readonly PyRegex regex;

    internal PyMatch(Match match, PyRegex regex)
    {
        Match = match;
        this.regex = regex;
    }

    /// <summary>The underlying .NET match.</summary>
    public Match Match { get; }

    public string Value => Match.Value;

    public int Index => Match.Index;

    public int Length => Match.Length;

    private Group? Net(int index)
    {
        if (index == 0)
            return Match;
        if (index < 0 || index > regex.GroupCount)
            throw new IndexOutOfRangeException("no such group");
        var g = Match.Groups[regex.GroupKey(index)];
        return g.Success ? g : null;
    }

    /// <summary>m.group(index): null where the group took no part (Python's None).</summary>
    public string? Group(int index) => Net(index)?.Value;

    /// <summary>m.group(name).</summary>
    public string? Group(string name) =>
        regex.GroupIndex.TryGetValue(name, out var index) ? Group(index) : throw new IndexOutOfRangeException("no such group");

    /// <summary>m.groups(): groups 1 to n.</summary>
    public IReadOnlyList<string?> Groups() => Enumerable.Range(1, regex.GroupCount).Select(Group).ToList();

    /// <summary>m.groupdict(): the named groups, in pattern order.</summary>
    public IReadOnlyDictionary<string, string?> GroupDict()
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (name, index) in regex.GroupIndex.OrderBy(p => p.Value))
            dict[name] = Group(index);
        return dict;
    }

    /// <summary>m.start(index), as a UTF-16 index; -1 where the group took no part.</summary>
    public int Start(int index = 0) => Net(index)?.Index ?? -1;

    /// <summary>m.end(index), as a UTF-16 index; -1 where the group took no part.</summary>
    public int End(int index = 0) => Net(index) is { } g ? g.Index + g.Length : -1;
}
