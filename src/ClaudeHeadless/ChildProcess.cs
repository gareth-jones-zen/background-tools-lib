using System.Diagnostics;
using System.Text;
using PyCompat;
using WinBackground;

namespace ClaudeHeadless;

/// <summary>A child process run as Python's subprocess.run(..., capture_output=True, text=True, encoding="utf-8")
/// ran it on Windows: input written with "\r\n" line ends, output read as UTF-8 with universal newlines.</summary>
internal static class ChildProcess
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static Completed Run(ProcessStartInfo info, string? input, TimeSpan timeout, string what)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {info.FileName}.");
        var output = ReadAll(process.StandardOutput.BaseStream);
        var error = ReadAll(process.StandardError.BaseStream);
        try
        {
            if (input is not null)
            {
                var bytes = Utf8.GetBytes(PyStr.WindowsNewlines(input));
                process.StandardInput.BaseStream.Write(bytes);
            }
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // the child exited without reading it all, as Python ignores a broken pipe here
        }
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it exited after all
            }
            throw new TimeoutException($"{what} timed out after {timeout.TotalSeconds:0} seconds");
        }
        process.WaitForExit();
        return new Completed(process.ExitCode, PyStr.DecodeOutput(output.Result), PyStr.DecodeOutput(error.Result));
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
