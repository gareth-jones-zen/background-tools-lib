using System.Collections;
using System.Globalization;

namespace MeetingRooms;

/// <summary>A meeting room: the name Outlook shows (and a booking's location), its mailbox address, its floor (text, such
/// as "3"), and how many it seats (null: not known).</summary>
public sealed record Room(string Name, string Address, string? Floor = null, int? Capacity = null);

/// <summary>One item on a room's schedule, as free/busy shows it, in local time: its status (busy, tentative, oof,
/// workingElsewhere, free or unknown) and its subject (often the organiser's name; blank when the room hides it).</summary>
public sealed record ScheduleItem(DateTime Start, DateTime End, string Status, string Subject);

/// <summary>A room in the order it's tried, with why it's there.</summary>
public sealed record RankedRoom(Room Room, string Why);

/// <summary>How a room's schedule was matched to a meeting.</summary>
public enum ShowingMatch { None, Organiser, Subject, Hidden }

/// <summary>The settings' list of rooms: a <c>[[rooms]]</c> array of tables.
/// <code>
/// [[rooms]]
/// name = "Room 3.1"                  # as Outlook shows it
/// address = "room3.1@example.com"    # its mailbox
/// floor = "3"                        # optional, text
/// capacity = 6                       # optional: how many it seats; a room with none counts as fitting
/// </code></summary>
public static class RoomList
{
    public static readonly IReadOnlyList<string> Keys = ["name", "address", "floor", "capacity"];

