using WinBackground;

namespace PeopleFirstClient.Tests;

/// <summary>The nine tests of Python's people-first-client (tests/test_client.py), ported. Where .NET behaves
/// differently on purpose, the test says so.</summary>
public class PortedTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    internal static readonly Settings TestSettings = new(url: "https://example.people-first.com/", account: "you@example.com",
        edgeProfile: Path.Combine(Path.GetTempPath(), "pf-test-profile"));

    private static Api ApiOver(FakeHttp http) => new(TestSettings, "Bearer x", new HttpClient(http));

    // ---------------------------------------------------------------- SettingsTest

    [Fact]
    public void Tenant_code_from_url()
    {
        Assert.Equal("https://example.people-first.com", TestSettings.Url);
        Assert.Equal("example", TestSettings.TenantCode);
        Assert.Equal("acme", new Settings(url: "https://acme-live.people-first.com").TenantCode);
        Assert.Equal("other", new Settings(url: "https://acme.people-first.com", tenantCode: "other").TenantCode);
        Assert.Equal("", new Settings(url: "https://hr.example.com").TenantCode);
    }

    // ---------------------------------------------------------------- ApiTest

    [Fact]
    public async Task Headers_and_me()
    {
        var http = new FakeHttp(new() { ["/profile"] = FakeHttp.Body("{\"data\":{\"profile\":{\"personId\":\"P1\"}}}") });
        using var api = ApiOver(http);
        Assert.Equal("P1", await api.MeAsync(Cancel));
        var (_, method, headers) = http.Calls[0];
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("example", headers["TenantCode"]);
        Assert.Equal("Bearer x", headers["Authorization"]);
        Assert.Equal("application/json", headers["Accept"]);
        Assert.True(headers.ContainsKey("EnvironmentCode"));
    }

    [Fact]
    public async Task Error_names_the_path_not_the_query()
    {
        using var api = ApiOver(new FakeHttp([]));
        var error = await Assert.ThrowsAsync<PeopleFirstException>(() => api.GetAsync("/hrm/people/P1/events?viewStartDate=2026-01-01", Cancel));
        Assert.Contains("/hrm/people/P1/events", error.Message);
        Assert.DoesNotContain("2026-01-01", error.Message);
        Assert.Equal("People First answered 404 to /hrm/people/P1/events", error.Message);
    }

    [Fact]
    public async Task Team_members_pages()
    {
        var pages = new Dictionary<int, string>
        {
            [0] = "[{\"personId\":\"A\",\"firstName\":\"Ann\"},{\"personId\":\"B\",\"firstName\":\"Bo\"}]",
            [2] = "[{\"personId\":\"C\",\"firstName\":\"Cy\"}]",
        };
        string Team(string path)
        {
            var offset = int.Parse(path.Split("page%5Boffset%5D=")[1].Split('&')[0]);
            return $"{{\"data\":{{\"team\":{pages.GetValueOrDefault(offset, "[]")}}},\"meta\":{{\"count\":3}}}}";
        }
        using var api = ApiOver(new FakeHttp(new() { ["/hrm/people/ME/team"] = Team }));
        var members = await api.TeamMembersAsync("ME", Cancel);
        Assert.Equal(["A", "B", "C"], members.Select(m => m.GetProperty("personId").GetString()));
    }

    [Fact]
    public async Task Events_query()
    {
        var http = new FakeHttp(new() { ["/hrm/people/A/events"] = FakeHttp.Body("{\"data\":{\"events\":[]}}") });
        using var api = ApiOver(http);
        await api.EventsAsync("A", "2026-11-12", "2027-11-12", cancellationToken: Cancel);
        Assert.Contains("viewStartDate=2026-11-12", http.Calls[0].Path);
        Assert.Contains("viewEndDate=2027-11-12", http.Calls[0].Path);
        Assert.Contains("annotate=t", http.Calls[0].Path);
        await api.EventsAsync("A", new DateOnly(2026, 11, 12), new DateOnly(2027, 11, 12), annotate: false, Cancel);
        Assert.Equal("/hrm/people/A/events?viewStartDate=2026-11-12&viewEndDate=2027-11-12", http.Calls[1].Path);
    }

    [Fact]
    public async Task Reporting_line_depth()
    {
        var tree = new Dictionary<string, string[]> { ["ME"] = ["A", "B"], ["A"] = ["A1"], ["B"] = [], ["A1"] = [] };
        var answers = tree.ToDictionary(
            pair => $"/hrm/people/{pair.Key}/team/people",
            pair => FakeHttp.Body($"{{\"data\":{{\"team\":[{string.Join(",", pair.Value.Select(c => $"{{\"personId\":\"{c}\"}}"))}]}},\"meta\":{{\"count\":{pair.Value.Length}}}}}"));
        using var api = ApiOver(new FakeHttp(answers));
        Assert.Equal(["ME", "A", "B"], await ReportingLine.GetAsync(api, "ME", depth: 1, Cancel));
        Assert.Equal(["ME", "A", "B", "A1"], await ReportingLine.GetAsync(api, "ME", cancellationToken: Cancel));
    }

    // ---------------------------------------------------------------- LockTest

    [Fact]
    public void Name_is_stable_across_spellings()
    {
        var path = Path.Combine(Path.GetTempPath(), "Profile");
        Assert.Equal(ProfileLock.NameFor(path), ProfileLock.NameFor(path.ToUpperInvariant()));
        Assert.StartsWith("people-first-edge-", ProfileLock.NameFor(path));
    }

    /// <summary>Python: "reentrant in process". Deliberately different here: the NamedLock under it isn't
    /// re-entrant, so the caller holds one ProfileLock for a run and passes it in. Taking it a second time fails
    /// at once (where waiting would have meant waiting on itself), and a held lock passed in is used as it is.</summary>
    [Fact]
    public async Task A_held_lock_is_passed_in_not_taken_again()
    {
        var profile = TestSettings.EdgeProfile!;
        await using (var held = await ProfileLock.AcquireAsync(profile, 5, Cancel))
        {
            var again = await Assert.ThrowsAsync<InvalidOperationException>(() => ProfileLock.AcquireAsync(profile, 0, Cancel));
            Assert.Contains("HeldLock", again.Message);
            var browser = new FakeBrowser
            {
                OnNavigate = (b, session, _) => b.SendToken(session),
            };
            var options = new SessionOptions { HeldLock = held, Tick = TimeSpan.FromMilliseconds(20), TransportFactory = (_, _) => browser };
            using var api = await Session.OpenAsync(TestSettings, options, Cancel);   // would time out if it took the lock
            Assert.True(held.IsHeld);
        }
        await using var afterwards = await ProfileLock.AcquireAsync(profile, 0, Cancel);   // released and unregistered
    }

    [Fact]
    public async Task Another_holder_is_waited_for()
    {
        // Another tool holding the same named lock: a NamedLock of its own, as a separate process would be.
        using var other = new NamedLock(ProfileLock.NameFor(TestSettings.EdgeProfile!));
        Assert.True(other.Acquire(5));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ProfileLock.AcquireAsync(TestSettings.EdgeProfile!, 0.2, Cancel));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(150));
        other.Release();
        await using var mine = await ProfileLock.AcquireAsync(TestSettings.EdgeProfile!, 5, Cancel);
        Assert.True(mine.IsHeld);
    }
}
