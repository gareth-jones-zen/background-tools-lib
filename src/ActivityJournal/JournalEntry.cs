using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ActivityJournal;

/// <summary>How much of a line a screen shown to others (an assistant UI's presenting mode) may show.</summary>
public static class Sensitivity
{
    /// <summary>Fine to show whole: a bank holiday added, a room booked.</summary>
    public const string Normal = "normal";

    /// <summary>Show what was done and by which rule, but hide the subject when others can see the screen: an email's or a
    /// meeting's subject, a person's name.</summary>
    public const string Private = "private";

    /// <summary>Hide the whole line when others can see the screen: anything about a person's performance, health or pay,
    /// or need-to-know work.</summary>
    public const string Restricted = "restricted";

    public static readonly IReadOnlyList<string> All = [Normal, Private, Restricted];
}

/// <summary>Who started what a line records.</summary>
public static class By
{
    /// <summary>The tool, on its own (a rule, a watcher's pass, a scheduled run).</summary>
    public const string Auto = "auto";

    /// <summary>A person, through one of the tool's commands (by hand in a session, or a button in a UI).</summary>
    public const string Hand = "hand";

    public static readonly IReadOnlyList<string> All = [Auto, Hand];
}

/// <summary>
/// One thing a tool did, as one line of the journal:
/// <code>
/// {"id":"3f9c0a1b2c4d","when":"2026-11-25T07:42:10.512+00:00","tool":"mail-triage","what":"delete","by":"auto","subject":"Your weekly digest","rule":"Newsletters","why":"Read on the web if at all","item":"&lt;abc@mail.example.com&gt;","undo":"mail-triage restore 0d1e2f3a4b5c","sensitivity":"private"}
/// </code>
/// <para><b>Never a message body</b>: a subject only where the tool already shows one (its own list or record), and
/// <see cref="Sensitivity"/> saying how much of the line may be shown to others.</para>
/// </summary>
public sealed record JournalEntry
{
    /// <summary>The longest any text field is kept, in UTF-16 units; longer text is cut and ends in "…".</summary>
    public const int MaxText = 1000;

    /// <summary>The line's own id (12 hex characters), set when it's written: for a UI to remember what's been seen.</summary>
    public string Id { get; init; } = "";

    /// <summary>When it was done, with the offset; set to now when written if left at the default.</summary>
    public DateTimeOffset When { get; init; }

    /// <summary>The tool's name, as its exe is called: "mail-triage".</summary>
    public required string Tool { get; init; }

    /// <summary>What was done: a short verb of the tool's own ("delete", "file", "accept", "move", "keep"...).</summary>
    public required string What { get; init; }

    /// <summary><see cref="ActivityJournal.By.Auto"/> (the default) or <see cref="ActivityJournal.By.Hand"/>.</summary>
    public string By { get; init; } = ActivityJournal.By.Auto;

    /// <summary>What it was done to, as the tool already shows it (an email's subject, a meeting's title and time). Optional.</summary>
    public string? Subject { get; init; }

    /// <summary>The rule that did it (its name), or for something done by hand, what it was ("by hand"). Optional.</summary>
    public string? Rule { get; init; }

    /// <summary>Why: the rule's reason as the person wrote it, or the tool's own (a check's result). Optional.</summary>
    public string? Why { get; init; }

    /// <summary>The item or message id it was done to, for a UI to find it again (an internet message ID, an event id). Optional.</summary>
    public string? Item { get; init; }

    /// <summary>A command that undoes it, where the tool has one ("mail-triage restore 0d1e2f3a4b5c"); null when it can't be
    /// undone by a command (it notified someone, or it's undone by hand).</summary>
    public string? Undo { get; init; }

    /// <summary><see cref="ActivityJournal.Sensitivity.Normal"/> (the default), <see cref="ActivityJournal.Sensitivity.Private"/>
    /// or <see cref="ActivityJournal.Sensitivity.Restricted"/>.</summary>
    public string Sensitivity { get; init; } = ActivityJournal.Sensitivity.Normal;

    /// <summary>Null when it was done; "unconfirmed" when the tool sent it but Microsoft (or whoever) didn't confirm it, so it
    /// may or may not have happened.</summary>
    public string? Outcome { get; init; }

    public const string Unconfirmed = "unconfirmed";

    /// <summary>The time as written: ISO 8601 with milliseconds and the offset, so lines sort and compare as text within
    /// one offset, and as times across them.</summary>
    public static string FormatWhen(DateTimeOffset when) => when.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    /// <summary>The line's JSON object, keys in a fixed order; optional fields that are null are left out.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = Id,
            ["when"] = FormatWhen(When),
            ["tool"] = Tool,
            ["what"] = What,
            ["by"] = By,
        };
        void Opt(string key, string? value)
        {
            if (value is not null)
                o[key] = Cut(value);
        }
        Opt("subject", Subject);
        Opt("rule", Rule);
        Opt("why", Why);
        Opt("item", Item);
        Opt("undo", Undo);
        o["sensitivity"] = Sensitivity;
        Opt("outcome", Outcome);
        return o;
    }

    /// <summary>A line read back; null when it isn't a journal line (no tool, what or time, or one of the wrong type).
    /// Fields it doesn't know are ignored, so a newer writer's lines still read.</summary>
    public static JournalEntry? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o)
            return null;
        string? Text(string key) => o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        if (Text("tool") is not { Length: > 0 } tool || Text("what") is not { Length: > 0 } what || Text("when") is not { } when
            || !DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            return null;
        return new JournalEntry
        {
            Id = Text("id") ?? "",
            When = at,
            Tool = tool,
            What = what,
            By = Text("by") ?? ActivityJournal.By.Auto,
            Subject = Text("subject"),
            Rule = Text("rule"),
            Why = Text("why"),
            Item = Text("item"),
            Undo = Text("undo"),
            Sensitivity = Text("sensitivity") is { } s && ActivityJournal.Sensitivity.All.Contains(s) ? s : ActivityJournal.Sensitivity.Restricted,
            Outcome = Text("outcome"),
        };
    }

    /// <summary>Checks what a writer gave: a tool and what (short, one line), a known sensitivity and by.</summary>
    internal void Check()
    {
        if (string.IsNullOrWhiteSpace(Tool) || Tool.Length > 100 || Tool.Any(char.IsControl))
            throw new ArgumentException("A journal line needs the tool's name: some text, one line, up to 100 characters.", nameof(Tool));
        if (string.IsNullOrWhiteSpace(What) || What.Length > 100 || What.Any(char.IsControl))
            throw new ArgumentException("A journal line needs what was done: a short verb, one line, up to 100 characters.", nameof(What));
        if (!ActivityJournal.Sensitivity.All.Contains(Sensitivity))
            throw new ArgumentException($"A journal line's sensitivity is one of {string.Join(", ", ActivityJournal.Sensitivity.All)}, not {Sensitivity}.", nameof(Sensitivity));
        if (!ActivityJournal.By.All.Contains(By))
            throw new ArgumentException($"A journal line's by is one of {string.Join(", ", ActivityJournal.By.All)}, not {By}.", nameof(By));
    }

    private static string Cut(string text) => text.Length <= MaxText ? text : text[..(char.IsHighSurrogate(text[MaxText - 2]) ? MaxText - 2 : MaxText - 1)] + "…";
}
