# background-tools-lib

Shared .NET libraries for a set of small personal background tools on Windows: watchers, sync jobs and helpers that run under Task Scheduler with nobody watching. The tools are being ported from Python to .NET 11 with Native AOT, and these are the pieces they have in common.

**This is a personal project. It is not an official Zen Internet product**, and it isn't supported by Zen.

## What's in it

| Project | What it does | Platforms |
|---|---|---|
| `src/WinBackground` | Running quietly in the background on Windows: power requests (keeping the machine awake), Task Scheduler tasks, single-instance locks between processes, a log that never throws, toast notifications, child processes without a console window flashing up | Windows only (`net11.0-windows`) |
| `src/M365Graph` | A small Microsoft Graph client: one sign-in per process, a token cache shared with the Python tools, requests with a timeout and retries, paging, and IPv4 first | Any (`net11.0`) |

Windows-only code lives in `WinBackground` and nowhere else, so other projects can later build for other platforms, and any of them could become a NuGet package.

Both libraries are AOT- and trim-checked (`IsAotCompatible`), build with warnings as errors, and publish under Native AOT with no warnings.

### WinBackground

Copied from the `dotnet` branch of [ai-keep-awake](https://github.com/gareth-jones-zen/ai-keep-awake), where it was first written as the port of the Python `win-background` package. Same namespace, same behaviour.

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

Native AOT publishing (for the tools, not these libraries) needs the Visual Studio C++ build tools; from Git Bash, put `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on the `PATH` so the SDK can find them.

## Licence

None yet. Until there is one, all rights are reserved.
