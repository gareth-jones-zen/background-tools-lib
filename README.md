# background-tools-lib

Shared .NET libraries for a set of small personal background tools on Windows: watchers, sync jobs and helpers that run under Task Scheduler with nobody watching. The tools are being ported from Python to .NET 11 with Native AOT, and these are the pieces they have in common.

**This is a personal project. It is not an official Zen Internet product**, and it isn't supported by Zen.

## What's in it

| Project | What it does | Platforms |
|---|---|---|
| `src/WinBackground` | Running quietly in the background on Windows: power requests (keeping the machine awake), Task Scheduler tasks, single-instance locks between processes, a log that never throws, toast notifications, child processes without a console window flashing up | Windows only (`net11.0-windows`) |
| `src/M365Graph` | A small Microsoft Graph client: one sign-in per process, a token cache shared with the Python tools, requests with a timeout and retries, paging, and IPv4 first | Any (`net11.0`) |
| `src/ClaudeHeadless` | Running Claude Code headless (`claude -p`) from background tools, and surviving the usage limit: one job queue per seat, shared with the Python claude-headless. Also `claude-headless.exe` and its windowless twin | Windows only (`net11.0-windows`) |
| `src/PyCompat` | The bits of Python's behaviour a port has to reproduce exactly, so its files and output match the Python tool's byte for byte: `str` methods, `json.dumps`, `pathlib`, `datetime`, `re`, `html` and `difflib` | Any (`net11.0`) |
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

#### Added for the tools' ports (13 Nov 2026)

What teams-channel-notes', mail-triage's and onenote-sync's ports had each written for themselves:

```csharp
// subprocess.run(args, input=payload, cwd=folder, capture_output=True, text=True, encoding="utf-8", errors="replace")
var done = Shell.Run(hook[0], hook[1..], TimeSpan.FromSeconds(60), stdin: payload, workingDirectory: folder, utf8: true);

Console.SetOut(ConsoleText.Out);          // Unicode to a console, UTF-8 when redirected, as Python's
using var run = Locks.Exclusive(name, 600, "triage run");   // win_background's exclusive()
```

- **`Shell.Run`'s optional `stdin`, `workingDirectory` and `utf8`.** stdin is written in text mode (each LF as CRLF) and then closed, and a program that exits without reading it is no error. With `utf8`, stdin, stdout and stderr are UTF-8 with no byte order mark, bad bytes are read as U+FFFD, and the output has universal newlines. **Without them, nothing changes:** stdin is closed at once, output is in the console's code page, and only CRLF becomes LF.
- **A timeout throws `ShellTimeoutException`** (a `TimeoutException`, so existing catches still work), after killing the program and everything it started. Its message is the library's own; `PythonMessage` is Python's `TimeoutExpired` text (`Command '[...]' timed out after 60 seconds`), for a log that has to match. `Shell.WinErrorText(Win32Exception)` gives an `OSError`'s (`[WinError 2] The system cannot find the file specified`).
- **`ConsoleText.Out` and `.Error`**: `WriteConsoleW` to a console, so an emoji shows rather than `?`, and UTF-8 with no byte order mark when redirected. .NET's own `Console.Out` uses the console's code page for both.
- **`Locks.Exclusive(name, timeoutSeconds, what)`** returns the `NamedLock` held, or throws `TimeoutException` with Python's words: "Another {what} didn't finish within {minutes} minutes." (minutes rounded half to even, as `{:.0f}` does). `Locks.SingleInstance` is `single_instance()`.
- **`Logger` writes a newline inside a message as CRLF,** as Python's text mode did: a multi-line message (a diff, a hook's output) has each LF written as CRLF in the file, and as the echo writer's own `NewLine`. **Before 13 Nov 2026 an LF inside a message was written as it was.** A caller that converted LF to CRLF itself (onenote-sync's port) must stop, or it gets CR CR LF.
- **`Locks.Key` lower-cases as Python's `str.lower()` (v0.3),** through `PyStr.Lower` (WinBackground now references PyCompat), so a .NET and a Python tool always name the same lock. Before v0.3 it used .NET's invariant lower case, which differs only for U+0130 (capital I with dot) and a capital sigma ending a word: a key for a path with either of those changed with v0.3, and every other key is the same as before. Checked against `win_background.locks.key()` (`tests/WinBackground.Tests/V03Tests.cs`). One difference left: Python can't encode a lone surrogate and raises, where .NET hashes U+FFFD.

