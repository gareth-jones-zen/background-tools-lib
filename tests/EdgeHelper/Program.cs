using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EdgeDevTools;
using PeopleFirstClient;

// EdgeHelper smoke <profile>         launch a headless Edge on a throwaway profile, check pages, script, the probe and
//                                    the close; prints "ok" lines, exits 0 (no network: a data: page only)
// EdgeHelper hold <profile>          launch a headless Edge, print "edge <pid>", then wait on stdin (a test kills us)
// EdgeHelper quiet <profile> <result>
//                                    run with a (hidden) console of its own: launch a headless Edge told to log
//                                    verbosely to stderr, load a data: page, close it, then read this console's text
//                                    and write how many characters Edge left there to <result> (0 is right)
// EdgeHelper lock-name <path>        print People First's lock name for the profile path
// EdgeHelper people-first <profile> <url> <account> <out.json>
//                                    the live read-only check: sign in on the profile (a copy), GET /profile and the
//                                    first pages of team/people and team, and write each body's SHA-256 and length.
//                                    Prints only names, statuses and counts: never a value from People First.
return args switch
{
    ["smoke", var profile] => await Smoke(profile),
    ["hold", var profile] => await Hold(profile),
    ["lock-name", var path] => LockName(path),
    ["quiet", var profile, var result] => await Quiet(profile, result),
    ["people-first", var profile, var url, var account, var output] => await PeopleFirst(profile, url, account, output),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: EdgeHelper smoke|hold <profile> | lock-name <path> | people-first <profile> <url> <account> <out.json>");
    return 2;
}

