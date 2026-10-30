// Decision 0011 (run W5, E5): what the Arena's pages show (compare.html's blind-held, blind, blind-voted, agent-blind, agent-blind-voted:
// _ArenaColumn, _AgentArenaColumn, _ArenaVoteBar and the revealed Metrics tab). Brief rule 3 is enforced here, once: a view built before
// the vote (status held or ready) never carries a contestant's identity, run, logo, timing, cost or error text. Reveal is null, Metrics is
// null, and an agent column's view of R6's AgentColumnView is used only for what the mockup shows blind (the check, the outcome, the answer,
// the files, the output tail); the Razor partials read Reveal to decide, so they cannot show a name that is not there. A void battle (a
// contestant failed, or the user stopped it) is revealed, with what went wrong, and never counted.
using System.Globalization;
using PiSharp.Web.Common;
using PiSharp.WebAgent.AgentCompare.Pages;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals.Arena;

/// <summary>Who a column was, shown after the vote.</summary>
/// <param name="Letter">The column.</param>
/// <param name="Identity">The model and settings.</param>
/// <param name="Name">The model's display name.</param>
/// <param name="ProviderName">The provider's display name.</param>
/// <param name="LogoUrl">The provider's logo, or null.</param>
public sealed record ArenaReveal(string Letter, ModelIdentity Identity, string Name, string ProviderName, string? LogoUrl)
{
    /// <summary><c>provider/model · thinking</c>, as the revealed head's second line says it.</summary>
    public string Detail => Identity.Name + (Identity.Thinking is { } thinking ? " · " + thinking : "")
        + (Identity.Temperature is { } temperature ? " · temperature " + temperature.ToString("0.##", CultureInfo.InvariantCulture) : "");
}

/// <summary>One request-mode column (<c>_ArenaColumn</c>).</summary>
public sealed record ArenaColumnView
{
    /// <summary>The column's letter.</summary>
    public required string Letter { get; init; }

    /// <summary>How many columns the battle has.</summary>
    public required int Count { get; init; }

    /// <summary>The battle's status.</summary>
    public required ArenaStatus Status { get; init; }

    /// <summary>Whether answers stream as they come.</summary>
    public bool StreamAnyway { get; init; }

    /// <summary>Its answer so far (text only when it may be shown).</summary>
    public required ArenaAnswer Answer { get; init; }

    /// <summary>Who it was, after the vote (or when the battle is void); null before.</summary>
    public ArenaReveal? Reveal { get; init; }

    /// <summary>Whether the vote picked it (or ranked it first).</summary>
    public bool Picked { get; init; }

    /// <summary>Its place in a ranking ("1st"), or null.</summary>
    public string? Rank { get; init; }

    /// <summary>The revealed footer (TTFT, total, tokens, cost), empty before.</summary>
    public IReadOnlyList<string> Foot { get; init; } = [];

    /// <summary>Why it failed, once revealed, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Whether its answer is held (the placeholder shows).</summary>
    public bool Held => Status == ArenaStatus.Held && (!StreamAnyway || Answer.Text is null);

    /// <summary>The head's label: "Answer B" before the vote.</summary>
    public string Label => Reveal is { } reveal ? $"{Letter}: {reveal.Name}" : "Answer " + Letter;
}

/// <summary>One agent-mode column (<c>_AgentArenaColumn</c>).</summary>
public sealed record AgentArenaColumnView
{
    /// <summary>The column's letter.</summary>
    public required string Letter { get; init; }

    /// <summary>The battle's status.</summary>
    public required ArenaStatus Status { get; init; }

    /// <summary>R6's view of the model's run (state, check, diff), or null when the run is gone.</summary>
    public AgentColumnView? Column { get; init; }

    /// <summary>Who it was, after the vote; null before.</summary>
    public ArenaReveal? Reveal { get; init; }

    /// <summary>Whether the vote picked it (or ranked it first).</summary>
    public bool Picked { get; init; }

    /// <summary>Its place in a ranking, or null.</summary>
    public string? Rank { get; init; }

