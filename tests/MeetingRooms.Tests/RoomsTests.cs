using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;

namespace MeetingRooms.Tests;

public sealed class RoomListTests
{
    private static IReadOnlyList<Room> Parse(string toml) => RoomList.Parse(PyToml.Read(toml, "t.toml").TryGetValue("rooms", out var v) ? v : null, "t.toml");

    [Fact]
    public void Reads_rooms_with_optional_floor_and_capacity()
    {
        var rooms = Parse("""
            [[rooms]]
            name = "Room 3.1"
            address = "room3.1@example.com"
            floor = "3"
            capacity = 6
            [[rooms]]
            name = "Booth"
            address = "booth@example.com"
            floor = 2
            """);
        Assert.Equal([new Room("Room 3.1", "room3.1@example.com", "3", 6), new Room("Booth", "booth@example.com", "2", null)], rooms);
    }

    [Theory]
    [InlineData("[[rooms]]\nname = \"A\"\naddress = \"a@example.com\"\ncolour = \"red\"", "unknown setting 'colour'")]
    [InlineData("[[rooms]]\nname = \"A\"", "address should be text")]
    [InlineData("[[rooms]]\nname = \"A\"\naddress = \"not-an-address\"", "email address")]
    [InlineData("[[rooms]]\nname = \"A\"\naddress = \"a@example.com\"\ncapacity = 0", "capacity")]
    [InlineData("[[rooms]]\nname = \"A\"\naddress = \"a@example.com\"\n[[rooms]]\nname = \"B\"\naddress = \"A@example.com\"", "listed twice")]
    [InlineData("rooms = \"A\"", "list of [[rooms]]")]
    public void Refuses_bad_rooms(string toml, string message)
    {
        var error = Assert.Throws<FormatException>(() => Parse(toml));
        Assert.Contains(message, error.Message);
        Assert.StartsWith("t.toml", error.Message);
    }

    [Fact]
    public void No_rooms_is_an_empty_list() => Assert.Empty(RoomList.Parse(null, "t.toml"));

    [Fact]
    public void Finds_a_room_by_name_or_address_in_any_case()
    {
        var rooms = new[] { new Room("Room 3.1", "Room3.1@example.com") };
        Assert.Same(rooms[0], RoomList.Find(rooms, " room 3.1 "));
        Assert.Same(rooms[0], RoomList.Find(rooms, "room3.1@EXAMPLE.com"));
        Assert.Null(RoomList.Find(rooms, "Room 9"));
    }
}

public sealed class RoomOrderTests
{
    private static readonly Room Big2 = new("Room 2.1", "r21@example.com", "2", 10);
    private static readonly Room Small3 = new("Room 3.2", "r32@example.com", "3", 4);
    private static readonly Room Unknown3 = new("Room 3.1", "r31@example.com", "3");
    private static readonly Room Booth = new("Booth 3.9", "b39@example.com", "3", 2);
    private static readonly Room[] All = [Big2, Small3, Unknown3, Booth];

    [Fact]
    public void Preferred_first_then_floors_then_smallest_known_then_unknown_then_name()
    {
        var order = RoomOrder.Rank(All, ["Room 2.1"], ["3", "2"], 2);
        Assert.Equal([Big2, Booth, Small3, Unknown3], order.Select(r => r.Room));
        Assert.Equal("preference 1", order[0].Why);
        Assert.Equal("floor 3, seats 2", order[1].Why);
        Assert.Equal("floor 3, size not known", order[3].Why);
    }

    [Fact]
    public void A_room_too_small_is_left_out_even_when_preferred()
    {
        var order = RoomOrder.Rank(All, ["b39@example.com", "nowhere"], ["3"], 3);
        Assert.Equal([Small3, Unknown3, Big2], order.Select(r => r.Room));
    }
}

public sealed class RoomScheduleTests
{
    private static readonly DateTime Ten = new(2026, 11, 26, 10, 0, 0);

    [Fact]
    public void Free_ignores_items_shown_as_free_and_ones_that_only_touch()
    {
        var bookings = new[]
        {
            new ScheduleItem(Ten.AddHours(-1), Ten, "busy", "Alex Example"),
            new ScheduleItem(Ten, Ten.AddHours(1), "free", ""),
            new ScheduleItem(Ten.AddMinutes(30), Ten.AddHours(1), "tentative", "Sam Example"),
        };
        Assert.True(RoomSchedule.Free(bookings, Ten, Ten.AddMinutes(30)));
        Assert.False(RoomSchedule.Free(bookings, Ten, Ten.AddMinutes(45)));
    }

