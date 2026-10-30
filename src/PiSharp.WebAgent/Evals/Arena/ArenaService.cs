// Decision 0011 (run W5, E5): the Arena (Flagship C, blind Compare). A battle is 2 to 4 contestants that the server shuffles into columns A
// to D with a seeded Fisher–Yates (EvalRandom.Permutation; the seed is stored with the battle, brief rule 5): position p holds the model the
// user put at place perm[p] of the setup. Request battles are Compare drafts with the per-column overrides cleared (blind battles use the
// shared settings) sent through the run engine as one group under source arena (Spend Guard, runs.db; brief rule 4). The group starts in
// the user's setup order, never in column order, so an engine message ("Column 2: ...") or a spend refusal can only name the column the
// user chose, never a letter. Agent battles are agent compare runs whose models are handed to the runner in column order, so slot a is
// column A (the runner records them; its rows keep the runner's own source). A battle is held until every answer is in (or, with "stream
// anyway", shows them as they come), ready when voting opens, voted once its one vote is saved, and void when a contestant failed or the
// user stopped it (a void battle is revealed and never counted). Nothing here returns a contestant's name to a page before the vote: the
// pages read ArenaBattle.ToBlind and the counts of Progress; the controller and endpoints decide what to show by the status.
// W5 integration fix 1: the service is the Runs area's IRunBlindSource (HiddenGroups: every key of Blind() with its battle's page, which agent
// compare's JSON API and event stream refuse until the vote); an agent battle's run is recorded under source arena with
// the Arena's title (AgentCompareCreateRequest.Source and Title); and a saved vote pins its contestants' runs in runs.db (RunStore.Pin; an
// agent battle's top row, which keeps its models and calls), so runs.db's retention never takes the leaderboard's cost and speed medians
// (ArenaMetrics) or the reveal's inspector links from a counted vote.
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Arena;

/// <summary>A battle that was made, and the Spend Guard's refusal when its answers could not start (nothing was reserved or sent; start it
/// again with the user's answer).</summary>
public sealed record ArenaStart(ArenaBattle Battle, RunRefusal? Refusal)
{
    /// <summary>Whether the answers started.</summary>
    public bool Posted => Refusal is null;
}

/// <summary>One column's answer as far as it got.</summary>
/// <param name="Letter">The column.</param>
/// <param name="Done">Whether it ended (completed, failed or stopped).</param>
/// <param name="Failed">Whether it ended without an answer to vote on.</param>
/// <param name="Text">The answer so far (request mode), or null.</param>
public sealed record ArenaAnswer(string Letter, bool Done, bool Failed, string? Text);

/// <summary>How far a battle's answers are: counts only, which name no one.</summary>
/// <param name="Finished">How many ended.</param>
/// <param name="Total">How many there are.</param>
/// <param name="Answers">Each column's answer, A first.</param>
public sealed record ArenaProgress(int Finished, int Total, IReadOnlyList<ArenaAnswer> Answers)
{
    /// <summary>A fingerprint of what a page would show: the counts and how much text each column has.</summary>
    public string Signature => Finished + "/" + Total + ":" + string.Join(",", Answers.Select(answer => (answer.Done ? "d" : "") + (answer.Text?.Length ?? 0)));
}

/// <summary>The outcome of a vote.</summary>
/// <param name="Battle">The battle, now voted.</param>
/// <param name="Vote">The stored vote.</param>
/// <param name="Saved">False when the same vote was already stored (a repeated request).</param>
public sealed record ArenaVoteResult(ArenaBattle Battle, ArenaVote Vote, bool Saved);

/// <summary>Blind battles: create, start, follow, stop, vote.</summary>
public sealed class ArenaService : IDisposable, IRunBlindSource
{
    /// <summary>The tag proposed for a request battle when no vote has used one yet.</summary>
    public const string DefaultTag = "general";

    /// <summary>The <c>title</c> of an Arena run in runs.db (it names no model).</summary>
    public const string RunTitle = "Arena battle";

    /// <summary>How long a battle that never started (a refusal nobody answered) is kept.</summary>
    public static TimeSpan Unstarted { get; } = TimeSpan.FromDays(1);

    private readonly ArenaStore store;
    private readonly IServiceProvider services;
    private readonly TimeProvider clock;
    private readonly RunStore? runs;
    private readonly ConcurrentDictionary<string, string> runBattles = new(StringComparer.Ordinal);
    private AgentCompareStore? agents;
    private int scanned;