#### ITask's later members have defaults (v0.3)

`Enable(..., TaskSchedule)` and `Start()` were added to `ITask` after the ports had written test fakes, and every fake broke. They now have default implementations: the scheduled `Enable` with `TaskSchedule.Watcher` calls the three-argument `Enable` (as `ScheduledTask` does), any other schedule throws `NotSupportedException`, and `Start()` throws `NotSupportedException`. A fake that needs them overrides them.

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

#### A fake handler for tests, raw requests, and connect failures (13 Nov 2026)

```csharp
// A tool's adapter test: every request through a fake handler, a token with no MSAL, waits recorded.
var graph = new GraphClient(options, fakeHandler, token: (scopes, _) => Task.FromResult("t"),
    delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, clock: () => now);

// Bytes in, bytes out: the Content-Type exactly as given, the reply as served, the status returned.
var page = await graph.SendRawAsync(HttpMethod.Get, $"/me/onenote/pages/{id}/content");
var reply = await graph.SendRawAsync(HttpMethod.Patch, url, body, "application/json", GraphRetryPolicy.Write with { Tries = 5 });
if (!reply.IsSuccess) ...   // reply.Status, reply.Body
```

- **`new GraphClient(options, handler, token?, delay?, clock?)`** sends every request through the handler. With `token`, MSAL is never used (it's given the scopes asked for and returns a token); without it, the sign-in is the usual one through the cache file, with MSAL's calls through the handler too, and isn't shared with other clients. `clock` is "now" for a `Retry-After` date.
- **`SendRawAsync(method, url, body, contentType, policy, scopes, headers)`** sends the body's bytes with the `Content-Type` exactly as given (no `; charset=utf-8`, which `StringContent` adds), and returns `GraphRawResponse(Status, Body, ContentType)` whatever the status. The default client for raw calls asks for no compression, so the bytes arrive as served (Python's urllib didn't ask either).
- **`GraphRetryPolicy(Tries, Statuses, ResendAfterDelivery)`** per call. `Read`: 429 and 5xx retried, and a dropped connection. `Write`: only a 429, or a request that never connected, is sent again; a timeout or a dropped connection after sending throws `GraphDeliveryUnknownException`, because a retried insert or create could be applied twice. Both default to `Graph.Retries` (3) tries; `with { Tries = 5 }` for more. Retries are logged through `Options.Log`, as other calls' are.
- **Connect failures say so by type.** The IPv4-first connect throws `HttpRequestException` with `HttpRequestError.NameResolutionError` or `ConnectionError` (SocketsHttpHandler wraps it in its own `ConnectionError`), and **`Net.NeverSent(error)`** reads the chain: true when the request certainly never reached the server, so it's safe to resend a write. No message matching.
- `Net.CreateHandler(decompress: false)` gives the IPv4-first handler without compression.

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

One set of Python-compatibility helpers, merged on 13 Nov 2026 from the ports' own copies (ai-keep-awake, claude-headless, mail-triage, onenote-sync, teams-channel-notes, and EdgeDevTools' `str.lower()`). Where two ports had written the same thing differently, the more complete one was kept and both ports' tests run against it.