    /// <summary>The tool calls waiting for the user (the same for blind and named views: a command names no model).</summary>
    public IReadOnlyList<ArenaApproval> Approvals { get; init; } = [];

    /// <summary>Whether its next turn waits on the run cap's Ask.</summary>
    public bool BudgetAsk { get; init; }

    /// <summary>The revealed footer (turns, time, cost), empty before.</summary>
    public IReadOnlyList<string> Foot { get; init; } = [];

    /// <summary>Whether it is still working.</summary>
    public bool Working => Column is { Working: true } || Column?.State.Phase == AgentModelPhase.Checking;

    /// <summary>Whether View diff is offered.</summary>
    public bool CanViewDiff => Column is { CanViewDiff: true } && !Working;

    /// <summary>The head's label: "Agent B" before the vote.</summary>
    public string Label => Reveal is { } reveal ? $"{Letter}: {reveal.Name}" : "Agent " + Letter;
}

/// <summary>A tool call of an agent waiting for the user's answer.</summary>
/// <param name="TaskId">The call's task (the answer names it).</param>
/// <param name="Tool">The tool's name.</param>
/// <param name="Text">What it wants to do: the command, or the call's arguments.</param>
public sealed record ArenaApproval(long TaskId, string Tool, string Text);

/// <summary>The vote bar (<c>_ArenaVoteBar</c>).</summary>
public sealed record ArenaVoteBarView
{
    /// <summary>The battle.</summary>
    public required ArenaBlindBattle Battle { get; init; }

    /// <summary>The tags offered, the proposed one first.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The proposed tag.</summary>
    public required string Tag { get; init; }

    /// <summary>The vote, once saved.</summary>
    public ArenaVote? Vote { get; init; }

    /// <summary>"You picked B: Echo small." and the like, once voted.</summary>
    public string? Summary { get; init; }

    /// <summary>"Added to coding · now 33 votes", once voted.</summary>
    public string? Added { get; init; }

    /// <summary>The leaderboard of the vote's tag.</summary>
    public string? LeaderboardUrl { get; init; }

    /// <summary>The share of decided pairs the left answer won so far (this mode), or null before any.</summary>
    public double? LeftShare { get; init; }

    /// <summary>The run Pin as test case saves (request mode, after the vote): the picked answer's, or A's.</summary>
    public string? PinRunId { get; init; }

    /// <summary>The agent run page where Keep is offered (agent mode, after the vote), or null.</summary>
    public string? KeepUrl { get; init; }

    /// <summary>The column Keep would keep (the pick), or null.</summary>
    public string? KeepLetter { get; init; }

    /// <summary>Whether the battle is void (nothing to vote on).</summary>
    public bool Void => Battle.Status == ArenaStatus.Void;

    /// <summary>The tooltip of the info icon.</summary>
    public string BiasText => LeftShare is { } share
        ? $"The server shuffles the order every round. So far you picked the left answer {share.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", "", StringComparison.Ordinal)} of the time; the leaderboard measures this position bias."
        : "The server shuffles the order every round; the leaderboard measures any bias toward the left answer.";
}

/// <summary>One revealed row of the Metrics tab.</summary>
/// <param name="Letter">The column.</param>
/// <param name="Name">The model's name.</param>
/// <param name="Detail">provider/model · settings.</param>
/// <param name="Picked">Whether the vote picked it.</param>
/// <param name="Metrics">Its metrics.</param>
/// <param name="StopReason">How its answer ended, or null.</param>
public sealed record ArenaMetricsRow(string Letter, string Name, string Detail, bool Picked, ContestantMetrics? Metrics, string? StopReason);

/// <summary>The Arena battle page and its swapped body.</summary>
public sealed record ArenaPageView
{
    /// <summary>The battle as the page may see it.</summary>
    public required ArenaBlindBattle Battle { get; init; }

    /// <summary>The counts of its answers.</summary>
    public required int Finished { get; init; }

    /// <summary>How many answers there are.</summary>
    public required int Total { get; init; }

