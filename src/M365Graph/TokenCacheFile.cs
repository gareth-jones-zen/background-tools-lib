using System.Text.Json;

namespace M365Graph;

/// <summary>The token cache file, shared with every other program that signs in through it, Python ones included.
///
/// Several programs share one cache (GJ's watchers, prep engine and triage all wake together). Until m365-graph
/// 0.1.1 it was rewritten in place, so two writers could interleave: on 10 Nov 2026 a shorter write over a longer
/// one left two stray characters after the JSON, and every tool then failed to start with "Extra data". So, as in
/// Python: writes go to a temporary file swapped in with one atomic replace, Windows' brief sharing locks are
/// retried, and a damaged file is read up to the end of its first complete JSON value. There is no lock between
/// processes, because the Python tools take none: the last writer wins, which is safe because every refresh token
/// written stays valid.</summary>
public static class TokenCacheFile
{
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    /// <summary>The cache's JSON as UTF-8, or null if there's no file yet. A file with stray text after the JSON
    /// keeps its first complete value. Throws InvalidDataException if there's no complete value at all.</summary>
    public static byte[]? Read(string path, Action<TimeSpan>? sleep = null)
    {
        if (!File.Exists(path))
            return null;
        byte[] bytes;
        try
        {
            bytes = Retrying(() => File.ReadAllBytes(path), sleep);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        return Salvage(bytes);
    }

    /// <summary>The first complete JSON value in the bytes (a byte order mark and leading whitespace skipped), or
    /// the bytes themselves if they're already valid JSON.</summary>
    public static byte[] Salvage(byte[] bytes)
    {
        ReadOnlySpan<byte> text = bytes;
        if (text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            text = text[3..];
        var reader = new Utf8JsonReader(text, new JsonReaderOptions { AllowMultipleValues = true });
        try
        {
            if (!reader.Read())
                throw new InvalidDataException("The token cache file is empty.");
            reader.Skip();
            var end = (int)reader.BytesConsumed;
            var rest = text[end..].Trim(" \t\r\n"u8);
            return rest.IsEmpty && text.Length == bytes.Length ? bytes : text[..end].ToArray();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"The token cache file has no complete JSON: {error.Message}", error);
        }
    }

    /// <summary>Replace the cache in one step: a temporary file beside it ("name.pid.tmp", as Python names it), then
    /// one atomic replace. False if Windows kept the file locked; the caller tries again on its next write.</summary>
    public static bool Write(string path, byte[] data, Action<TimeSpan>? sleep = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(temp, data);
        try
        {
            Retrying(() => { File.Move(temp, path, overwrite: true); return true; }, sleep);
            return true;
        }
        catch (Exception error) when (IsSharingLock(error))
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>Windows' "in use by another process" and "access denied", which Python sees as PermissionError.</summary>
    internal static bool IsSharingLock(Exception error) =>
        error is UnauthorizedAccessException || error is IOException { HResult: SharingViolation or LockViolation };

    /// <summary>Ten tries, 0.1 s longer apart each time, on a sharing lock only.</summary>
    internal static T Retrying<T>(Func<T> action, Action<TimeSpan>? sleep = null, int tries = 10)
    {
        sleep ??= Thread.Sleep;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception error) when (IsSharingLock(error) && attempt < tries - 1)
            {
                sleep(TimeSpan.FromSeconds(0.1 * (attempt + 1)));
            }
        }
    }
}
