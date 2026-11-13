using System.Text;

namespace PyCompat;

/// <summary>Python's difflib.unified_diff, ported line for line (SequenceMatcher with its "popular lines" rule
/// included), so a dry run shows exactly the diff the Python version showed. .NET has no diff of its own.</summary>
public static class UnifiedDiff
{
    private readonly record struct Match(int A, int B, int Size);
    private readonly record struct Opcode(char Tag, int I1, int I2, int J1, int J2);   // e equal, r replace, d delete, i insert

    /// <summary>list(unified_diff(a, b, fromFile, toFile, lineterm="", n=context)): each line without an ending, so
    /// "\n".join() of it is what Python printed. a and b are lines without their endings.</summary>
    public static List<string> Lines(IReadOnlyList<string> a, IReadOnlyList<string> b, string fromFile, string toFile, int context = 3)
    {
        var output = new List<string>();
        foreach (var group in GroupedOpcodes(a, b, context))
        {
            if (output.Count == 0)
            {
                output.Add("--- " + fromFile);
                output.Add("+++ " + toFile);
            }
            var (first, last) = (group[0], group[^1]);
            output.Add($"@@ -{Range(first.I1, last.I2)} +{Range(first.J1, last.J2)} @@");
            foreach (var code in group)
            {
                if (code.Tag == 'e')
                {
                    for (var i = code.I1; i < code.I2; i++)
                        output.Add(" " + a[i]);
                    continue;
                }
                if (code.Tag is 'r' or 'd')
                    for (var i = code.I1; i < code.I2; i++)
                        output.Add("-" + a[i]);
                if (code.Tag is 'r' or 'i')
                    for (var j = code.J1; j < code.J2; j++)
                        output.Add("+" + b[j]);
            }
        }
        return output;
    }

    /// <summary>unified_diff(a, b, fromFile, toFile) joined: a and b are lists of lines with their endings.</summary>
    public static string Of(IReadOnlyList<string> a, IReadOnlyList<string> b, string fromFile, string toFile, int context = 3)
    {
        var output = new StringBuilder();
        var started = false;
        foreach (var group in GroupedOpcodes(a, b, context))
        {
            if (!started)
            {
                started = true;
                output.Append("--- ").Append(fromFile).Append('\n');
                output.Append("+++ ").Append(toFile).Append('\n');
            }
            var (first, last) = (group[0], group[^1]);
            output.Append("@@ -").Append(Range(first.I1, last.I2)).Append(" +").Append(Range(first.J1, last.J2)).Append(" @@\n");
            foreach (var code in group)
            {
                if (code.Tag == 'e')
                {
                    for (var i = code.I1; i < code.I2; i++)
                        output.Append(' ').Append(a[i]);
                    continue;
                }
                if (code.Tag is 'r' or 'd')
                    for (var i = code.I1; i < code.I2; i++)
                        output.Append('-').Append(a[i]);
                if (code.Tag is 'r' or 'i')
                    for (var j = code.J1; j < code.J2; j++)
                        output.Append('+').Append(b[j]);
            }
        }
        return output.ToString();
    }

    private static string Range(int start, int stop)
    {
        var beginning = start + 1;
        var length = stop - start;
        if (length == 1)
            return beginning.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (length == 0)
            beginning--;
        return $"{beginning},{length}";
    }

    private static List<List<Opcode>> GroupedOpcodes(IReadOnlyList<string> a, IReadOnlyList<string> b, int n)
    {
        var codes = Opcodes(a, b);
        if (codes.Count == 0)
            codes.Add(new Opcode('e', 0, 1, 0, 1));
        if (codes[0].Tag == 'e')
        {
            var c = codes[0];
            codes[0] = c with { I1 = Math.Max(c.I1, c.I2 - n), J1 = Math.Max(c.J1, c.J2 - n) };
        }
        if (codes[^1].Tag == 'e')
        {
            var c = codes[^1];
            codes[^1] = c with { I2 = Math.Min(c.I2, c.I1 + n), J2 = Math.Min(c.J2, c.J1 + n) };
        }
        var groups = new List<List<Opcode>>();
        var group = new List<Opcode>();
        foreach (var code in codes)
        {
            var (i1, i2, j1, j2) = (code.I1, code.I2, code.J1, code.J2);
            if (code.Tag == 'e' && i2 - i1 > n + n)
            {
                group.Add(new Opcode('e', i1, Math.Min(i2, i1 + n), j1, Math.Min(j2, j1 + n)));
                groups.Add(group);
                group = [];
                (i1, j1) = (Math.Max(i1, i2 - n), Math.Max(j1, j2 - n));
            }
            group.Add(new Opcode(code.Tag, i1, i2, j1, j2));
        }
        if (group.Count > 0 && !(group.Count == 1 && group[0].Tag == 'e'))
            groups.Add(group);
        return groups;
    }