    /// <summary>The shared prompt (request) or the task (agent).</summary>
    public string Prompt { get; init; } = "";

    /// <summary>The shared system prompt, or null.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The shared settings line ("thinking medium · maxTokens 4,096").</summary>
    public string? Settings { get; init; }

    /// <summary>The request columns.</summary>
    public IReadOnlyList<ArenaColumnView> Columns { get; init; } = [];

    /// <summary>The agent columns.</summary>
    public IReadOnlyList<AgentArenaColumnView> AgentColumns { get; init; } = [];

    /// <summary>The vote bar.</summary>
    public required ArenaVoteBarView VoteBar { get; init; }

    /// <summary>The revealed Metrics rows (empty before the vote).</summary>
    public IReadOnlyList<ArenaMetricsRow> Metrics { get; init; } = [];

    /// <summary>Whether the names are shown (voted or void).</summary>
    public bool Revealed => Battle.Status is ArenaStatus.Voted or ArenaStatus.Void;

    /// <summary>Whether this is an agent battle.</summary>
    public bool Agent => Battle.Mode == ArenaMode.Agent;

    /// <summary>The top bar's subtitle.</summary>
    public string Subtitle => (Agent ? "Agent · blind · " : "Blind · ") + Battle.Status switch
    {
        ArenaStatus.Voted => "revealed",
        ArenaStatus.Void => "no vote · revealed",
        _ => $"{Total} contestants, order hidden",
    };

    /// <summary>The status line by the tabs.</summary>
    public string StatusText => Battle.Status switch
    {
        ArenaStatus.Held when Agent => $"{Finished} of {Total} finished · order shuffled by the server",
        ArenaStatus.Held => $"{Finished} of {Total} finished · " + (Battle.StreamAnyway ? "streaming as they come" : "answers held until all finish"),
        ArenaStatus.Ready when Agent => $"{Finished} of {Total} finished · order shuffled by the server · vote on the diffs and checks",
        ArenaStatus.Ready => $"{Finished} of {Total} finished · order shuffled by the server",
        ArenaStatus.Voted => "Vote saved · names revealed",
        _ => "Not voted · names revealed",
    };

    /// <summary>The state name the page's script and CSS key on (the mockup's).</summary>
    public string State => (Agent ? "agent-" : "") + Battle.Status switch
    {
        ArenaStatus.Held => Agent ? "blind-held" : "blind-held",
        ArenaStatus.Ready => "blind",
        ArenaStatus.Voted => "blind-voted",
        _ => "blind-void",
    };
}

/// <summary>Builds the Arena's page views from the service, runs.db and the agent compare pages.</summary>
public sealed class ArenaPages(ArenaService arena, IServiceProvider services)
{
    /// <summary>The battle page's view: <c>Views/Compare/Arena.cshtml</c>.</summary>
    public const string PageView = "/Views/Compare/Arena.cshtml";

    /// <summary>The swapped body: <c>Views/Compare/_ArenaBattle.cshtml</c> (status line, columns, vote bar, metrics).</summary>
    public const string BodyPartial = "/Views/Compare/_ArenaBattle.cshtml";

    /// <summary>The blind agent diff: <c>Views/Compare/_ArenaAgentDiff.cshtml</c> (R6's _AgentDiff with names hidden).</summary>
    public const string DiffPartial = "/Views/Compare/_ArenaAgentDiff.cshtml";

    /// <summary>The notice on R6's agent run page for a run that is an unvoted battle: <c>Views/Compare/_ArenaBlindNotice.cshtml</c>.</summary>
    public const string NoticePartial = "/Views/Compare/_ArenaBlindNotice.cshtml";

    /// <summary>The service.</summary>
    public ArenaService Arena => arena;

    private IModelCatalog? Catalog => services.GetService<IModelCatalog>();

