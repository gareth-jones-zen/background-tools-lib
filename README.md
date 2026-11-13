# background-tools-lib

Shared .NET libraries for a set of small personal background tools on Windows: watchers, sync jobs and helpers that run under Task Scheduler with nobody watching. The tools are being ported from Python to .NET 11 with Native AOT, and these are the pieces they have in common.

**This is a personal project. It is not an official Zen Internet product**, and it isn't supported by Zen.

## What's in it

| Project | What it does | Platforms |
|---|---|---|
| `src/WinBackground` | Running quietly in the background on Windows: power requests (keeping the machine awake), Task Scheduler tasks, single-instance locks between processes, a log that never throws, toast notifications, child processes without a console window flashing up | Windows only (`net11.0-windows`) |
| `src/M365Graph` | A small Microsoft Graph client: one sign-in per process, a token cache shared with the Python tools, requests with a timeout and retries, paging, and IPv4 first | Any (`net11.0`) |
| `src/ClaudeHeadless` | Running Claude Code headless (`claude -p`) from background tools, and surviving the usage limit: one job queue per seat, shared with the Python claude-headless. Also `claude-headless.exe` and its windowless twin | Windows only (`net11.0-windows`) |
| `src/PyCompat` | The bits of Python's behaviour a port has to reproduce exactly, so its files and output match the Python tool's byte for byte | Any (`net11.0`) |
| `src/EdgeDevTools` | The installed Microsoft Edge driven over the DevTools protocol through a pipe, with no Playwright or Node: launch on a profile, pages, script, clicks, request headers, a clean close, and Edge ending with the process that started it. Also the Edge profile's lock key (the same as the Python tools') and Microsoft sign-in page recognition | Windows only (`net11.0-windows`) |
| `src/PeopleFirstClient` | People First (MHR's HR system) read as you through its web app's own single sign-on: a hidden Edge takes the token, then HttpClient reads the API. GET only. A port of `people-first-client` | Windows only (`net11.0-windows`) |

Windows-only code lives in the Windows-only projects (`WinBackground`, `ClaudeHeadless`, and `EdgeDevTools` and `PeopleFirstClient`, which drive Edge on Windows), so `M365Graph` and `PyCompat` can later build for other platforms, and any of them could become a NuGet package.

All the libraries are AOT- and trim-checked (`IsAotCompatible`), build with warnings as errors, and publish under Native AOT with no warnings.

### WinBackground

Copied from the `dotnet` branch of [ai-keep-awake](https://github.com/gareth-jones-zen/ai-keep-awake), where it was first written as the port of the Python `win-background` package. Same namespace, same behaviour, apart from `NamedLock` (below).

#### Locks between processes

```csharp
using WinBackground;

using var watcher = Locks.SingleInstance("my-tool-watch");   // null: another one is running
if (watcher is null)
    return;

using var run = new NamedLock("my-tool-run-" + Locks.Key(configPath));
if (!await run.AcquireAsync(timeoutSeconds: 600, cancellationToken))
    throw new TimeoutException("Another run didn't finish within 10 minutes.");
await DoTheRunAsync();   // any number of awaits: release works from whichever thread finishes
run.Release();
```

A `NamedLock` is a mutex named `Local\{name}`, the same object the Python `win-background` locks create, so .NET and Python tools exclude each other by name. Windows releases a mutex when the process holding it ends (it's "abandoned"), so a crash never leaves a lock stuck.

**Each held lock has a small thread of its own** (fixed 12 Nov 2026). A Win32 mutex belongs to the thread that took it, and `ReleaseMutex` fails on any other thread. The first version took and released the mutex on the caller's thread, so in async code (acquire, `await`, release) the release ran on another thread, failed without a word, and left the lock held until the process exited whenever another process had the mutex open, such as one waiting for it. Now the lock's own thread takes the mutex, waits, and releases it when asked, so:

- `Acquire`, `AcquireAsync`, `Release` and `Dispose` work from any thread, either side of an `await`;
- `Acquire(timeoutSeconds)` waits up to that long; 0 tries once, and a negative timeout waits indefinitely. `AcquireAsync` waits without blocking a thread and can be cancelled;
- releasing twice, or disposing after a release, is safe; a release that Windows refuses throws `InvalidOperationException` rather than passing silently;
- `Dispose` without `Release` releases it, and stops an acquire in progress (which throws `ObjectDisposedException`);
- `WasAbandoned` says the last holder ended without releasing it. The lock is still yours;
- the thread is a background thread, so a lock that's never released (a watcher's single-instance lock) never keeps the process alive, and is freed as abandoned when the process ends.

**Differences from the first version, and from Python:**

- **Not re-entrant.** Two `NamedLock`s with the same name exclude each other inside one process, even on one thread. A bare mutex let the same thread take it twice.
- **Acquiring twice with one object** throws `InvalidOperationException`, and a disposed object can't be acquired again (`ObjectDisposedException`). The first version overwrote its handle.
- **A mutex made by a process this one can't open** (an elevated one) counts as held by someone else: `Acquire` returns false, as in Python. Any other failure to open it throws.

`Locks.Held(name)` says whether any process (this one included) has the lock, **without ever taking it**, for a `status` command to see whether the watcher is running. A probe that took the lock, even for a moment, could make a watcher starting at that moment think another was running and exit. It also says true while a `NamedLock` is waiting for it, as the waiter has the mutex open.

#### Fixes from ai-keep-awake's review (12 Nov 2026)

Brought over once ai-keep-awake had them, so the two copies are one again:

- **System programs by their full path.** `Shell.PowerShell` runs `Shell.PowerShellExe` (Windows PowerShell 5.1 in System32) and the powercfg fallback runs `Power.PowercfgExe`. Windows looks in the program's own folder and the current folder before System32, and an exe is often run from wherever it was unzipped, so a stray `powershell.exe` there would run instead. `Shell.SystemProgram(...)` gives any other System32 program.
- **`Shell.Quote` doubles the curly single quotes (U+2018 to U+201B) as well as `'`.** PowerShell ends a single-quoted string at any of them, so a path such as `C:\Users\O’Brien` ended the string early: a syntax error at best, a way to inject script at worst.
- **Stopping a task no longer waits for its own PowerShell.** The script that registers a task contains the task's arguments, so the wait for the old process found the script itself and always ran the full 30 seconds. `ScheduledTask.AliveFilter` leaves out `$PID`.
- **`ITask.Start()`** starts the task as it is, to undo a `Stop` when what came after it failed. False if there's no task.

### M365Graph

A port of the Python library `m365-graph` (0.1.2), with the same capabilities and much the same shape:

```csharp
using M365Graph;

var graph = new GraphClient(new GraphClientOptions
{
    CachePath = @"C:\Users\me\AppData\Local\my-tools\msal-cache.json",
    Tenant = "<tenant id>",
    LoginHint = "me@example.com",
    SignInHint = "Run my-tool sign-in at a console.",
});

var me = await graph.GetAsync("/me?$select=displayName");
await foreach (var message in graph.PagesAsync("/me/messages?$top=50", ["Mail.Read"]))
    Console.WriteLine((string?)message!["subject"]);
```

- **One sign-in per process.** Every `GraphClient` on the same cache file, client and tenant shares one MSAL app and its tokens.
- **No browser when nobody's there.** Under Task Scheduler, a service, or with input redirected, a missing sign-in throws `SignInNeededException` (its message includes your `SignInHint`) rather than opening a browser nobody will see. `Interactive = true` or `false` overrides the check.
- **Scopes per call**, so a new consent for one tool never breaks another.
- **A 60-second timeout and three tries** for 429, 500, 502, 503, 504 and dropped connections (honouring `Retry-After`, otherwise 5 s then 10 s). The silent sign-in refresh is retried the same way on a dropped connection.
- **IPv4 first**, through `SocketsHttpHandler.ConnectCallback`, for Graph calls and MSAL's sign-in calls alike. login.microsoftonline.com lists its IPv6 addresses first, and on a network where IPv6 to Microsoft goes nowhere, trying them in order stalled sign-ins for minutes.
- **Typed replies through source generation**: `GetAsync(url, MyJsonContext.Default.MyType)`, as well as `JsonObject` replies. No Graph SDK.

#### Sharing the token cache with the Python tools

During the move from Python, .NET and Python tools sign in through **the same cache file**, at the same time. That works because:

- both use MSAL's unified cache format (`SerializeMsalV3` in MSAL.NET), with the same client (Microsoft's "Graph Command Line Tools" app by default) and authority;
- MSAL.NET's **instance discovery is off**, so it files tokens under `login.microsoftonline.com`, as MSAL Python does. With it on, MSAL.NET used `login.windows.net`; each side could still read the other's tokens, but the file held two of everything and neither reused the other's access tokens;
- writes follow m365-graph 0.1.1: a temporary file beside the cache, then one atomic replace, with Windows' brief sharing locks retried;
- a damaged file (stray text after the JSON, as two interleaved writers once left) is read up to the end of its first complete JSON value.

There is **no lock between processes**, because the Python tools take none: the last writer wins, which is safe because every refresh token written stays valid.

This was checked against a copy of a real cache: .NET redeemed a refresh token Python had written, Python redeemed one .NET had written, and each reused the access token the other had just written (also from a Native AOT build).

#### Deliberate differences from the Python library

- **Async.** `TokenAsync`, `RequestAsync`, `GetAsync` and `PagesAsync` (an `IAsyncEnumerable`), each taking a `CancellationToken`.
- **The cache file is re-read when another program has replaced it.** Python loads it once per process. A long-running .NET watcher therefore picks up a refresh a Python tool made, rather than writing its older view back over it.
- **Each address gets 20 seconds to connect** before the next is tried. Python waits the full timeout on each; with IPv4 first this only matters if an IPv4 address is dead too.
- **Errors.** A non-retried HTTP error is a `GraphRequestException` (status and body); a sign-in that can't happen is a `SignInNeededException`.
- **Instance discovery is off** (see above), which also saves a call to login.microsoftonline.com.
- **No command line.** Python's `python -m m365_graph sign-in` stays the way to sign in for now; a tool can call `TokenAsync(interactive: true)` from a console to do the same.

### ClaudeHeadless

A port of the Python library `claude-headless` (0.1.0):

```csharp
using ClaudeHeadless;

IClaudeRunner claude = new ClaudeRunner();
try
{
    var result = claude.Run(["-p", "--output-format", "json", "--model", "opus"], prompt, cwd, TimeSpan.FromMinutes(30));
    var text = (string?)result["result"];
}
catch (LimitReachedException limit)
{
    if (JobQueue.Default.Retry([exe, "draft", path], cwd, limit.Reset))
        Toast.Notify("Claude usage limit reached", [$"Carries on after {limit.Reset:HH:mm}"]);
}
```

```
claude-headless status        # the limit, and what's queued
claude-headless run-queue     # run what's queued now, if the limit has passed
claude-headless autostart on  # run the queue every 15 minutes and at logon (claude-headless-background.exe)
```

- **One queue per seat**, in `%LOCALAPPDATA%/claude-headless` (or `CLAUDE_HEADLESS_HOME`), whichever tool queued the job and whichever language it's written in. `queue.json` and `limit.json` are written **byte for byte as the Python version writes them** (checked against Python on a copy of a real queue), and the runner takes **the same named lock** (`claude-headless-runner-<key of the folder>`), so Python and .NET tools can share one queue while they move across, and only one runner of either kind works it at a time.
- **The limit is read from whichever shape it comes in**: text on stderr, a JSON result with `is_error`, or an API 429. The reset time is read from the message as a wall-clock time in `CLAUDE_HEADLESS_TZ` (Europe/London by default), with Python's choice of offset in the hour the clocks change; if it can't be read, an hour from now.
- **Once a limit is known, later runs don't start** until it has passed (plus five minutes). **One notification per limit:** `Retry()` returns true only the first time a limit is seen.
- **The prompt goes on stdin with Windows line ends**, exactly as Python's text-mode pipe sent it, and the output is read as UTF-8 with universal newlines.
- **Tools take `IClaudeRunner`**, so their tests give a fake and never spend usage.

#### Deliberate differences from the Python version

- **The current folder isn't searched for `claude`.** Python 3.12 and later look there first on Windows, so a stray `claude.cmd` in a workspace could be run.
- **A job that runs past its timeout is killed with everything it started** (Python kills only the job itself), and **a job's stdin is closed** rather than inherited, so a job can never wait on a console.
- **A queue file that isn't the expected shape is read as empty** rather than stopping the runner, and a time with no offset is taken as being in the seat's zone.
- **Errors are .NET exceptions:** `LimitReachedException` (with `Reset`), `ClaudeRunException` (Python's RuntimeError) and `TimeoutException`. Log lines for a failed job name the .NET exception, so their text differs.
- **`autostart on` runs `claude-headless-background.exe`** (no window) under the same task name, `claude-headless run-queue`, so switching from Python replaces the Python task rather than adding a second. `TaskSchedule` was added to WinBackground's `ScheduledTask` for this (a job every N minutes with a time limit, as win-background's `Task.enable` does); the watcher script is unchanged.

### PyCompat

`Py`, `PyJson` and `PyPath` were first written for ai-keep-awake's port, and are copied here unchanged apart from the namespace. `PyStr` (Python's whitespace, code-point slices, splitlines, text-mode files) and `PyTime` (zoneinfo-style wall-clock arithmetic, isoformat) are new. Each has tests, with expected values printed by Python 3.14.

