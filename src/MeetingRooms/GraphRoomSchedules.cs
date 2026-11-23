using System.Globalization;
using System.Text.Json.Nodes;
using M365Graph;

namespace MeetingRooms;

/// <summary>Rooms' schedules through free/busy (Graph's <c>POST /me/calendar/getSchedule</c>), which any signed-in
/// user can read for a room (Calendars.Read or Calendars.ReadWrite). A read: sent with
/// <see cref="GraphRetryPolicy.Read"/>, so a 5xx is retried like any other read.</summary>
public sealed class GraphRoomSchedules(GraphClient graph, IReadOnlyList<string> scopes, TimeZoneInfo zone)
{
    /// <summary>getSchedule takes at most 62 days at a time.</summary>
    public const int ChunkDays = 60;

    /// <summary>Schedules asked for in one call (Graph's limit is higher; 20 keeps the replies small).</summary>
    public const int SchedulesPerCall = 20;

    private Dictionary<string, string> Headers => new() { ["Prefer"] = $"outlook.timezone=\"{zone.Id}\"" };

    /// <summary>Each room's schedule from <paramref name="from"/> to <paramref name="to"/> (local time), by address (any
    /// case); null for a room whose schedule couldn't be read (an unknown address, or one refused), so it's never
    /// taken to be free.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ScheduleItem>?> Read(IReadOnlyList<string> addresses, DateTime from, DateTime to)
    {
        var wanted = addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new Dictionary<string, List<ScheduleItem>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in wanted.Chunk(SchedulesPerCall))
        {
            for (var start = from; start < to; start = start.AddDays(ChunkDays))
            {
                var end = start.AddDays(ChunkDays) < to ? start.AddDays(ChunkDays) : to;
                var body = new JsonObject
                {
                    ["schedules"] = new JsonArray([.. batch.Select(a => (JsonNode)JsonValue.Create(a))]),
                    ["startTime"] = When(start),
                    ["endTime"] = When(end),
                    ["availabilityViewInterval"] = 15,
                };
                var reply = graph.RequestAsync("/me/calendar/getSchedule", HttpMethod.Post, body, scopes, Headers, policy: GraphRetryPolicy.Read)
                    .GetAwaiter().GetResult();
                foreach (var (id, items) in Parse(reply))
                {
                    if (found.TryGetValue(id, out var known) && known is null)
                        continue;
                    if (items is null)
                    {
                        found[id] = null;
                        continue;
                    }
                    if (known is null)
                        found[id] = known = [];
                    known.AddRange(items);
                }
            }
        }
        return wanted.ToDictionary(a => a,
            a => found.TryGetValue(a, out var b) && b is not null ? (IReadOnlyList<ScheduleItem>?)b.Distinct().OrderBy(x => x.Start).ToList() : null,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A getSchedule reply: each schedule's id and its items (null when it came back with an error).</summary>
    internal static IEnumerable<(string Id, List<ScheduleItem>? Items)> Parse(JsonObject reply)
    {
        foreach (var schedule in (reply["value"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (Text(schedule, "scheduleId") is not { } id)
                continue;
            if (schedule["error"] is JsonObject || schedule["scheduleItems"] is not JsonArray items)
            {
                yield return (id, null);
                continue;
            }
            yield return (id, items.OfType<JsonObject>()
                .Select(i => new ScheduleItem(Time(i["start"]), Time(i["end"]), Text(i, "status") ?? "busy", Text(i, "subject") ?? ""))
                .ToList());
        }
    }

    private JsonObject When(DateTime t) => new()
    {
        ["dateTime"] = t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        ["timeZone"] = zone.Id,
    };

    internal static DateTime Time(JsonNode? node)
    {
        var text = (node as JsonObject) is { } o ? Text(o, "dateTime") : null;
        return text is null ? default : DateTime.Parse(text[..Math.Min(19, text.Length)], CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
