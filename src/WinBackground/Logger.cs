using System.Text;

namespace WinBackground;

/// <summary>A log file that a background tool can always write to.</summary>
public static class Logger
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>A log(text) function: appends "yyyy-MM-dd HH:mm:ss  text" to the file, and writes it to echo too
    /// (with the time unless stampEcho is false). Never throws: a log that can't be written (a locked or
    /// missing folder, no console) mustn't stop the tool.</summary>
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
                    echo.WriteLine(stampEcho ? line : text);
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
                    File.AppendAllText(path, line + "\r\n", Utf8);   // as Python's text mode wrote it on Windows
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    // never raises
                }
            }
        };
    }
}
