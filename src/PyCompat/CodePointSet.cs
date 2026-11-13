using System.Globalization;
using System.Text;

namespace PyCompat;

/// <summary>A set of code points (0 to U+10FFFF) as sorted, separate, inclusive ranges: what PyRegex computes a
/// character class as, so it can write it out for .NET's UTF-16 regex with characters beyond U+FFFF matched whole.</summary>
internal sealed class CodePointSet
{
    public const int Max = 0x10FFFF;

    private readonly List<(int Lo, int Hi)> ranges;

    private CodePointSet(List<(int Lo, int Hi)> ranges) => this.ranges = ranges;

    public static readonly CodePointSet Empty = new([]);

    public static readonly CodePointSet All = new([(0, Max)]);

    public IReadOnlyList<(int Lo, int Hi)> Ranges => ranges;

    public bool IsEmpty => ranges.Count == 0;

    public static CodePointSet Of(int cp) => new([(cp, cp)]);

    public static CodePointSet Range(int lo, int hi) => lo > hi ? Empty : new([(lo, hi)]);

    /// <summary>From a generated table: first, last, first, last...</summary>
    public static CodePointSet FromFlat(int[] flat)
    {
        var list = new List<(int, int)>(flat.Length / 2);
        for (var i = 0; i < flat.Length; i += 2)
            list.Add((flat[i], flat[i + 1]));
        return Normalise(list);
    }

    public static CodePointSet FromPoints(IEnumerable<int> points) => Normalise(points.Select(p => (p, p)).ToList());

