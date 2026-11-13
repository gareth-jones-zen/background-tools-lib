using System.Text;

namespace WinBackground;

/// <summary>A log file that a background tool can always write to.</summary>
public static class Logger
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>A log(text) function: appends "yyyy-MM-dd HH:mm:ss  text" to the file, and writes it to echo too
    /// (with the time unless stampEcho is false). Never throws: a log that can't be written (a locked or
    /// missing folder, no console) mustn't stop the tool.
    /// <para>As Python's text mode wrote it on Windows, every newline in the file is CRLF, including any inside the
    /// text: a multi-line message (a diff, a hook's output) has each LF written as CRLF (and an existing CRLF as
    /// CR CR LF, as Python's was). The echo writes each LF as the writer's own NewLine, which is CRLF for the
    /// console and for a default StreamWriter or StringWriter, so it matches Python's print() too. Until 13 Nov 2026
    /// an LF inside the text was written as it was.</para></summary>
    public static Action<string> Create(string? path, TextWriter? echo = null, bool stampEcho = true, Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.Now);
        return text =>
        {
            var line = $"{now():yyyy-MM-dd HH:mm:ss}  {text}";
            if (echo is not null)
            {
                try
                {
                    echo.WriteLine((stampEcho ? line : text).Replace("\n", echo.NewLine));
                    echo.Flush();
                }
                catch (Exception)
                {
                    // nothing to do: the file still gets it
                }
            }
            if (path is not null)
            {
                try
                {
                    var folder = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(folder))
                        Directory.CreateDirectory(folder);
                    File.AppendAllText(path, (line + "\n").Replace("\n", "\r\n"), Utf8);   // Python's text mode on Windows
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    // never raises
                }
            }
        };
    }
}