| Class | Python | What's in it |
|---|---|---|
| `PyStr` | `str` | `IsSpace` (U+001C to U+001F included; checked against every BMP character), `Strip`/`LStrip`/`RStrip` (and with chars), `Split`, `Collapse`, `SplitLines` (and keep-ends), `Length`/`Head`/`Tail` by code point, `IsDigit`, `Int` (any script's digits, sign, underscores), `Lower` and `CaseFold` (Python's full mappings and final-sigma rule, from Python's tables), `ExpandTabs`, `CompareCodePoints`/`Order`, text-mode files (`ReadText`, `WriteText`, newlines), `SpaceClass`, `Space` and `NotSpace` for a tool's own regexes |
| `Py` | values | Truthiness and `int`/`float`/`str` of JSON values, `repr` of a float or a string (non-printable characters escaped as Python does), `{:.Nf}` and `{:g}`, `From` (a slice from a negative start) |
| `PyFormat` | `str.format` | `Format(template, values)` for `{name}`, `{name!s}`/`{name!r}`, a date's strftime spec (`{date:%Y-%m}`, `%z` on an aware time) and `str()` of dates and datetimes, text's and whole numbers' specs (fill, align, sign, width, precision, grouping); Python's errors as `PyFormatException` (`Kind` is `KeyError`, `IndexError`, `ValueError` or `TypeError`, with Python's message); `Problem` refuses at load what isn't supported (`{a.b}`, `{a[0]}`, nested fields, `!a`, other strftime codes); `FieldNames`. Merged from ev-charging's, mail-triage's and teams-channel-notes' templates, for teams-transcripts' |
| `PyUtf8` | `bytes.decode` | `Decode` throws `DecoderFallbackException` with Python's `UnicodeDecodeError` message, the bad bytes and Python's position; `FirstError` finds it as CPython's decoder does. `PyStr.ReadText` uses it |
| `PyJson` | `json` | `Parse` (a duplicate key as a dict keeps it), `Dump(node, indent, ensureAscii, sortKeys)` byte for byte in every mode, `Quote` for one string, Python's `==` |
| `PyPath` | `pathlib`, `ntpath` | `Pure`, `IsAbsolute`, `Home`, `ExpandUser`, `ExpandVars` (`%VAR%`, `$VAR`, `${VAR}`), `Resolve` (links followed and the case corrected, through the final path, as `Path.resolve()` does), `ResolveAgainst`, `Absolute` (by the text alone), `Same` |
| `PyTime` | `datetime`, `zoneinfo` | `Zone` (IANA or Windows names), `At`/`Offset` (a wall time with fold=0), `Local`, `Now`, `AddWall`, `Iso` and `IsoFormat` in every timespec, `FromIso`/`FromIsoFormat`/`TimeFromIso` (an exact port of CPython's `fromisoformat`, every form 3.14 accepts, "24:00" included), `Strftime` (a checked subset, C locale), `Weekday`, `Micro`, `TimeMax` |
| `PyRegex` | `re` | Python's patterns translated for .NET, each class and escape worked out as the code points Python matches (`\w`, `\d`, `\s`, `.`, `[...]` from Python's tables, an emoji matched whole), Python's IGNORECASE (`_sre`'s lower case and `re._casefix`), named groups with Python's numbering (`PyMatch`), `{,n}`, scoped flags; a guard that refuses what the two read differently; `Search`, `Find`, `FindIter`, `MatchStart`, `FullMatch`, `FindAll`, `Sub`, `Split` stepping as Python does, `Escape`; a time limit per match. First written for mail-triage |
| `PyHtml` | `html` | `Unescape` exactly (all 2,231 HTML5 names, longest prefix, the numeric-reference rules), `Escape(text, quote)`, `IsEntity` |
| `UnifiedDiff` | `difflib` | `unified_diff` line for line, including SequenceMatcher's popular-lines rule: `Of` (lines with endings) and `Lines` (`lineterm=""`) |

**Deliberate behaviour changes for callers of the 12 Nov version:** `PyPath.Resolve` now follows links and corrects case (the old text-only behaviour is `PyPath.Absolute`), and so does `ResolveAgainst`; `PyTime.Zone("UTC")` is `TimeZoneInfo.Utc`; `FromIsoFormat` accepts every form Python does and keeps microseconds only, as Python does (it took up to seven fraction digits before).

**Checked against Python itself.** `tools/gen/gen_tables.py` writes the HTML5 entity table, the whitespace table and `PyUnicode.g.cs` (re's `\w`, `\d` and `\s`, `str.isprintable`, cased and case-ignorable characters, the full lower and casefold mappings, and `_sre`'s cased set, simple lower case and `_EXTRA_CASES`) from Python's own data, so none of it depends on .NET's Unicode version; `tools/gen/gen_expected.py` writes the expected values for `fromisoformat`, `time.fromisoformat`, `unified_diff`, `json.dumps` in four modes, `html.unescape`, `str.lower`, `str.casefold`, `repr`, `read_text`'s decode errors, `re` (finditer, sub and split over 70 patterns, and IGNORECASE over every cased character for 45 patterns), `str.format` (91 templates) and `int` (`tests/PyCompat.Tests/Generated`). Each carries the Python version it came from (3.14.5, what the Python tools run); re-run both after a Python upgrade and diff.

#### v0.3 (13 Nov 2026): what changed for callers

- **`PyRegex` reads more patterns the way Python does, and refuses fewer.** Patterns refused before and now translated: `(?P<name>...)` and `(?P=name)`, `{,n}`, `(?u)`, `\z`, `\U0001F600`, `(?>...)`, `(?#...)`, `(?-i:...)`, and classes .NET would have read its own way (`[\W]`, `[[:alpha:]]`, `[a-z-[aeiou]]`), so `Problem` now accepts them, as Python does. **Matching changed only where it used to differ from Python:** an emoji is one character for `.`, `\W`, `\D`, `\S` and negated classes (it was two halves); IGNORECASE folds exactly as Python's (İ and ı in a pattern included); after an empty match a non-empty one may start at the same place. The options from `Translate` no longer include `IgnoreCase`: case-folding is written into the pattern. **`Search` and `MatchStart` still return .NET's `Match`, numbered as before** (a pattern with named groups numbers them last: use `Find`, `MatchGroups` or `Wrap` for Python's numbering). `FindAll` with one group now gives the group, as Python's does (it gave the whole match), and refuses more than one; `Split` refuses a pattern with groups; `Sub` refuses a backslash in the replacement. Fuzzed against Python with 10,000 random patterns over 40,000 texts: 3 differ, all one engine difference (a group inside a repeated group whose iterations match empty: .NET reports None where Python reports ""), now documented on the class.
- **`PyStr.Lower` comes from Python's table**, so it no longer depends on .NET's Unicode version, and keeps a lone surrogate (it became U+FFFD).
- **`PyStr.ReadText`'s error message is Python's**; it's still a `DecoderFallbackException`, so existing catches work. A port that rewrote the message itself can drop that.
- **`Py.ReprString` escapes what Python's `repr` escapes**: a no-break space is `\xa0`, a zero-width space `​`, a lone surrogate `\ud800`. Messages that quote such text now match Python's.

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
- **`EdgeProfile`**: `Key(path)` is the 16-character key win-background (Python) gives the same profile, byte for byte: it spells the path as `str(pathlib.Path(os.path.abspath(p)))` does and lower-cases as Python's `str.lower()` does (`PyStr.Lower`, which differs from .NET's for U+0130 and a final capital sigma). So a .NET and a Python tool sharing a profile lock the same mutex. `IsOpen(path)` says whether an Edge has the profile open (Edge holds `lockfile` in it, shared for reading only, and deletes it on close); `LaunchAsync` checks it rather than handing a second Edge to the first.
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

`tests/LockHelper` is a small console used by the lock and `Shell.Run` tests as another process. CI also publishes it with Native AOT and runs `async` (the locks) and `aot-smoke` (the PyCompat, WinBackground and M365Graph additions of 13 Nov 2026, with a fake Graph handler and no network) under AOT.

`tests/PyCompat.Tests/MergedTests.cs` holds the ports' tests for the merged helpers and the checks against the Python-generated tables; one compares `PyPath.Resolve` with `pathlib` and is skipped where `python` isn't on the `PATH`.

`tests/EdgeHelper` is the same for Edge: the child process for the "Edge dies with the process that started it" test, and the Native AOT check of `EdgeDevTools` and `PeopleFirstClient`, which CI publishes and runs against Edge on a throwaway profile (`smoke`: a `data:` page only, no network). Its `people-first` command is the live read-only comparison with Python; it prints only names, statuses and counts.

`tests/EdgeDevTools.Tests` runs the real Edge, headless, on new empty profiles in the temp folder (skipped where Edge isn't installed). `tests/PeopleFirstClient.Tests` has the Python library's 9 tests ported, the sign-in paths against a fake browser, and two tests against Python itself (lock names, including this machine's People First profile path, and the mutex held by each side in turn), skipped unless Python and checkouts of `people-first-client` and `win-background` are found (`PEOPLE_FIRST_PYTHON`, `PEOPLE_FIRST_CLIENT_SRC`, `WIN_BACKGROUND_SRC`, or the usual places). Native AOT publishing needs the Visual Studio C++ build tools; from Git Bash, put `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on the `PATH` so the SDK can find them.

## Licence

None yet. Until there is one, all rights are reserved.