    private static CodePointSet Normalise(List<(int Lo, int Hi)> list)
    {
        list.Sort((a, b) => a.Lo.CompareTo(b.Lo));
        var merged = new List<(int Lo, int Hi)>(list.Count);
        foreach (var (lo, hi) in list)
        {
            if (lo > hi)
                continue;
            if (merged.Count > 0 && lo <= merged[^1].Hi + 1)
                merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, hi));
            else
                merged.Add((lo, hi));
        }
        return new CodePointSet(merged);
    }

    public bool Contains(int cp)
    {
        int lo = 0, hi = ranges.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (cp < ranges[mid].Lo)
                hi = mid - 1;
            else if (cp > ranges[mid].Hi)
                lo = mid + 1;
            else
                return true;
        }
        return false;
    }

    public CodePointSet Union(CodePointSet other) => Normalise([.. ranges, .. other.ranges]);

    public static CodePointSet Union(IEnumerable<CodePointSet> sets) => Normalise(sets.SelectMany(s => s.ranges).ToList());

    public CodePointSet Complement()
    {
        var list = new List<(int, int)>(ranges.Count + 1);
        var next = 0;
        foreach (var (lo, hi) in ranges)
        {
            if (lo > next)
                list.Add((next, lo - 1));
            next = hi + 1;
        }
        if (next <= Max)
            list.Add((next, Max));
        return new CodePointSet(list);
    }

    public CodePointSet Intersect(int lo, int hi)
    {
        var list = new List<(int, int)>();
        foreach (var r in ranges)
        {
            var a = Math.Max(r.Lo, lo);
            var b = Math.Min(r.Hi, hi);
            if (a <= b)
                list.Add((a, b));
        }
        return new CodePointSet(list);
    }

    public CodePointSet Minus(CodePointSet other) => Intersect(other.Complement());

    public CodePointSet Intersect(CodePointSet other)
    {
        var list = new List<(int, int)>();
        int i = 0, j = 0;
        while (i < ranges.Count && j < other.ranges.Count)
        {
            var a = Math.Max(ranges[i].Lo, other.ranges[j].Lo);
            var b = Math.Min(ranges[i].Hi, other.ranges[j].Hi);
            if (a <= b)
                list.Add((a, b));
            if (ranges[i].Hi < other.ranges[j].Hi)
                i++;
            else
                j++;
        }
        return new CodePointSet(list);
    }

    public bool SetEquals(CodePointSet other) => ranges.SequenceEqual(other.ranges);

    public IEnumerable<int> Points()
    {
        foreach (var (lo, hi) in ranges)
            for (var c = lo; c <= hi; c++)
                yield return c;
    }

    // ---- writing it as a .NET pattern ----

    private const int HighFirst = 0xD800, HighLast = 0xDBFF, LowFirst = 0xDC00, LowLast = 0xDFFF;
    private const string AnyHigh = @"[\uD800-\uDBFF]";
    private const string AnyLow = @"[\uDC00-\uDFFF]";

    /// <summary>One character of the set, as a .NET pattern that consumes a character beyond U+FFFF whole (its
    /// surrogate pair) and a lone surrogate only when it isn't half of a pair. Always a single atom, so a
    /// quantifier can follow it.</summary>
    public string ToPattern()
    {
        var bmp = Intersect(0, HighFirst - 1).Union(Intersect(LowLast + 1, 0xFFFF));
        var highs = Intersect(HighFirst, HighLast);
        var lows = Intersect(LowFirst, LowLast);
        var astral = Intersect(0x10000, Max);
        var parts = new List<string>();
        if (!bmp.IsEmpty)
            parts.Add(bmp.ClassPattern());
        if (!astral.IsEmpty)
            parts.Add(AstralPattern(astral));
        if (!highs.IsEmpty)
            parts.Add(highs.ClassPattern() + "(?!" + AnyLow + ")");
        if (!lows.IsEmpty)
            parts.Add("(?<!" + AnyHigh + ")" + lows.ClassPattern());
        if (parts.Count == 0)
            return "(?:(?!))";
        if (parts.Count == 1 && bmp.IsEmpty is false)
            return parts[0];
        return "(?:" + string.Join("|", parts) + ")";
    }

    private static string AstralPattern(CodePointSet astral)
    {
        if (astral.SetEquals(Range(0x10000, Max)))
            return AnyHigh + AnyLow;
        var outside = Range(0x10000, Max).Minus(astral);
        var inside = Pairs(astral);
        var other = Pairs(outside);
        // Whichever is shorter: the pairs in the set, or any pair but the ones outside it.
        if (other.Length + 12 < inside.Length)
            return "(?!" + other + ")" + AnyHigh + AnyLow;
        return inside;
    }

    /// <summary>An alternation of surrogate pairs matching exactly the (astral) set, high surrogates with the same
    /// low ranges merged.</summary>
    private static string Pairs(CodePointSet astral)
    {
        var byHigh = new SortedDictionary<int, List<(int Lo, int Hi)>>();
        foreach (var (lo, hi) in astral.ranges)
        {
            for (var start = lo; start <= hi;)
            {
                var high = HighFirst + ((start - 0x10000) >> 10);
                var blockEnd = 0x10000 + ((high - HighFirst + 1) << 10) - 1;
                var end = Math.Min(hi, blockEnd);
                if (!byHigh.TryGetValue(high, out var lows))
                    byHigh[high] = lows = [];
                lows.Add((LowFirst + ((start - 0x10000) & 0x3FF), LowFirst + ((end - 0x10000) & 0x3FF)));
                start = end + 1;
            }
        }
        var groups = new List<(int First, int Last, string Lows)>();
        foreach (var (high, lows) in byHigh)
        {
            var lowClass = new CodePointSet(lows).ClassPattern();
            if (groups.Count > 0 && groups[^1].Last == high - 1 && groups[^1].Lows == lowClass)
                groups[^1] = (groups[^1].First, high, lowClass);
            else
                groups.Add((high, high, lowClass));
        }
        var alternatives = groups.Select(g =>
            (g.First == g.Last ? Escape(g.First) : "[" + Escape(g.First) + "-" + Escape(g.Last) + "]") + g.Lows);
        return "(?:" + string.Join("|", alternatives) + ")";
    }

    /// <summary>A .NET character class of the set's code points (all at most U+FFFF).</summary>
    private string ClassPattern()
    {
        if (ranges.Count == 1 && ranges[0].Lo == ranges[0].Hi)
            return Escape(ranges[0].Lo);
        var b = new StringBuilder("[");
        foreach (var (lo, hi) in ranges)
        {
            b.Append(Escape(lo));
            if (hi > lo)
                b.Append(hi > lo + 1 ? "-" : "").Append(Escape(hi));
        }
        return b.Append(']').ToString();
    }

    private static string Escape(int cp) =>
        cp < 0x80 && (char.IsAsciiLetterOrDigit((char)cp) || cp is '_' or ' ' or ',' or '<' or '>' or '"' or '\'' or '=' or '@' or '%' or '/' or ':' or ';' or '!' or '&' or '~' or '`')
            ? ((char)cp).ToString()
            : @"\u" + cp.ToString("X4", CultureInfo.InvariantCulture);
}