**A trap worth knowing:** a C# hex escape such as the one for U+001C takes up to four hex digits, so when it's followed by a letter that is also a hex digit it swallows it. Use the four-digit `u` escape in tests.
### EdgeDevTools

Drives the installed Edge directly over the DevTools protocol, for tools that only need a browser for a sign-in. Playwright for .NET can't be compiled with Native AOT and brings Node (about 110 MB); this is a few hundred KB of AOT-clean code, and a tool built on it publishes as a single exe of about 6 MB.

```csharp
using EdgeDevTools;

await using var edge = await EdgeBrowser.LaunchAsync(new EdgeLaunchOptions { UserDataDir = profile });   // hidden
edge.RequestSent += request => { /* request.Url, request.Header("authorization") */ };
var page = await edge.NewPageAsync("https://example.com/");
var title = (await edge.EvaluateAsync(page, "document.title")).GetString();
if (await edge.FindTextAsync(page, "Sign in", exact: true) is { } at)
    await edge.ClickAsync(page, at.X, at.Y);
```

- **The pipe, not a port.** Edge is started with `--remote-debugging-pipe`, so nothing listens on the network. .NET's `Process` can't hand a child the extra file descriptors (3 and 4) that needs, so `PipeTransport` calls `CreateProcessW` and passes them the way the C runtime inherits them (`STARTUPINFO.lpReserved2`), as Node does.
- **Edge dies with the tool.** It runs in a job object that ends every process in it when the tool's handle closes, however the tool ends; Edge also exits when the pipe closes. `CloseAsync` (or disposing) asks Edge to close, waits up to 15 seconds, then ends it.
- **Every page, frame and worker is attached from its first request** (`Target.setAutoAttach` with each one paused until its network events are on), so a sign-in pop-up or a redirect through another site can't slip past. `RequestSent` merges `requestWillBeSent` with its extra-info event, which carries the headers the page's own list leaves out.
- **No reflection:** messages are written with `Utf8JsonWriter` and read with `JsonDocument`.
- **It only reads and clicks.** Nothing in it reads or changes cookies or storage, or types into a page.
- **`EdgeProfile`**: `Key(path)` is the 16-character key win-background (Python) gives the same profile, byte for byte: it spells the path as `str(pathlib.Path(os.path.abspath(p)))` does and lower-cases as Python's `str.lower()` does (which differs from .NET's for U+0130 and a final capital sigma). So a .NET and a Python tool sharing a profile lock the same mutex. `IsOpen(path)` says whether an Edge has the profile open (Edge holds `lockfile` in it, shared for reading only, and deletes it on close); `LaunchAsync` checks it rather than handing a second Edge to the first.
- **`MicrosoftSignIn`**: one probe script per tab reports what's showing (the address, a button, an account tile, password, email, code and approval prompts, Windows Hello), and `Classify` decides, in C# where it's tested without a browser, whether it's the account picker (which needs no one) or something that needs a person.

