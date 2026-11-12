namespace M365Graph;

/// <summary>Whether anyone is at the console to complete a browser sign-in.</summary>
public static class Unattended
{
    /// <summary>True under Task Scheduler, a service, or with input redirected: as Python's "stdin is None or not
    /// a tty". A program with no console at all (a Windows exe) counts as redirected, so it's unattended too.</summary>
    public static bool Now()
    {
        if (!Environment.UserInteractive)
            return true;
        try
        {
            return Console.IsInputRedirected;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
