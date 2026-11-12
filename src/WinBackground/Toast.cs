namespace WinBackground;

/// <summary>Windows notifications (toasts), for tools that run with nobody watching.</summary>
public static class Toast
{
    // Shown as coming from Windows PowerShell, which every Windows machine has registered as an app.
    // A tool of our own would need registering (a Start menu shortcut with an AppUserModelID) first.
    private const string App = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>The PowerShell that shows it, for tests.</summary>
    public static string Script(string title, IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        if (text.Length > 600)
            text = text[..600];
        return $"""

            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
            $t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
            $t.GetElementsByTagName('text').Item(0).AppendChild($t.CreateTextNode({Shell.Quote(title)})) | Out-Null
            $t.GetElementsByTagName('text').Item(1).AppendChild($t.CreateTextNode({Shell.Quote(text)})) | Out-Null
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier({Shell.Quote(App)}).Show([Windows.UI.Notifications.ToastNotification]::new($t))

            """;
    }

    /// <summary>Show a notification: a title and up to 600 characters of text. Best effort, always: it never
    /// throws, because a failed notification must never stop the run it's reporting on.</summary>
    public static void Notify(string title, IEnumerable<string> lines)
    {
        try
        {
            Shell.PowerShell(Script(title, lines), 30);
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