    /// <summary>The page of battle <paramref name="id"/>, or null when there is none.</summary>
    public async Task<ArenaPageView?> PageAsync(string id, CancellationToken cancellationToken = default)
    {
        if (arena.Get(id) is not { } battle) return null;
        var progress = arena.Progress(battle);
        var round = arena.Store.Round(battle.Id);
        var revealed = battle.Status is ArenaStatus.Voted or ArenaStatus.Void;
        var vote = battle.Status == ArenaStatus.Voted ? arena.Store.VoteOf(battle.Id) : null;
        var reveals = revealed ? battle.Contestants.Select(Reveal).ToList() : null;
        var ranks = Ranks(vote);
        var view = new ArenaPageView
        {
            Battle = battle.ToBlind(),
            Finished = progress.Finished,
            Total = progress.Total,
            Prompt = round?.Draft?.Prompt ?? Text(round?.Agent, "task") ?? "",
            SystemPrompt = round?.Draft?.SystemPrompt ?? Text(round?.Agent, "systemPrompt"),
            Settings = round?.Draft is { } draft ? Shared(draft) : null,
            VoteBar = VoteBar(battle, vote, reveals),
        };
        if (battle.Mode == ArenaMode.Request)
        {
            var metrics = revealed ? services.GetService<ArenaMetrics>() : null;
            var runs = services.GetService<RunStore>();
            var columns = new List<ArenaColumnView>();
            var rows = new List<ArenaMetricsRow>();
            foreach (var contestant in battle.Contestants)
            {
                var answer = progress.Answers[contestant.Position];
                var reveal = reveals?[contestant.Position];
                var record = revealed && contestant.RunId is { } run ? runs?.Get(run) : null;
                var measured = reveal is null ? null : metrics?.Of(battle, contestant);
                columns.Add(new ArenaColumnView
                {
                    Letter = contestant.Letter,
                    Count = battle.Contestants.Count,
                    Status = battle.Status,
                    StreamAnyway = battle.StreamAnyway,
                    Answer = answer,
                    Reveal = reveal,
                    Picked = vote?.Best == contestant.Letter,
                    Rank = ranks.GetValueOrDefault(contestant.Letter),
                    Foot = reveal is null ? [] : Foot(record, measured),
                    Error = reveal is null ? null : record is null ? "Its answer was not recorded." : record.Status switch
                    {
                        RunStatus.Completed => null,
                        RunStatus.Aborted => "Stopped before it finished.",
                        RunStatus.Interrupted => "Interrupted: the app stopped while it ran.",
                        RunStatus.Running => null,
                        _ => record.Error ?? "It ended with an error.",
                    },
                });
                if (reveal is not null) rows.Add(new ArenaMetricsRow(contestant.Letter, reveal.Name, reveal.Detail, vote?.Best == contestant.Letter, measured, record?.StopReason));
            }
            return view with { Columns = columns, Metrics = rows };
        }

        var agentRun = battle.GroupId is { } runId && services.GetService<AgentComparePages>() is { } pages
            ? await pages.RunAsync(runId, cancellationToken).ConfigureAwait(false) : null;
        var agentMetrics = revealed ? services.GetService<ArenaMetrics>() : null;
        var agentColumns = new List<AgentArenaColumnView>();
        var agentRows = new List<ArenaMetricsRow>();
        foreach (var contestant in battle.Contestants)
        {
            var slot = AgentCompareRunner.Slots[contestant.Position];
            var column = agentRun?.Columns.FirstOrDefault(item => item.Slot == slot);
            var reveal = reveals?[contestant.Position];
            var measured = reveal is null ? null : agentMetrics?.Of(battle, contestant);
            agentColumns.Add(new AgentArenaColumnView
            {
                Letter = contestant.Letter,
                Status = battle.Status,
                Column = column,
                Reveal = reveal,
                Picked = vote?.Best == contestant.Letter,
                Rank = ranks.GetValueOrDefault(contestant.Letter),
                Approvals = column is null || battle.Status is not ArenaStatus.Held ? [] : [.. column.Approvals.Select(item =>
                    new ArenaApproval(item.Request.TaskId, item.Request.Tool, AgentColumnView.CallText(item.Request)))],
                BudgetAsk = battle.Status == ArenaStatus.Held && column?.State is { BudgetAsk: not null, Outcome: null },
                Foot = reveal is null || column is null ? [] : AgentFoot(column),
            });
            if (reveal is not null) agentRows.Add(new ArenaMetricsRow(contestant.Letter, reveal.Name, reveal.Detail, vote?.Best == contestant.Letter, measured, column?.State.Outcome));
        }
        return view with { AgentColumns = agentColumns, Metrics = agentRows };
    }

