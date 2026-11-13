namespace PeopleFirstClient;

/// <summary>Walking down a reporting line.</summary>
public static class ReportingLine
{
    /// <summary>The most people a walk returns, as Python's MAX_PEOPLE.</summary>
    public const int MaxPeople = 1000;

    /// <summary>Your person id and everyone's below you, depth levels down (0: all), breadth first. With no
    /// <paramref name="me"/>, starts from <see cref="Api.MeAsync"/>. As Python's <c>reporting_line</c>.</summary>
    public static async Task<IReadOnlyList<string>> GetAsync(Api api, string? me = null, int depth = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (string.IsNullOrEmpty(me))
            me = await api.MeAsync(cancellationToken).ConfigureAwait(false);
        var people = new List<string>();
        var queue = new Queue<(string Person, int Level)>();
        queue.Enqueue((me, 0));
        while (queue.Count > 0 && people.Count < MaxPeople)
        {
            var (person, level) = queue.Dequeue();
            if (people.Contains(person))
                continue;
            people.Add(person);
            if (depth == 0 || level < depth)
                foreach (var report in await api.TeamAsync(person, cancellationToken).ConfigureAwait(false))
                    queue.Enqueue((report, level + 1));
        }
        return people;
    }
}