It lives apart from `WinBackground` because it's about Edge, and so `EdgeProfile` sits with it: a future Playwright-based project can reference this small project for the profile key.

### PeopleFirstClient

A port of the Python [people-first-client](https://github.com/gareth-jones-zen/people-first-client) 0.1.0, for org-directory and team-leave.

```csharp
using PeopleFirstClient;

var settings = new Settings(url: "https://yourorg.people-first.com", account: "you@example.com",
                            edgeProfile: @"C:\Users\you\AppData\Local\my-tool\edge-profile",
                            signInHint: "At a console, run: my-tool sign-in");
using var api = await Session.OpenAsync(settings, new SessionOptions { Log = Console.WriteLine });
var me = await api.MeAsync();
foreach (var person in await api.TeamMembersAsync(me))
{
    var absences = await api.EventsAsync(person.GetProperty("personId").GetString()!, start, end);
}
```

- **The same API as Python:** `Settings` (the url loses trailing slashes; the tenant code defaults to the url's first part), a GET-only `Api` (`MeAsync`, `TeamAsync`, `TeamMembersAsync`, `EventsAsync`, `ContactAsync`, `GetAsync`, and `GetBytesAsync` for raw bodies), `ReportingLine.GetAsync`, `SignInWindow.RunAsync` (a visible window for a sign-in that needs a person), `SignInNeededException` (its message ends with the settings' sign-in hint) and `PeopleFirstException`. Paging, query strings and error messages are Python's.
- **Edge closes as soon as the token is captured.** People First needs only the bearer token, `TenantCode`, an empty `EnvironmentCode` and `Accept`, and no cookies, so the API is read with `HttpClient` (IPv4 first, 60-second timeout) after the browser has gone. Checked on a copy of a real profile on 13 Nov 2026: `/profile`, `team/people` and `team` were byte-identical to Python's, compared by hash and length only.
- **A sign-in that needs a person stops at once.** Python's only check was for a password box, so an Authenticator prompt, Windows Hello or an email box sat there for the full 3 minutes (found by the DevTools spike, 12 Nov 2026). Now, unattended, any Microsoft page that needs a person raises `SignInNeededException` on the next look, and any other Microsoft page still showing 20 seconds after the account tile was clicked (or after it appeared) does too. Redirects through Microsoft pass in a second or two, so they aren't cut short. A sign-in window never stops early: a person is there.
- **The profile lock** is `people-first-edge-{EdgeProfile.Key(profile)}`, the same named mutex as Python's, so Python and .NET tools take turns on a shared profile (tested both ways against Python itself). Without a held lock, `Session.OpenAsync` takes it only while Edge is open, a few seconds.
- **Hold one `ProfileLock` for a run.** Python's lock is re-entrant within a process; `NamedLock` isn't. A tool that wants the profile for more than one sign-in takes `ProfileLock.AcquireAsync(profile)` once and passes it in `SessionOptions.HeldLock`. Taking a second one for the same profile while the process holds the first throws `InvalidOperationException` at once, rather than waiting 15 minutes on itself.

**Before sharing a tool built on it:** the token is taken from People First's web app, and the tool isn't a registered API client. That's fine for reading your own permitted data on your own machine; distributing it to other people needs clearing with the HR system's owners and information security first (as the Python library's README says).

## Using it from a tool

Tools reference these libraries as a **sibling checkout**: clone this repo next to the tool's repo, and add project references.

```
source/repos/
  background-tools-lib/   <- this repo
  my-tool/
    src/MyTool/MyTool.csproj
```

```xml
<ItemGroup>
  <ProjectReference Include="..\..\..\background-tools-lib\src\M365Graph\M365Graph.csproj" />
  <ProjectReference Include="..\..\..\background-tools-lib\src\WinBackground\WinBackground.csproj" />
</ItemGroup>
```

A project that references `WinBackground` has to target `net11.0-windows` too. In a GitHub workflow, check out both repositories side by side (`actions/checkout` with `path:`).

## Building

Needs the .NET 11 SDK (`global.json` pins 11.0.100, rolling forward to later feature bands). Tests use xunit v3 on the Microsoft Testing Platform.

```
dotnet build -c Release
dotnet test -c Release
```

`tests/M365Graph.Tests` has one live test against the real Graph, skipped unless `M365GRAPH_LIVE_CACHE` names a token cache file (and `M365GRAPH_LIVE_TENANT` its tenant). It copies the file first and works on the copy, silently only, and reads only `/me`.

`tests/LockHelper` is a small console used by the lock tests as another process. CI also publishes it with Native AOT, which checks the locks under AOT.

`tests/EdgeHelper` is the same for Edge: the child process for the "Edge dies with the process that started it" test, and the Native AOT check of `EdgeDevTools` and `PeopleFirstClient`, which CI publishes and runs against Edge on a throwaway profile (`smoke`: a `data:` page only, no network). Its `people-first` command is the live read-only comparison with Python; it prints only names, statuses and counts.

`tests/EdgeDevTools.Tests` runs the real Edge, headless, on new empty profiles in the temp folder (skipped where Edge isn't installed). `tests/PeopleFirstClient.Tests` has the Python library's 9 tests ported, the sign-in paths against a fake browser, and two tests against Python itself (lock names, including this machine's People First profile path, and the mutex held by each side in turn), skipped unless Python and checkouts of `people-first-client` and `win-background` are found (`PEOPLE_FIRST_PYTHON`, `PEOPLE_FIRST_CLIENT_SRC`, `WIN_BACKGROUND_SRC`, or the usual places). Native AOT publishing needs the Visual Studio C++ build tools; from Git Bash, put `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on the `PATH` so the SDK can find them.

## Licence

None yet. Until there is one, all rights are reserved.