    /// <summary>The blind diff of column <paramref name="letter"/> of agent battle <paramref name="id"/>, or null.</summary>
    public async Task<ArenaAgentDiffView?> DiffAsync(string id, string? letter, CancellationToken cancellationToken = default)
    {
        if (arena.Get(id) is not { Mode: ArenaMode.Agent, GroupId: { } run } battle || battle.At(letter) is not { } contestant) return null;
        if (services.GetService<AgentComparePages>() is not { } pages) return null;
        var diff = await pages.DiffAsync(run, AgentCompareRunner.Slots[contestant.Position], cancellationToken).ConfigureAwait(false);
        return diff is null ? null : new ArenaAgentDiffView(diff, battle.Status is ArenaStatus.Held or ArenaStatus.Ready, battle.Id);
    }

    private ArenaVoteBarView VoteBar(ArenaBattle battle, ArenaVote? vote, IReadOnlyList<ArenaReveal>? reveals)
    {
        var tags = arena.Tags(battle.Mode).ToList();
        var tag = vote?.Tag ?? battle.Tag ?? arena.ProposedTag(battle.Mode);
        tags.Remove(tag);
        tags.Insert(0, tag);
        var voted = arena.Voted(battle.Mode);
        var bar = new ArenaVoteBarView { Battle = battle.ToBlind(), Tags = tags, Tag = tag, LeftShare = LeftShare(voted) };
        if (vote is null || reveals is null) return bar;
        var best = vote.Best is { } letter ? reveals.FirstOrDefault(reveal => reveal.Letter == letter) : null;
        var summary = vote.Kind switch
        {
            VoteKind.Pick => $"You picked {vote.Pick}: {best?.Name}.",
            VoteKind.Tie => "You voted Tie: each pair counts as half a win.",
            VoteKind.AllBad => "You voted All bad: counted as a tie and marked all bad.",
            _ => $"Your ranking: {string.Join(", ", vote.Ranking ?? [])}, split into weighted pairs.",
        };
        var count = voted.Count(item => item.Vote.Tag == vote.Tag);
        var provisionalBelow = services.GetService<ISettingsStore>()?.Get<EvalsSettings>().ProvisionalBelow ?? EvalsSettings.DefaultProvisionalBelow;
        var comparisons = Leaderboards.ComparisonsOf(battle.Mode, vote.Tag, voted, out _);
        var perModel = comparisons.SelectMany(pair => new[] { pair.Left.Key, pair.Right.Key }).GroupBy(key => key, StringComparer.Ordinal).Select(group => group.Count());
        var provisional = perModel.Any(total => total < provisionalBelow);
        var added = $"Added to {vote.Tag} · {(count == 1 ? "1 vote" : $"now {count} votes")}{(provisional ? ", provisional" : "")}";
        var pin = battle.Mode == ArenaMode.Request ? (battle.At(vote.Best) ?? battle.At("A"))?.RunId : null;
        return bar with
        {
            Vote = vote,
            Summary = summary,
            Added = added,
            LeaderboardUrl = EvalRoutes.LeaderboardOf(battle.Mode, vote.Tag),
            PinRunId = pin,
            KeepUrl = battle.Mode == ArenaMode.Agent && battle.GroupId is { } run ? "/compare?mode=agent&run=" + Uri.EscapeDataString(run) : null,
            KeepLetter = battle.Mode == ArenaMode.Agent ? vote.Best : null,
        };
    }