    [Fact]
    public void Showing_matches_the_organisers_name_at_the_same_times()
    {
        var bookings = new[] { new ScheduleItem(Ten, Ten.AddMinutes(30), "busy", "Alex Example") };
        Assert.Equal(ShowingMatch.Organiser, RoomSchedule.Showing(bookings, Ten, Ten.AddMinutes(30), "Alex Example", "alex@example.com", "Weekly").Match);
        Assert.Equal(ShowingMatch.None, RoomSchedule.Showing(bookings, Ten, Ten.AddMinutes(30), "Sam Example", "sam@example.com", "Weekly").Match);
        Assert.Equal(ShowingMatch.None, RoomSchedule.Showing(bookings, Ten, Ten.AddMinutes(60), "Alex Example", "alex@example.com", "Weekly").Match);
    }

    [Fact]
    public void Showing_takes_the_meetings_own_subject_or_a_hidden_one()
    {
        var subject = new[] { new ScheduleItem(Ten, Ten.AddMinutes(30), "busy", "Weekly sync") };
        Assert.Equal(ShowingMatch.Subject, RoomSchedule.Showing(subject, Ten, Ten.AddMinutes(30), "Alex", "alex@example.com", "weekly sync").Match);
        var hidden = new[] { new ScheduleItem(Ten, Ten.AddMinutes(30), "busy", "") };
        Assert.Equal(ShowingMatch.Hidden, RoomSchedule.Showing(hidden, Ten, Ten.AddMinutes(30), "Alex", "alex@example.com", "Weekly").Match);
        var free = new[] { new ScheduleItem(Ten, Ten.AddMinutes(30), "free", "Alex") };
        Assert.Equal(ShowingMatch.None, RoomSchedule.Showing(free, Ten, Ten.AddMinutes(30), "Alex", "alex@example.com", "Weekly").Match);
    }
}

/// <summary>Graph stood in for: each request gets the next queued answer, and is recorded.</summary>
internal sealed class FakeGraph : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> answers = new();
    public List<string> Seen { get; } = [];
    public List<string> Bodies { get; } = [];

    public void Reply(HttpStatusCode status, string body = "{}") =>
        answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        Seen.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel));
        return answers.Dequeue()();
    }

    public GraphRoomSchedules Schedules() => new(new GraphClient(new GraphClientOptions { Log = _ => { } }, this,
        token: (_, _) => Task.FromResult("tok"), delay: (_, _) => Task.CompletedTask), ["Calendars.Read"], TimeZoneInfo.Utc);
}

public sealed class GraphRoomSchedulesTests
{
    [Fact]
    public void Reads_each_rooms_items_and_marks_an_unreadable_one_null()
    {
        var graph = new FakeGraph();
        graph.Reply(HttpStatusCode.ServiceUnavailable);   // a read: retried
        graph.Reply(HttpStatusCode.OK, """
            {"value": [
              {"scheduleId": "a@example.com", "scheduleItems": [
                {"status": "busy", "subject": "Alex Example", "start": {"dateTime": "2026-11-26T10:00:00.0000000", "timeZone": "UTC"},
                 "end": {"dateTime": "2026-11-26T10:30:00.0000000", "timeZone": "UTC"}}]},
              {"scheduleId": "b@example.com", "error": {"message": "not found"}}
            ]}
            """);
        var found = graph.Schedules().Read(["a@example.com", "B@example.com"], new DateTime(2026, 11, 26), new DateTime(2026, 11, 27));
        Assert.Equal([new ScheduleItem(new DateTime(2026, 11, 26, 10, 0, 0), new DateTime(2026, 11, 26, 10, 30, 0), "busy", "Alex Example")], found["a@example.com"]!);
        Assert.Null(found["B@example.com"]);
        Assert.Equal(["POST /v1.0/me/calendar/getSchedule", "POST /v1.0/me/calendar/getSchedule"], graph.Seen);
        var body = JsonNode.Parse(graph.Bodies[1])!;
        Assert.Equal("2026-11-26T00:00:00", (string)body["startTime"]!["dateTime"]!);
    }
}