static async Task<int> Quiet(string profile, string result)
{
    await using (var browser = await EdgeBrowser.LaunchAsync(new EdgeLaunchOptions
    {
        UserDataDir = profile,
        ExtraArguments = ["--enable-logging=stderr", "--v=1"],
    }))
    {
        await browser.NewPageAsync("data:text/html,<title>quiet</title>");
        await Task.Delay(1500);
    }
    await Task.Delay(500);
    File.WriteAllText(result, ConsoleText.Read().Trim().Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return 0;
}

static int LockName(string path)
{
    Console.WriteLine(ProfileLock.NameFor(path));
    return 0;
}

static async Task<int> Smoke(string profile)
{
    var clock = Stopwatch.StartNew();
    var browser = await EdgeBrowser.LaunchAsync(new EdgeLaunchOptions { UserDataDir = profile });
    var pid = browser.Connection.Transport.ProcessId;
    Console.WriteLine($"ok launched {browser.Version.Split('/')[0]} in {clock.ElapsedMilliseconds} ms");
    try
    {
        if (!EdgeProfile.IsOpen(profile))
            return Fail("the profile isn't seen as open while Edge has it");
        Console.WriteLine("ok profile seen as open");
        var page = await browser.NewPageAsync("data:text/html,<title>Login - smoke</title><p>Hello</p><button>Organisation account</button>");
        string title = "";
        for (var i = 0; i < 50 && title != "Login - smoke"; i++)
        {
            title = (await browser.EvaluateAsync(page, "document.title")).GetString() ?? "";
            await Task.Delay(100);
        }
        if (title != "Login - smoke")
            return Fail("the page's title never arrived");
        Console.WriteLine("ok page and script");
        if (await browser.FindTextAsync(page, "Organisation account", exact: true) is not { } at)
            return Fail("FindText didn't find the button");
        await browser.ClickAsync(page, at.X, at.Y);
        Console.WriteLine("ok find text and click");
        var probe = MicrosoftSignIn.Parse(await browser.EvaluateAsync(page, MicrosoftSignIn.ProbeScript(Settings.AccountButton, "nobody@example.com")));
        if (probe is null || probe.Button is null || probe.Tile is not null || MicrosoftSignIn.Classify(probe) != SignInPage.NotMicrosoft)
            return Fail("the sign-in probe read the page wrongly");
        Console.WriteLine("ok sign-in probe");
    }
    finally
    {
        await browser.CloseAsync();
    }
    using (var process = TryProcess(pid))
        if (process is not null && !process.WaitForExit(5000))
            return Fail("Edge was still running after the close");
    for (var i = 0; i < 50 && EdgeProfile.IsOpen(profile); i++)
        await Task.Delay(100);
    if (EdgeProfile.IsOpen(profile))
        return Fail("the profile was still seen as open after the close");
    Console.WriteLine($"ok closed, {clock.ElapsedMilliseconds} ms in all");
    return 0;
}

static async Task<int> Hold(string profile)
{
    var browser = await EdgeBrowser.LaunchAsync(new EdgeLaunchOptions { UserDataDir = profile });
    Console.WriteLine($"edge {browser.Connection.Transport.ProcessId}");
    Console.Out.Flush();
    Console.ReadLine();
    await browser.CloseAsync();
    return 0;
}

static async Task<int> PeopleFirst(string profile, string url, string account, string output)
{
    var settings = new Settings(url: url, account: account, edgeProfile: profile);
    var clock = Stopwatch.StartNew();
    Api api;
    try
    {
        api = await Session.OpenAsync(settings, new SessionOptions { Log = Console.WriteLine });
    }
    catch (SignInNeededException e)
    {
        Console.WriteLine("sign-in needed: " + e.Message);
        return 3;
    }
    Console.WriteLine($"signed in in {clock.Elapsed.TotalSeconds:0.0} s");
    using (api)
    {
        var results = new List<(string Name, int Status, int Bytes, string Sha256)>();
        async Task<byte[]?> Get(string name, string path)
        {
            try
            {
                var body = await api.GetBytesAsync(path);
                results.Add((name, 200, body.Length, Convert.ToHexStringLower(SHA256.HashData(body))));
                return body;
            }
            catch (PeopleFirstException e)
            {
                results.Add((name, int.TryParse(e.Message.Split(' ')[3], out var s) ? s : -1, 0, ""));
                return null;
            }
        }
        var me = await api.MeAsync();
        await Get("profile", "/profile");
        await Get("team/people", $"/hrm/people/{me}/team/people?page%5BLimit%5D=100&page%5BOffset%5D=0");
        await Get("team", $"/hrm/people/{me}/team?page%5Blimit%5D=100&page%5Boffset%5D=0");
        var line = await ReportingLine.GetAsync(api, me, depth: 1);
        var members = await api.TeamMembersAsync(me);
        await using (var file = File.Create(output))
        await using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("reporting_line_depth1_count", line.Count);
            writer.WriteNumber("team_members_count", members.Count);
            writer.WriteStartArray("results");
            foreach (var r in results)
            {
                writer.WriteStartObject();
                writer.WriteString("name", r.Name);
                writer.WriteNumber("status", r.Status);
                writer.WriteNumber("bytes", r.Bytes);
                writer.WriteString("sha256", r.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        foreach (var r in results)
            Console.WriteLine($"  {r.Name}: {r.Status} bytes={r.Bytes}");
        Console.WriteLine($"  reporting line (depth 1): {line.Count} people; team members: {members.Count}");
    }
    return 0;
}

static Process? TryProcess(int pid)
{
    try
    {
        return pid == 0 ? null : Process.GetProcessById(pid);
    }
    catch (ArgumentException)
    {
        return null;   // already gone
    }
}

static int Fail(string why)
{
    Console.WriteLine("FAILED: " + why);
    return 1;
}

/// <summary>The text in this process's console window, whoever wrote it.</summary>
internal static partial class ConsoleText
{
    public static unsafe string Read()
    {
        var output = CreateFileW("CONOUT$", 0xC0000000, 3, 0, 3, 0, 0);
        if (output == -1)
            return "";
        try
        {
            var info = new byte[22];
            fixed (byte* buffer = info)
            {
                if (!GetConsoleScreenBufferInfo(output, buffer))
                    return "";
                int width = *(short*)buffer, height = *(short*)(buffer + 2);
                var text = new char[width * height];
                fixed (char* chars = text)
                    ReadConsoleOutputCharacterW(output, chars, (uint)text.Length, 0, out var read);
                return new string(text);
            }
        }
        finally
        {
            CloseHandle(output);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static unsafe partial bool GetConsoleScreenBufferInfo(nint console, byte* info);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static unsafe partial bool ReadConsoleOutputCharacterW(nint console, char* text, uint length, uint origin, out uint read);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