    private static List<Opcode> Opcodes(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var (i, j) = (0, 0);
        var answer = new List<Opcode>();
        foreach (var block in MatchingBlocks(a, b))
        {
            var tag = i < block.A && j < block.B ? 'r' : i < block.A ? 'd' : j < block.B ? 'i' : '\0';
            if (tag != '\0')
                answer.Add(new Opcode(tag, i, block.A, j, block.B));
            (i, j) = (block.A + block.Size, block.B + block.Size);
            if (block.Size > 0)
                answer.Add(new Opcode('e', block.A, i, block.B, j));
        }
        return answer;
    }

    private static List<Match> MatchingBlocks(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // b2j: where each line of b appears, without the "popular" ones (difflib's autojunk, for 200+ lines).
        var b2j = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < b.Count; index++)
        {
            if (!b2j.TryGetValue(b[index], out var indices))
                b2j[b[index]] = indices = [];
            indices.Add(index);
        }
        if (b.Count >= 200)
        {
            var test = b.Count / 100 + 1;
            foreach (var popular in b2j.Where(pair => pair.Value.Count > test).Select(pair => pair.Key).ToList())
                b2j.Remove(popular);
        }

        var queue = new List<(int ALo, int AHi, int BLo, int BHi)> { (0, a.Count, 0, b.Count) };
        var blocks = new List<Match>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue[^1];
            queue.RemoveAt(queue.Count - 1);
            var x = LongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (x.Size > 0)
            {
                blocks.Add(x);
                if (alo < x.A && blo < x.B)
                    queue.Add((alo, x.A, blo, x.B));
                if (x.A + x.Size < ahi && x.B + x.Size < bhi)
                    queue.Add((x.A + x.Size, ahi, x.B + x.Size, bhi));
            }
        }
        blocks.Sort((p, q) => p.A != q.A ? p.A.CompareTo(q.A) : p.B != q.B ? p.B.CompareTo(q.B) : p.Size.CompareTo(q.Size));

        var (i1, j1, k1) = (0, 0, 0);
        var nonAdjacent = new List<Match>();
        foreach (var (i2, j2, k2) in blocks)
        {
            if (i1 + k1 == i2 && j1 + k1 == j2)
                k1 += k2;
            else
            {
                if (k1 > 0)
                    nonAdjacent.Add(new Match(i1, j1, k1));
                (i1, j1, k1) = (i2, j2, k2);
            }
        }
        if (k1 > 0)
            nonAdjacent.Add(new Match(i1, j1, k1));
        nonAdjacent.Add(new Match(a.Count, b.Count, 0));
        return nonAdjacent;
    }

    private static Match LongestMatch(IReadOnlyList<string> a, IReadOnlyList<string> b, Dictionary<string, List<int>> b2j,
        int alo, int ahi, int blo, int bhi)
    {
        var (besti, bestj, bestsize) = (alo, blo, 0);
        var j2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var indices))
            {
                foreach (var j in indices)
                {
                    if (j < blo)
                        continue;
                    if (j >= bhi)
                        break;
                    var k = newj2len[j] = j2len.GetValueOrDefault(j - 1) + 1;
                    if (k > bestsize)
                        (besti, bestj, bestsize) = (i - k + 1, j - k + 1, k);
                }
            }
            j2len = newj2len;
        }
        // Extend by equal lines at each end (popular lines aren't junk, so they extend; there is no other junk).
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
            (besti, bestj, bestsize) = (besti - 1, bestj - 1, bestsize + 1);
        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
            bestsize++;
        return new Match(besti, bestj, bestsize);
    }
}
