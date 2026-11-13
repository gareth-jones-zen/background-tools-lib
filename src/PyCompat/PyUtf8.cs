using System.Text;

namespace PyCompat;

/// <summary>Python's UTF-8 decoding errors. Python raises UnicodeDecodeError (a ValueError) with a message such as
/// "'utf-8' codec can't decode byte 0xff in position 3: invalid start byte", or "bytes in position 3-4" for more
/// than one; Decode throws .NET's DecoderFallbackException (what PyStr.ReadText threw before v0.3, so existing
/// catches still work) with that message, the bad bytes and Python's start position.</summary>
public static class PyUtf8
{
    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>bytes.decode("utf-8"), throwing DecoderFallbackException with Python's message.</summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var (start, end, _) = FirstError(bytes) ?? (0, 1, "invalid start byte");
            throw new DecoderFallbackException(Message(bytes, start, end), bytes[start..end], start);
        }
    }

    /// <summary>Python's message for the error FirstError found.</summary>
    public static string Message(ReadOnlySpan<byte> bytes, int start, int end)
    {
        var reason = FirstErrorAt(bytes, start)?.Reason ?? "invalid start byte";
        return end - start == 1
            ? $"'utf-8' codec can't decode byte 0x{bytes[start]:x2} in position {start}: {reason}"
            : $"'utf-8' codec can't decode bytes in position {start}-{end - 1}: {reason}";
    }

    private static bool Continuation(byte b) => b is >= 0x80 and <= 0xBF;

    /// <summary>The first error in the bytes, as CPython's UTF-8 decoder finds it (stringlib/codecs.h): its start and
    /// end (exclusive) byte offsets, and the reason ("invalid start byte", "invalid continuation byte" or "unexpected
    /// end of data"). Null if the bytes are valid.</summary>
    public static (int Start, int End, string Reason)? FirstError(ReadOnlySpan<byte> s) => FirstErrorAt(s, 0);

    private static (int Start, int End, string Reason)? FirstErrorAt(ReadOnlySpan<byte> s, int from)
    {
        var p = from;
        while (p < s.Length)
        {
            var ch = s[p];
            if (ch < 0x80)
            {
                p++;
                continue;
            }
            var left = s.Length - p;
            int bad;   // 0: unexpected end of data; 1: invalid start byte; 2-4: invalid continuation, bytes taken + 1
            if (ch < 0xC2)
                bad = 1;
            else if (ch < 0xE0)
            {
                if (left < 2)
                    bad = 0;
                else if (!Continuation(s[p + 1]))
                    bad = 2;
                else
                {
                    p += 2;
                    continue;
                }
            }
            else if (ch < 0xF0)
            {
                if (left < 3)
                {
                    if (left < 2)
                        bad = 0;
                    else
                    {
                        var c2 = s[p + 1];
                        bad = !Continuation(c2) || (c2 < 0xA0 ? ch == 0xE0 : ch == 0xED) ? 2 : 0;
                    }
                }
                else
                {
                    var c2 = s[p + 1];
                    if (!Continuation(c2) || (ch == 0xE0 && c2 < 0xA0) || (ch == 0xED && c2 >= 0xA0))
                        bad = 2;
                    else if (!Continuation(s[p + 2]))
                        bad = 3;
                    else
                    {
                        p += 3;
                        continue;
                    }
                }
            }
            else if (ch < 0xF5)
            {
                if (left < 4)
                {
                    if (left < 2)
                        bad = 0;
                    else
                    {
                        var c2 = s[p + 1];
                        if (!Continuation(c2) || (c2 < 0x90 ? ch == 0xF0 : ch == 0xF4))
                            bad = 2;
                        else if (left < 3)
                            bad = 0;
                        else
                            bad = Continuation(s[p + 2]) ? 0 : 3;
                    }
                }
                else
                {
                    var c2 = s[p + 1];
                    if (!Continuation(c2) || (ch == 0xF0 && c2 < 0x90) || (ch == 0xF4 && c2 >= 0x90))
                        bad = 2;
                    else if (!Continuation(s[p + 2]))
                        bad = 3;
                    else if (!Continuation(s[p + 3]))
                        bad = 4;
                    else
                    {
                        p += 4;
                        continue;
                    }
                }
            }
            else
                bad = 1;
            return bad switch
            {
                0 => (p, s.Length, "unexpected end of data"),
                1 => (p, p + 1, "invalid start byte"),
                _ => (p, p + bad - 1, "invalid continuation byte"),
            };
        }
        return null;
    }
}