    private ArenaReveal Reveal(ArenaContestant contestant)
    {
        var identity = contestant.Identity;
        var model = Catalog?.FindModel(identity.Provider, identity.Model);
        var provider = Catalog?.FindProvider(identity.Provider);
        var logo = provider?.LogoKey is { } key ? ProviderLogos.KeyUrl(key) : ProviderLogos.Url(identity.Provider);
        return new ArenaReveal(contestant.Letter, identity, model?.Name ?? identity.Model, provider?.DisplayName ?? identity.Provider, logo);
    }

    private static Dictionary<string, string> Ranks(ArenaVote? vote)
    {
        var ranks = new Dictionary<string, string>(StringComparer.Ordinal);
        if (vote?.Kind != VoteKind.Rank || vote.Ranking is not { } ranking) return ranks;
        string[] places = ["1st", "2nd", "3rd", "4th"];
        for (var index = 0; index < ranking.Count && index < places.Length; index++) ranks[ranking[index]] = places[index];
        return ranks;
    }

    /// <summary>The share of decided pairs (not ties) the left column won, over every vote of a mode; null before any.</summary>
    public static double? LeftShare(IEnumerable<VotedBattle> votes)
    {
        var decided = 0;
        var left = 0;
        foreach (var item in votes)
        {
            IReadOnlyList<ArenaComparison> pairs;
            try { pairs = item.Battle.Comparisons(item.Vote); }
            catch (ArgumentException) { continue; }
            foreach (var pair in pairs)
            {
                if (pair.LeftScore == 0.5) continue;
                decided++;
                if (pair.LeftScore == 1) left++;
            }
        }
        return decided == 0 ? null : (double)left / decided;
    }

    private static IReadOnlyList<string> Foot(RunRecord? record, ContestantMetrics? metrics)
    {
        if (record is null) return [];
        var items = new List<string>();
        if (record.Status != RunStatus.Completed && record.DurationMs is { } failed) items.Add("ended after " + RunHistoryText.Seconds(failed));
        if (record.TtftMs is { } ttft) items.Add("TTFT " + RunHistoryText.Seconds(ttft));
        if (record.DurationMs is { } total && record.Status == RunStatus.Completed) items.Add("total " + RunHistoryText.Duration(total));
        items.Add($"{RunHistoryText.Tokens(record.Usage.Input)} → {RunHistoryText.Tokens(record.Usage.Output)} tok");
        if (metrics?.CostMicros is { } cost) items.Add(metrics.ListPrice ? $"≈ {RunHistoryText.Money(cost)} list" : RunHistoryText.Money(cost));
        else if (record.CostMicros is { } recorded) items.Add(RunHistoryText.Money(recorded));
        return items;
    }

    private static IReadOnlyList<string> AgentFoot(AgentColumnView column)
    {
        var items = new List<string> { column.Turns == 1 ? "1 turn" : column.Turns + " turns" };
        if (column.ElapsedMs is { } elapsed) items.Add(AgentText.Duration(elapsed));
        if (column.SpendLine is { Length: > 0 } spend) items.Add(spend);
        return items;
    }

    private static string? Shared(PiSharp.WebAgent.Compare.CompareDraft draft)
    {
        var parts = new List<string>();
        if (draft.Thinking is { Length: > 0 } thinking) parts.Add("thinking " + thinking);
        if (draft.MaxTokens is { } max) parts.Add("maxTokens " + max.ToString("N0", CultureInfo.InvariantCulture));
        if (draft.Temperature is { } temperature) parts.Add("temperature " + temperature.ToString("0.##", CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "shared settings: provider defaults" : "shared settings: " + string.Join(" · ", parts);
    }

    private static string? Text(System.Text.Json.Nodes.JsonObject? json, string name) =>
        json?[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}

/// <summary>The blind diff modal's content: R6's view and whether names are hidden.</summary>
/// <param name="View">R6's diff view.</param>
/// <param name="Blind">Whether the battle is not voted yet (names, run id, Open as chat and Keep hidden).</param>
/// <param name="BattleId">The battle.</param>
public sealed record ArenaAgentDiffView(AgentDiffView View, bool Blind, string BattleId);
