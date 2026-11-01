// Decision 0011 (run W5, fix 1 arena): the one blind predicate every page outside the battle's own uses (brief rule 3). A battle that is held
// or ready hides its keys: a request battle's run group and each column's run id (runs.db rows under source arena, ledger rows whose run is
// the group and whose op is run-<id>), and an agent battle's agent compare run id and its model keys "<run>-<slot>" (runs.db's parent row,
// its model rows and their turns, whose parent is a model key; the ledger rows of each model's run key; the workspaces). Slot a is column A
// (ArenaService hands the models to the runner in column order), so anything that names a slot's model names a column's contestant. Until
// the vote (or once void) History, the inspector, /api/runs, Settings › Data, History › Spend, the ledger API and the Playground show such a
// row without its model, provider, account, usage, timings or cost, and leave it out of a filter that could single one model out.
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Arena;

/// <summary>What is blind now: every key of each held or ready battle, with the battle it belongs to. A snapshot; take one per request.</summary>
public sealed class ArenaBlindSet
{
    private readonly IReadOnlyDictionary<string, string> keys;

    /// <summary>A set over <paramref name="keys"/> (key → battle id).</summary>
    public ArenaBlindSet(IReadOnlyDictionary<string, string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        this.keys = keys;
    }

    /// <summary>Nothing is blind.</summary>
    public static ArenaBlindSet Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>The blind set of the app's Arena now, or <see cref="Empty"/> when the Arena is not registered.</summary>
    public static ArenaBlindSet Of(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService<ArenaService>()?.Blind() ?? Empty;
    }

    /// <summary>Whether no battle is blind.</summary>
    public bool IsEmpty => keys.Count == 0;

    /// <summary>Every hidden key: run groups, run ids, agent compare run ids and model keys.</summary>
    public IReadOnlyCollection<string> Keys => [.. keys.Keys];

    /// <summary>The battle that hides <paramref name="key"/> (a run id, a group id, an agent compare run id or a model key), or null.</summary>
    public string? BattleOf(string? key) => key is { Length: > 0 } && keys.TryGetValue(key.Trim(), out var battle) ? battle : null;

    /// <summary>Whether <paramref name="key"/> is hidden.</summary>
    public bool Hides(string? key) => BattleOf(key) is not null;

    /// <summary>The battle that hides <paramref name="run"/> (its id, its group, or its parent: an agent model's turn), or null.</summary>
    public string? BattleOf(RunRecord? run) => run is null ? null : BattleOf(run.Id) ?? BattleOf(run.GroupId) ?? BattleOf(run.ParentId);

    /// <summary>Whether <paramref name="run"/> is hidden.</summary>
    public bool Hides(RunRecord? run) => BattleOf(run) is not null;

    /// <summary>The battle that hides ledger row <paramref name="row"/> (its run key, or the run of its op <c>run-&lt;id&gt;</c>), or null.</summary>
    public string? BattleOf(Reservation? row)
    {
        if (row is null || IsEmpty) return null;
        if (BattleOf(row.Run) is { } battle) return battle;
        var op = RunHistoryText.SendOf(row.Op);
        return op.StartsWith("run-", StringComparison.Ordinal) ? BattleOf(op[4..]) : BattleOf(op);
    }

    /// <summary>Whether ledger row <paramref name="row"/> is hidden.</summary>
    public bool Hides(Reservation? row) => BattleOf(row) is not null;

    /// <summary>A SQL condition that is true for a runs.db row of table alias <paramref name="alias"/> this set hides (its id, group or
    /// parent is a hidden key), with its parameters named <paramref name="prefix"/>0, 1, ...; <c>0</c> when nothing is hidden.</summary>
    public (string Sql, (string Name, object? Value)[] Parameters) RunSql(string alias, string prefix = "$blind")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (IsEmpty) return ("0", []);
        var values = keys.Keys.ToList();
        var names = values.Select((_, index) => prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var set = string.Join(", ", names);
        return ($"({alias}.id IN ({set}) OR COALESCE({alias}.group_id, '') IN ({set}) OR COALESCE({alias}.parent_id, '') IN ({set}))",
            [.. names.Zip(values, (name, value) => (name, (object?)value))]);
    }
}

/// <summary>What a page outside the battle shows of a hidden run or ledger row.</summary>
public static class ArenaBlindText
{
    /// <summary>The title a hidden run or ledger row shows.</summary>
    public const string Title = "Arena battle · blind";

    /// <summary>What stands for a hidden model.</summary>
    public const string Model = "hidden until you vote";

    /// <summary>Why a hidden run or row cannot be opened or sent again yet.</summary>
    public const string Refusal = "This run is part of a blind Arena battle that hasn't been voted on, so its model stays hidden. Vote first, then open it here.";

    /// <summary>The battle's page.</summary>
    public static string Href(string battleId)
    {
        ArgumentNullException.ThrowIfNull(battleId);
        return "/compare/arena/" + Uri.EscapeDataString(battleId);
    }

    /// <summary><paramref name="run"/> as a page may show it before its battle's vote: its id, kind, source, status, group, parent, pin and
    /// times of creation only (no provider, model, account, op, request, answer, error, usage, cost or timings).</summary>
    public static RunRecord Mask(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RunRecord
        {
            Id = run.Id,
            Op = "",
            Kind = run.Kind,
            Source = run.Source,
            Status = run.Status,
            GroupId = run.GroupId,
            ParentId = run.ParentId,
            Title = Title,
            Pinned = run.Pinned,
            Created = run.Created,
            Updated = run.Created,
        };
    }
}