    /// <summary>The service over <paramref name="store"/>; the run engine, runs.db, live runs and the agent compare runner come from
    /// <paramref name="services"/> when registered.</summary>
    public ArenaService(ArenaStore store, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(services);
        this.store = store;
        this.services = services;
        clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        runs = services.GetService<RunStore>();
        if (runs is not null) runs.Changed += OnRunsChanged;
    }

    /// <summary>Raised with a battle's id when its state or its answers changed (for the blind event stream).</summary>
    public event Action<string>? Changed;

    /// <summary>The store.</summary>
    public ArenaStore Store => store;

    // ---- create and start ---------------------------------------------------------------------------------------------------------

    /// <summary>The draft as a blind battle sends it: the shared prompt and options, and each column's model and account, with the
    /// per-column overrides cleared (every answer uses the shared settings).</summary>
    public static CompareDraft Blind(CompareDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var trimmed = draft.Trimmed();
        return trimmed with { Columns = [.. trimmed.Columns.Select(column => column with { Thinking = null, Temperature = null })] };
    }

    /// <summary>Makes a request battle from <paramref name="draft"/> and starts its answers (one run group under source arena).</summary>
    /// <exception cref="CompareRefusedException">A column cannot run; nothing was made, reserved or sent.</exception>
    /// <exception cref="ArgumentException">The draft is not a battle (2 to 4 different models), or the engine refused it.</exception>
    public async Task<ArenaStart> CreateRequestAsync(CompareDraft draft, bool streamAnyway = false, RunSpendAnswer? spend = null,
        CancellationToken cancellationToken = default)
    {
        var blind = Blind(draft);
        if (blind.Columns.Count is < ArenaLetters.MinContestants or > ArenaLetters.MaxContestants)
            throw new ArgumentException($"A blind round compares {ArenaLetters.MinContestants} to {ArenaLetters.MaxContestants} models.");
        var specs = Specs(blind);
        var identities = specs.Select(ModelIdentity.Of).ToList();
        if (identities.Select(identity => identity.Key).Distinct(StringComparer.Ordinal).Count() != identities.Count)
            throw new ArgumentException("Two columns are the same model with the same settings; a blind vote between them would rate nothing. Choose different models.");
        var seed = EvalRandom.NewSeed();
        var order = new EvalRandom(seed).Permutation(identities.Count);
        var battle = store.Add(new ArenaBattle
        {
            Id = EvalIds.New(EvalIds.Battle),
            Mode = ArenaMode.Request,
            Tag = ProposedTag(ArenaMode.Request),
            Status = ArenaStatus.Held,
            Seed = seed,
            Contestants = [.. Enumerable.Range(0, identities.Count).Select(position => new ArenaContestant(position, identities[order[position]]))],
            GroupId = "g" + Guid.NewGuid().ToString("N"),
            StreamAnyway = streamAnyway,
            Created = clock.GetUtcNow(),
        }, new ArenaRound { Draft = blind });
        try
        {
            return await StartGroupAsync(battle, specs, spend, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            store.Delete(battle.Id);
            throw;
        }
    }

    /// <summary>Starts the answers of request battle <paramref name="id"/> that the Spend Guard refused, with the user's
    /// <paramref name="spend"/> answer (its group keeps its id, so the answer is the group's).</summary>
    /// <exception cref="KeyNotFoundException">There is no such battle.</exception>
    /// <exception cref="InvalidOperationException">It is not a request battle waiting to start.</exception>
    public async Task<ArenaStart> StartAsync(string id, RunSpendAnswer? spend, CancellationToken cancellationToken = default)
    {
        var battle = Require(id);
        if (battle.Mode != ArenaMode.Request || battle.Status != ArenaStatus.Held || battle.Contestants.Any(contestant => contestant.RunId is not null))
            throw new InvalidOperationException("This battle already started.");
        var draft = store.Round(battle.Id)?.Draft ?? throw new InvalidOperationException("This battle's request is gone; start a new round.");
        var specs = Specs(draft);
        var order = new EvalRandom(battle.Seed).Permutation(specs.Count);
        for (var position = 0; position < specs.Count; position++)
            if (specs.Count != battle.Contestants.Count || !ModelIdentity.Of(specs[order[position]]).SameAs(battle.Contestants[position].Identity))
                throw new InvalidOperationException("The models of this round changed since it was made; start a new round.");
        return await StartGroupAsync(battle, specs, spend, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes a new request battle that asks what battle <paramref name="id"/> asked, with a new shuffle (New round).</summary>
    /// <exception cref="KeyNotFoundException">There is no such battle.</exception>
    public Task<ArenaStart> AgainAsync(string id, RunSpendAnswer? spend = null, CancellationToken cancellationToken = default)
    {
        var battle = Require(id);
        var round = store.Round(battle.Id);
        if (battle.Mode != ArenaMode.Request || round?.Draft is not { } draft) throw new InvalidOperationException("Only a request battle can start a new round here.");
        return CreateRequestAsync(draft, battle.StreamAnyway, spend, cancellationToken);
    }

    /// <summary>Makes an agent battle: the agent compare run of <paramref name="body"/> (R5's create request), with its models handed to the
    /// runner in column order. A repeated create (the same request id) answers the battle it made.</summary>
    /// <exception cref="AgentCompareException">The runner refused the request.</exception>
    /// <exception cref="ArgumentException">It is not a battle (2 to 4 different models).</exception>
    /// <exception cref="InvalidOperationException">Agent compare is not available in this app.</exception>
    public async Task<ArenaBattle> CreateAgentAsync(JsonObject body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var runner = services.GetService<AgentCompareRunner>() ?? throw new InvalidOperationException("Agent compare is not available.");
        var request = AgentCompareEndpoints.CreateRequest(body);
        if (request.Models.Count is < ArenaLetters.MinContestants or > ArenaLetters.MaxContestants)
            throw new ArgumentException($"A blind agent round compares {ArenaLetters.MinContestants} to {ArenaLetters.MaxContestants} models.");
        var asked = request.Models.Select(model => new ModelIdentity(model.Provider, model.Model, model.Thinking).Normalize()).ToList();
        if (asked.Select(identity => identity.Key).Distinct(StringComparer.Ordinal).Count() != asked.Count)
            throw new ArgumentException("Two columns are the same model with the same settings; a blind vote between them would rate nothing. Choose different models.");
        var requestId = request.RequestId.Trim();
        if (requestId.Length is 0 or > 100) throw AgentCompareException.BadRequest("A create needs a request id of 1 to 100 characters.");
        var seed = EvalRandom.NewSeed();
        var order = new EvalRandom(seed).Permutation(asked.Count);
        var run = await runner.CreateAsync(request with
        {
            RequestId = "arena-" + requestId,
            Models = [.. Enumerable.Range(0, asked.Count).Select(position => request.Models[order[position]])],
            Source = SpendSource.Arena,
            Title = RunTitle,
        }, cancellationToken).ConfigureAwait(false);
        if (store.ByGroup(run.Id).FirstOrDefault() is { } existing) return Refresh(existing);
        var contestants = new List<ArenaContestant>();
        for (var position = 0; position < run.Spec.Models.Count && position < AgentCompareRunner.Slots.Count; position++)
        {
            var slot = AgentCompareRunner.Slots[position];
            var model = run.ModelSpec(slot) ?? throw new InvalidOperationException("The agent run lost a model.");
            contestants.Add(new ArenaContestant(position, new ModelIdentity(model.Provider, model.Model, model.Thinking), AgentCompareRun.ModelKey(run.Id, slot)));
        }
        var round = (JsonObject)body.DeepClone();
        round.Remove("requestId");
        round.Remove("confirmLargeCopy");
        ArenaBattle battle;
        try
        {
            battle = store.Add(new ArenaBattle
            {
                Id = EvalIds.New(EvalIds.Battle),
                Mode = ArenaMode.Agent,
                Tag = ProposedTag(ArenaMode.Agent),
                Status = ArenaStatus.Held,
                Seed = seed,
                Contestants = contestants,
                GroupId = run.Id,
                Created = clock.GetUtcNow(),
            }, new ArenaRound { Agent = round });
        }
        catch (ArgumentException)
        {
            // Two models became the same identity once the runner applied their thinking levels: nothing to vote on.
            try { await runner.StopAsync(run.Id, null, CancellationToken.None).ConfigureAwait(false); }
            catch (AgentCompareException) { /* It ended already. */ }
            throw new ArgumentException("Two columns ended up as the same model with the same settings, so the round was stopped. Choose different models.");
        }
        FollowAgents();
        return battle;
    }

    // ---- read and follow ----------------------------------------------------------------------------------------------------------

    /// <summary>Battle <paramref name="id"/>, its status brought up to date from its runs, or null.</summary>
    public ArenaBattle? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !EvalIds.IsValid(EvalIds.Battle, id.Trim())) return null;
        return store.Get(id.Trim()) is { } battle ? Refresh(battle) : null;
    }

    /// <summary>Battle <paramref name="id"/>, up to date.</summary>
    /// <exception cref="KeyNotFoundException">There is no such battle.</exception>
    public ArenaBattle Require(string id) => Get(id) ?? throw new KeyNotFoundException($"There is no battle {id}.");

    /// <summary>The battle that owns agent compare run <paramref name="runId"/> and is not voted yet (its names stay hidden), or null.</summary>
    public ArenaBattle? UnvotedAgentBattle(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return null;
        return store.ByGroup(runId.Trim()).Where(battle => battle.Mode == ArenaMode.Agent).Select(Refresh)
            .FirstOrDefault(battle => battle.Status is ArenaStatus.Held or ArenaStatus.Ready);
    }

    /// <summary>What is blind now (brief rule 3, for every page outside the battle's own): the keys of each held or ready battle, either
    /// mode: a request battle's run group and its columns' run ids; an agent battle's agent compare run id and its model keys
    /// (<c>&lt;run&gt;-&lt;slot&gt;</c>, slot a being column A). A held battle is brought up to date first, so one whose runs ended or went
    /// away stops hiding them as soon as it is ready-and-voted or void.</summary>
    public ArenaBlindSet Blind()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var status in new[] { ArenaStatus.Held, ArenaStatus.Ready })
        {
            foreach (var stored in store.WithStatus(status))
            {
                var battle = status == ArenaStatus.Held ? Refresh(stored) : stored;
                if (battle.Status is not (ArenaStatus.Held or ArenaStatus.Ready)) continue;
                if (battle.GroupId is { Length: > 0 } group) keys.TryAdd(group, battle.Id);
                foreach (var contestant in battle.Contestants)
                    if (contestant.RunId is { Length: > 0 } run) keys.TryAdd(run, battle.Id);
                if (battle.Mode == ArenaMode.Agent && battle.GroupId is { Length: > 0 } agentRun)
                    foreach (var slot in AgentCompareRunner.Slots.Take(battle.Contestants.Count))
                        keys.TryAdd(AgentCompareRun.ModelKey(agentRun, slot), battle.Id);
            }
        }
        return keys.Count == 0 ? ArenaBlindSet.Empty : new ArenaBlindSet(keys);
    }

    /// <summary>Whether <paramref name="runIdOrKey"/> (a run id, a run group, an agent compare run id or one of its model keys) belongs to a
    /// battle that is held or ready, in either mode: its model stays hidden everywhere until the vote.</summary>
    public bool Hidden(string? runIdOrKey) => Blind().Hides(runIdOrKey);

    /// <summary>The held or ready battle that <paramref name="runIdOrKey"/> belongs to (see <see cref="Hidden"/>), or null.</summary>
    public string? HidingBattle(string? runIdOrKey) => Blind().BattleOf(runIdOrKey);

    /// <inheritdoc />
    /// <remarks>Every key of <see cref="Blind"/> (the one blind predicate: a held or ready battle's run group and run ids, or its agent run id
    /// and model keys) mapped to its battle's page; a voted or void battle hides nothing.</remarks>
    public IReadOnlyDictionary<string, string> HiddenGroups()
    {
        var blind = Blind();
        var hidden = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in blind.Keys)
            if (blind.BattleOf(key) is { } battle) hidden[key] = ArenaEndpoints.PageUrl(battle);
        return hidden;
    }

    /// <summary>The tag a new battle of <paramref name="mode"/> proposes: a request battle the one the last request vote used, else
    /// <see cref="DefaultTag"/>; an agent battle always <see cref="EvalTags.Agent"/> (compare.html's agent select starts on agent; another
    /// agent tag is picked or typed per vote).</summary>
    public string ProposedTag(ArenaMode mode) => mode == ArenaMode.Agent ? EvalTags.Agent : store.Tags(mode).FirstOrDefault() ?? DefaultTag;

    /// <summary>The tags offered for a vote of <paramref name="mode"/>: the ones its votes used, most recent first, and the default.</summary>
    public IReadOnlyList<string> Tags(ArenaMode mode)
    {
        var tags = store.Tags(mode).ToList();
        var fallback = mode == ArenaMode.Agent ? EvalTags.Agent : DefaultTag;
        if (!tags.Contains(fallback, StringComparer.Ordinal)) tags.Add(fallback);
        return tags;
    }

    /// <summary>How far battle <paramref name="battle"/>'s answers are. Text is filled only when a page may show it: once every answer is
    /// in, or while held with stream anyway on. Failed answers carry no text (an error message could name a provider).</summary>
    public ArenaProgress Progress(ArenaBattle battle)
    {
        ArgumentNullException.ThrowIfNull(battle);
        var showText = battle.Status != ArenaStatus.Held || battle.StreamAnyway;
        var answers = new List<ArenaAnswer>();
        foreach (var contestant in battle.Contestants)
        {
            if (battle.Mode == ArenaMode.Agent)
            {
                answers.Add(AgentAnswer(battle, contestant));
                continue;
            }
            var record = contestant.RunId is { } run ? runs?.Get(run) : null;
            var done = record is not null && record.Status != RunStatus.Running;
            var failed = done && record!.Status != RunStatus.Completed;
            string? text = null;
            if (showText && !failed && record is not null)
                text = done ? record.OutputText ?? "" : Partial(contestant.RunId!);
            answers.Add(new ArenaAnswer(contestant.Letter, done, failed, text));
        }
        return new ArenaProgress(answers.Count(answer => answer.Done), answers.Count, answers);
    }

    /// <summary>What an agent battle waits on the user for, as a fingerprint for its event stream (which letters have calls waiting or a
    /// budget Ask): the page reloads to show them. Empty for a request battle or one not held.</summary>
    public string Waiting(ArenaBattle battle)
    {
        ArgumentNullException.ThrowIfNull(battle);
        if (battle.Mode != ArenaMode.Agent || battle.Status != ArenaStatus.Held || battle.GroupId is not { } id) return "";
        if (services.GetService<AgentCompareRunner>() is not { } runner || runner.Store.Get(id) is not { } run) return "";
        var calls = runner.Pending(run).Select(item => item.Slot + ":" + item.Approval.Request.TaskId);
        var asks = run.Models.Where(model => model.BudgetAsk is not null && model.Outcome is null).Select(model => model.Slot + ":budget");
        return string.Join(",", calls.Concat(asks));
    }

    /// <summary>Every voted battle with its vote (of <paramref name="mode"/>, or both): the leaderboards' input.</summary>
    public IReadOnlyList<VotedBattle> Voted(ArenaMode? mode = null) => store.Voted(mode);

    // ---- change -------------------------------------------------------------------------------------------------------------------

    /// <summary>Turns stream anyway on or off for battle <paramref name="id"/>.</summary>
    public ArenaBattle SetStreamAnyway(string id, bool on)
    {
        var battle = Require(id);
        var next = store.Update(battle.Id, current => current.StreamAnyway == on ? current : current with { StreamAnyway = on }) ?? battle;
        Raise(next.Id);
        return next;
    }

    /// <summary>Stops battle <paramref name="id"/>: its running answers are cancelled and it becomes void (revealed, never counted). A voted
    /// battle is left as it is.</summary>
    public async Task<ArenaBattle> StopAsync(string id, CancellationToken cancellationToken = default)
    {
        var battle = Require(id);
        if (battle.Status is ArenaStatus.Voted or ArenaStatus.Void) return battle;
        if (battle.Mode == ArenaMode.Request)
        {
            var engine = services.GetService<RunEngine>();
            foreach (var contestant in battle.Contestants)
                if (contestant.RunId is { } run) engine?.Cancel(run);
        }
        else if (battle.GroupId is { } run && services.GetService<AgentCompareRunner>() is { } runner)
        {
            try { await runner.StopAsync(run, null, cancellationToken).ConfigureAwait(false); }
            catch (AgentCompareException) { /* The run is gone or ended. */ }
        }
        var stopped = store.Update(battle.Id, current => current.Status is ArenaStatus.Held or ArenaStatus.Ready ? current with { Status = ArenaStatus.Void } : current) ?? battle;
        Raise(stopped.Id);
        return stopped;
    }

    /// <summary>Saves the vote of battle <paramref name="id"/> and reveals it. The tag defaults to the battle's proposed one.</summary>
    /// <exception cref="KeyNotFoundException">There is no such battle.</exception>
    /// <exception cref="InvalidOperationException">The battle is not ready, or has another vote.</exception>
    /// <exception cref="ArgumentException">The vote does not fit the battle.</exception>
    public ArenaVoteResult Vote(string id, VoteKind kind, string? pick, IReadOnlyList<string>? ranking, string? tag)
    {
        var battle = Require(id);
        var (voted, vote, saved) = store.Vote(new ArenaVote
        {
            BattleId = battle.Id,
            Mode = battle.Mode,
            Tag = string.IsNullOrWhiteSpace(tag) ? battle.Tag ?? ProposedTag(battle.Mode) : tag,
            Kind = kind,
            Pick = pick,
            Ranking = ranking,
            Created = clock.GetUtcNow(),
        });
        if (saved) Raise(voted.Id);
        Keep(voted);
        return new ArenaVoteResult(voted, vote, saved);
    }

    /// <summary>Deletes battle <paramref name="id"/> when it never started (a refusal the user dismissed). A started battle stays.</summary>
    public bool Discard(string id)
    {
        var battle = Require(id);
        return battle.Contestants.All(contestant => contestant.RunId is null) && store.Delete(battle.Id);
    }

    /// <summary>Brings <paramref name="battle"/>'s status up to date from its runs: held until every answer ended, then ready, or void when
    /// one failed (request) or the agent run failed or is gone (agent). Battles that never started and are older than
    /// <see cref="Unstarted"/> are deleted.</summary>
    public ArenaBattle Refresh(ArenaBattle battle)
    {
        ArgumentNullException.ThrowIfNull(battle);
        if (battle.Status != ArenaStatus.Held) return battle;
        var next = Evaluate(battle);
        if (next is null)
        {
            if (clock.GetUtcNow() - battle.Created > Unstarted) store.Delete(battle.Id);
            return battle;
        }
        if (next == battle.Status) return battle;
        var updated = store.Update(battle.Id, current => current.Status == ArenaStatus.Held ? current with { Status = next.Value } : current) ?? battle;
        Raise(updated.Id);
        return updated;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (runs is not null) runs.Changed -= OnRunsChanged;
        if (Interlocked.Exchange(ref agents, null) is { } store) store.Changed -= OnAgentChanged;
    }

    // ---- internals ----------------------------------------------------------------------------------------------------------------

    // A counted vote keeps its runs: each request answer's row, or an agent battle's top row (a pin keeps its models and their calls).
    // Pinning again is harmless, so a repeated vote pins what an earlier attempt may have missed; a run already gone stays gone.
    private void Keep(ArenaBattle battle)
    {
        if (runs is null) return;
        List<string> ids = battle.Mode == ArenaMode.Agent ? [.. battle.GroupId is { } group ? [group] : Array.Empty<string>()]
            : [.. battle.Contestants.Select(contestant => contestant.RunId).OfType<string>()];
        foreach (var id in ids)
        {
            try { runs.Pin(id, true); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger<ArenaService>().LogWarning(error, "Run {Run} of battle {Battle} could not be pinned.", id, battle.Id);
            }
        }
    }

    private List<RunSpec> Specs(CompareDraft draft)
    {
        var engine = services.GetRequiredService<RunEngine>();
        var plan = ComparePlanner.Plan(draft, engine);
        return [.. plan.Specs.Select(spec => spec with { Source = SpendSource.Arena.Wire(), Title = RunTitle })];
    }

    private async Task<ArenaStart> StartGroupAsync(ArenaBattle battle, IReadOnlyList<RunSpec> specs, RunSpendAnswer? spend, CancellationToken cancellationToken)
    {
        var engine = services.GetRequiredService<RunEngine>();
        // Setup order: the engine's messages and refusals can only name the column the user chose.
        var start = await engine.StartGroupAsync(specs, battle.GroupId, spend, cancellationToken).ConfigureAwait(false);
        if (!start.Posted) return new ArenaStart(battle, start.Refusal);
        var order = new EvalRandom(battle.Seed).Permutation(specs.Count);
        foreach (var id in start.Ids) runBattles[id] = battle.Id;
        var started = store.Update(battle.Id, current => current with
        {
            Contestants = [.. current.Contestants.Select(contestant => contestant with { RunId = start.Ids[order[contestant.Position]] })],
        }) ?? battle;
        Raise(started.Id);
        return new ArenaStart(Refresh(started), null);
    }

    private ArenaStatus? Evaluate(ArenaBattle battle)
    {
        if (battle.Mode == ArenaMode.Agent)
        {
            if (battle.GroupId is not { } id || services.GetService<AgentCompareRunner>() is not { } runner) return ArenaStatus.Void;
            FollowAgents();
            var run = runner.Store.Get(id);
            if (run is null || run.Deleting) return ArenaStatus.Void;
            if (run.IsActive) return ArenaStatus.Held;
            return run.State == AgentRunState.Failed ? ArenaStatus.Void : ArenaStatus.Ready;
        }
        if (battle.Contestants.Any(contestant => contestant.RunId is null)) return null;
        var records = battle.Contestants.Select(contestant => runs?.Get(contestant.RunId!)).ToList();
        if (records.Any(record => record is null)) return ArenaStatus.Void;
        if (records.Any(record => record!.Status == RunStatus.Running)) return ArenaStatus.Held;
        return records.All(record => record!.Status == RunStatus.Completed) ? ArenaStatus.Ready : ArenaStatus.Void;
    }

    private ArenaAnswer AgentAnswer(ArenaBattle battle, ArenaContestant contestant)
    {
        var slot = AgentCompareRunner.Slots[contestant.Position];
        var run = battle.GroupId is { } id ? services.GetService<AgentCompareRunner>()?.Store.Get(id) : null;
        var model = run?.Model(slot);
        var done = model?.Phase == AgentModelPhase.Done;
        return new ArenaAnswer(contestant.Letter, done, done && model!.Outcome is AgentModelOutcome.Failed or AgentModelOutcome.Error, null);
    }

    private string Partial(string runId)
    {
        if (services.GetService<RunLive>()?.Find(runId) is not { } live) return "";
        var text = new StringBuilder();
        foreach (var item in live.Items)
            if (item.Event["type"]?.GetValue<string>() == "text_delta" && item.Event["delta"] is JsonValue delta && delta.TryGetValue<string>(out var piece))
                text.Append(piece);
        return text.ToString();
    }

    private void OnRunsChanged(object? sender, RunChange change)
    {
        if (Interlocked.Exchange(ref scanned, 1) == 0)
            foreach (var held in store.WithStatus(ArenaStatus.Held).Where(battle => battle.Mode == ArenaMode.Request))
                foreach (var contestant in held.Contestants)
                    if (contestant.RunId is { } run) runBattles.TryAdd(run, held.Id);
        foreach (var battleId in change.Ids.Select(id => runBattles.TryGetValue(id, out var battle) ? battle : null).OfType<string>().Distinct(StringComparer.Ordinal).ToList())
        {
            try
            {
                if (store.Get(battleId) is not { } battle) continue;
                var refreshed = Refresh(battle);
                if (refreshed.Status != ArenaStatus.Held)
                    foreach (var contestant in refreshed.Contestants)
                        if (contestant.RunId is { } run) runBattles.TryRemove(run, out _);
                Raise(battleId);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger<ArenaService>().LogWarning(error, "Battle {Battle} could not follow its runs.", battleId);
            }
        }
    }

    private void FollowAgents()
    {
        if (Volatile.Read(ref agents) is not null || services.GetService<AgentCompareRunner>() is not { } runner) return;
        if (Interlocked.CompareExchange(ref agents, runner.Store, null) is null) runner.Store.Changed += OnAgentChanged;
    }

    private void OnAgentChanged(string run)
    {
        try
        {
            foreach (var battle in store.ByGroup(run).Where(battle => battle.Mode == ArenaMode.Agent))
            {
                Refresh(battle);
                Raise(battle.Id);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<ArenaService>().LogWarning(error, "The battles of agent run {Run} could not follow it.", run);
        }
    }

    private void Raise(string battleId)
    {
        try { Changed?.Invoke(battleId); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<ArenaService>().LogWarning(error, "A listener of battle {Battle} failed.", battleId);
        }
    }
}
