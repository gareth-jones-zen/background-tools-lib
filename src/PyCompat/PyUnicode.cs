namespace PyCompat;

/// <summary>Lookups over the tables in PyUnicode.g.cs (generated from Python, so they're Python's Unicode data, not
/// .NET's). Code points, not UTF-16 units: a character beyond U+FFFF is one code point, and a lone surrogate is a
/// code point of its own, as in Python.</summary>
internal static partial class PyUnicode
{
    public static bool In(int[] ranges, int cp)
    {
        int lo = 0, hi = ranges.Length / 2 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (cp < ranges[2 * mid])
                hi = mid - 1;
            else if (cp > ranges[2 * mid + 1])
                lo = mid + 1;
            else
                return true;
        }
        return false;
    }

    public static bool IsWord(int cp) => In(WordRanges, cp);

    public static bool IsPrintable(int cp) => In(PrintableRanges, cp);

    public static bool IsCased(int cp) => In(CasedRanges, cp);

    public static bool IsCaseIgnorable(int cp) => In(CaseIgnorableRanges, cp);

    public static bool IsSreCased(int cp) => In(SreCasedRanges, cp);

    /// <summary>The full mapping for cp from a From/To pair of tables, or null if it maps to itself.</summary>
    public static string? Map(int[] from, string[] to, int cp)
    {
        var at = Array.BinarySearch(from, cp);
        return at >= 0 ? to[at] : null;
    }

    private static int PairLookup(int[] pairs, int cp)
    {
        int lo = 0, hi = pairs.Length / 2 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var key = pairs[2 * mid];
            if (cp < key)
                hi = mid - 1;
            else if (cp > key)
                lo = mid + 1;
            else
                return pairs[2 * mid + 1];
        }
        return cp;
    }

    /// <summary>_sre.unicode_tolower: the simple lower case.</summary>
    public static int SreLower(int cp) => PairLookup(SreLowerPairs, cp);

    /// <summary>The simple upper case, for characters beyond the BMP (BMP characters map to themselves here: re only
    /// needs this for RANGE_UNI_IGNORE, where the BMP is covered otherwise).</summary>
    public static int SreUpperAstral(int cp) => PairLookup(SreUpperPairs, cp);

    private static Dictionary<int, int[]>? extraCases;

    /// <summary>re._casefix._EXTRA_CASES.</summary>
    public static Dictionary<int, int[]> ExtraCaseMap
    {
        get
        {
            if (extraCases is { } done)
                return done;
            var map = new Dictionary<int, int[]>();
            var i = 0;
            while (i < ExtraCases.Length)
            {
                var key = ExtraCases[i++];
                var values = new List<int>();
                while (ExtraCases[i] != 0)
                    values.Add(ExtraCases[i++]);
                i++;
                map[key] = [.. values];
            }
            return extraCases = map;
        }
    }

    /// <summary>The code point at text[i] and how many UTF-16 units it takes: a surrogate pair is one code point,
    /// a lone surrogate is itself (as Python's str holds it).</summary>
    public static (int CodePoint, int Width) At(string text, int i)
    {
        var c = text[i];
        if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            return (char.ConvertToUtf32(c, text[i + 1]), 2);
        return (c, 1);
    }

    /// <summary>A code point as UTF-16 (a lone surrogate as the single unit).</summary>
    public static string Text(int cp) => cp <= 0xFFFF ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);
}
