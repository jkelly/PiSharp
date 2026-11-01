// Decision 0011 (run W5, integration fix 1): what a blind round hides from the pages that list or show runs (brief rule 3, "blind means
// blind"). A request battle's answers are runs.db rows of one run group, and an agent battle's are the rows of one agent compare run (its
// top row, its models' rows and their calls); until the battle is voted on or void, those rows name the contestants in column order. The
// Runs area knows nothing of battles: the Arena registers an IRunBlindSource that names the hidden groups (each with its battle's page), and
// History › Runs, /api/runs, the Run inspector, History's exports, HAR and JSON downloads, the Playground's ?run= and agent compare's
// endpoints ask RunBlinds. A list leaves the rows out in SQL (Visible), so its totals, counts, filter options and pages agree with the rows
// it shows; a single run answers 409 {error: {code: "blind", message, battle}} (JSON) or a notice that links to the battle (pages). Once the
// battle is voted on or void its rows show again, as they were.
using System.Collections.Frozen;
using System.Globalization;

namespace PiSharp.WebAgent.Runs;

/// <summary>Names the run groups a blind round hides (the Arena's battles that are not voted on or void yet).</summary>
public interface IRunBlindSource
{
    /// <summary>The hidden groups: each unvoted battle's run group (a request battle's group id, an agent battle's agent compare run id)
    /// mapped to the address of its battle page.</summary>
    IReadOnlyDictionary<string, string> HiddenGroups();
}

/// <summary>Which runs a blind round hides (see the file comment). A singleton; without an <see cref="IRunBlindSource"/> it hides nothing.</summary>
public sealed class RunBlinds(IServiceProvider services)
{
    /// <summary>The error code of a hidden run's JSON answer.</summary>
    public const string Code = "blind";

    /// <summary>What a hidden run's JSON answer and notice say.</summary>
    public const string Message = "This run is part of a blind round that has no vote yet, so its models stay hidden. Vote on the round (or stop it) to see it here.";

    private static readonly IReadOnlyDictionary<string, string> None = FrozenDictionary<string, string>.Empty;

    /// <summary>The registered instance, or one over <paramref name="services"/>.</summary>
    public static RunBlinds Of(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService<RunBlinds>() ?? new RunBlinds(services);
    }

    /// <summary>The hidden groups, each with its battle page (empty when nothing is blind).</summary>
    public IReadOnlyDictionary<string, string> Groups() => services.GetService<IRunBlindSource>()?.HiddenGroups() ?? None;

    /// <summary>The battle page of the round that hides group (or agent compare run) <paramref name="group"/>, or null.</summary>
    public string? Group(string? group) => string.IsNullOrWhiteSpace(group) ? null : Groups().GetValueOrDefault(group.Trim());

    /// <summary>The battle page of the round that hides <paramref name="run"/>, or null when no round hides it.</summary>
    public string? Hidden(RunRecord run) => Hidden(run, Groups());

    /// <summary>The battle page of the round in <paramref name="groups"/> that hides <paramref name="run"/>: the run is the group's top row,
    /// one of its calls, or a row under it (an agent model's row, or one of that model's calls); null when none hides it.</summary>
    public string? Hidden(RunRecord run, IReadOnlyDictionary<string, string> groups)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0) return null;
        if (Find(groups, run.Id, run.GroupId, run.ParentId) is { } page) return page;
        if (run.ParentId is not { } parent || services.GetService<RunStore>()?.Get(parent) is not { } above) return null;
        if (Find(groups, above.GroupId, above.ParentId) is { } found) return found;
        return above.ParentId is { } top && services.GetService<RunStore>()?.Get(top) is { } root ? Find(groups, root.GroupId) : null;
    }

    /// <summary>A SQL condition that is true for a row of <c>runs</c> (aliased <paramref name="alias"/>, or unaliased when empty) that none of
    /// <paramref name="groups"/> hides, with its parameters (named <paramref name="prefix"/>0, 1, …). It never yields NULL, so it can be
    /// joined to any WHERE clause with AND.</summary>
    public static (string Sql, List<(string Name, object? Value)> Parameters) Visible(string alias, IReadOnlyCollection<string> groups, string prefix = "$blind")
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0) return ("1 = 1", []);
        var names = groups.Select((_, index) => prefix + index.ToString(CultureInfo.InvariantCulture)).ToList();
        var set = string.Join(", ", names);
        var a = alias.Length == 0 ? "" : alias + ".";
        // Its own id or group; its parent (a model's row, an agent run's call of its own); its parent's parent or group (a model's calls).
        var sql = $"NOT ({a}id IN ({set}) OR COALESCE({a}group_id, '') IN ({set}) OR COALESCE({a}parent_id, '') IN ({set}) " +
            $"OR COALESCE({a}parent_id, '') IN (SELECT bp.id FROM runs bp WHERE COALESCE(bp.parent_id, '') IN ({set}) OR COALESCE(bp.group_id, '') IN ({set})))";
        return (sql, [.. names.Zip(groups, (name, group) => (name, (object?)group))]);
    }

    private static string? Find(IReadOnlyDictionary<string, string> groups, params string?[] keys)
    {
        foreach (var key in keys)
            if (key is not null && groups.TryGetValue(key, out var page)) return page;
        return null;
    }
}

/// <summary>The inspector's notice for a run a blind round hides (<c>Views/Runs/Blind.cshtml</c>).</summary>
/// <param name="Id">The run asked for.</param>
/// <param name="Battle">The battle page that reveals it after the vote.</param>
public sealed record RunBlindView(string Id, string Battle);