    /// <summary>Reads the array (a Tomlyn TomlTableArray, or any list of string-keyed dictionaries; null: no rooms),
    /// strictly: an unknown key, a missing name or address, an address without an @, a bad capacity, or the same room
    /// twice is a FormatException naming <paramref name="where"/> (the settings file).</summary>
    public static IReadOnlyList<Room> Parse(object? value, string where, string table = "rooms")
    {
        if (value is null)
            return [];
        if (value is not IEnumerable list || value is string || value is IDictionary)
            throw new FormatException($"{where}: {table} should be a list of [[{table}]] tables.");
        var rooms = new List<Room>();
        var n = 0;
        foreach (var item in list)
        {
            n++;
            if (item is not IDictionary<string, object> room)
                throw new FormatException($"{where}: {table} should be a list of [[{table}]] tables.");
            foreach (var key in room.Keys)
                if (!Keys.Contains(key))
                    throw new FormatException($"{where}: unknown setting '{key}' in [[{table}]] {n} (known: {string.Join(", ", Keys)}).");
            string Required(string key) => room.TryGetValue(key, out var v) && v is string { Length: > 0 } s && s.Trim().Length > 0
                ? s.Trim() : throw new FormatException($"{where}: [[{table}]] {n}: {key} should be text, and not empty.");
            var name = Required("name");
            var address = Required("address");
            if (!address.Contains('@'))
                throw new FormatException($"{where}: [[{table}]] {n}: address should be the room's email address.");
            int? capacity = !room.TryGetValue("capacity", out var c) ? null
                : c is long l && l is >= 1 and <= 1000 ? (int)l
                : throw new FormatException($"{where}: [[{table}]] {n}: capacity should be a whole number from 1 to 1000.");
            string? floor = !room.TryGetValue("floor", out var f) ? null
                : f is string { Length: > 0 } fs ? fs.Trim()
                : f is long fl ? fl.ToString(CultureInfo.InvariantCulture)
                : throw new FormatException($"{where}: [[{table}]] {n}: floor should be text, such as \"3\".");
            if (rooms.Any(r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException($"{where}: [[{table}]] {n}: {name} ({address}) is listed twice.");
            rooms.Add(new Room(name, address, floor, capacity));
        }
        return rooms;
    }

    /// <summary>The room that <paramref name="wanted"/> names, by address or name (any case, spaces trimmed); null if
    /// none does.</summary>
    public static Room? Find(IEnumerable<Room> rooms, string wanted) =>
        rooms.FirstOrDefault(r => string.Equals(r.Address, wanted.Trim(), StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(r.Name, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The order rooms are tried in.</summary>
public static class RoomOrder
{
    /// <summary>The rooms that seat <paramref name="seats"/> (a room of unknown capacity counts as fitting), in the
    /// order they're tried: <paramref name="preferred"/> first, in its order (names or addresses); then rooms on
    /// <paramref name="floors"/>, in that order; then the smallest with a known capacity; then any of unknown capacity;
    /// then by name. A preferred name that isn't in the list is skipped.</summary>
    public static IReadOnlyList<RankedRoom> Rank(IEnumerable<Room> rooms, IReadOnlyList<string> preferred,
        IReadOnlyList<string> floors, int seats)
    {
        var all = rooms.ToList();
        var result = new List<RankedRoom>();
        bool Fits(Room r) => r.Capacity is null || r.Capacity >= seats;
        var n = 0;
        foreach (var wanted in preferred)
        {
            n++;
            var room = RoomList.Find(all, wanted);
            if (room is null || !Fits(room) || result.Any(x => x.Room == room))
                continue;
            result.Add(new RankedRoom(room, $"preference {n}"));
        }
        int FloorRank(Room r) => r.Floor is { } f && floors.Contains(f, StringComparer.OrdinalIgnoreCase)
            ? floors.ToList().FindIndex(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))
            : floors.Count;
        foreach (var room in all.Where(r => Fits(r) && result.All(x => x.Room != r))
                     .OrderBy(FloorRank)
                     .ThenBy(r => r.Capacity is null ? 1 : 0)
                     .ThenBy(r => r.Capacity ?? 0)
                     .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var why = new List<string>();
            if (FloorRank(room) < floors.Count)
                why.Add($"floor {room.Floor}");
            why.Add(room.Capacity is { } c ? $"seats {c}" : "size not known");
            result.Add(new RankedRoom(room, string.Join(", ", why)));
        }
        return result;
    }
}

/// <summary>What a room's schedule says.</summary>
public static class RoomSchedule
{
    /// <summary>Whether nothing on the schedule (other than an item shown as free) overlaps start to end.</summary>
    public static bool Free(IEnumerable<ScheduleItem> bookings, DateTime start, DateTime end) =>
        !bookings.Any(b => !IsFreeStatus(b.Status) && b.Start < end && b.End > start);

    private static bool IsFreeStatus(string status) => string.Equals(status, "free", StringComparison.OrdinalIgnoreCase);

    /// <summary>The item on a room's schedule that is this meeting, if there is one: at the meeting's times (to within
    /// <paramref name="slack"/>, a minute by default), not shown as free, and whose subject names the organiser (many
    /// rooms show the organiser's name as the subject), or is the meeting's own subject, or is blank (a room that hides
    /// subjects: then the times alone decide, <see cref="ShowingMatch.Hidden"/>). A room that only lists the meeting
    /// somewhere else doesn't count: only its own schedule does.</summary>
    public static (ScheduleItem? Booking, ShowingMatch Match) Showing(IEnumerable<ScheduleItem> bookings, DateTime start, DateTime end,
        string organiserName, string organiserAddress, string subject, TimeSpan? slack = null)
    {
        var within = slack ?? TimeSpan.FromMinutes(1);
        foreach (var b in bookings)
        {
            if (IsFreeStatus(b.Status) || (b.Start - start).Duration() > within || (b.End - end).Duration() > within)
                continue;
            var s = b.Subject.Trim();
            if (s.Length == 0)
                return (b, ShowingMatch.Hidden);
            if ((organiserName.Trim().Length > 0 && s.Contains(organiserName.Trim(), StringComparison.OrdinalIgnoreCase))
                || (organiserAddress.Trim().Length > 0 && s.Contains(organiserAddress.Trim(), StringComparison.OrdinalIgnoreCase)))
                return (b, ShowingMatch.Organiser);
            if (subject.Trim().Length > 0 && string.Equals(s, subject.Trim(), StringComparison.OrdinalIgnoreCase))
                return (b, ShowingMatch.Subject);
        }
        return (null, ShowingMatch.None);
    }
}
