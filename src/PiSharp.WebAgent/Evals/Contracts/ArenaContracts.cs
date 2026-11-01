// Decision 0011 (run W5, E0): the Arena's battles and votes (mockup compare: blind-held, blind, blind-voted, agent-blind, agent-blind-voted,
// _ArenaColumn, _AgentArenaColumn, _ArenaVoteBar with its av-pick and av-rank panes). A battle is 2 to 4 contestants that the server
// shuffled into columns A to D with a seeded Fisher–Yates (EvalRandom; the seed is stored with the positions, brief rule 5); it is held
// while answers are held until all finish, ready when voting opens, voted once the one vote is saved, and void when it cannot be voted on
// (a contestant failed, or the user stopped it). A vote picks a column, calls a tie, says all were bad (a tie, marked all bad), or ranks
// every column (3 or more answers), under a tag; request and agent votes are kept apart by mode. Comparisons splits a vote into pairs for
// the rater: each pair is ordered left to right (the lower column first), so the rater can measure the user's left-position bias; how much
// a pair weighs is the rater's choice (E1). Before the vote nothing that names a contestant may reach the page (brief rule 3): ToBlind is
// the only view of a battle a page gets until then.
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Evals;

/// <summary>What was compared: single answers (Compare's request mode) or whole agent runs (agent compare). Sent as <c>request</c>,
/// <c>agent</c>. Votes of the two modes never share a leaderboard.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArenaMode>))]
public enum ArenaMode
{
    /// <summary>One answer per contestant (<c>request</c>).</summary>
    [JsonStringEnumMemberName("request")] Request,

    /// <summary>One agent run per contestant, voted on its diff and check result (<c>agent</c>).</summary>
    [JsonStringEnumMemberName("agent")] Agent,
}

/// <summary>Where a battle is. Sent as <c>held</c>, <c>ready</c>, <c>voted</c>, <c>void</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArenaStatus>))]
public enum ArenaStatus
{
    /// <summary>Answers are held until every contestant finishes (mockup <c>blind-held</c>).</summary>
    [JsonStringEnumMemberName("held")] Held,

    /// <summary>Every answer is in; voting is open (mockup <c>blind</c>, <c>agent-blind</c>).</summary>
    [JsonStringEnumMemberName("ready")] Ready,

    /// <summary>The vote is saved and the contestants are revealed (mockup <c>blind-voted</c>, <c>agent-blind-voted</c>).</summary>
    [JsonStringEnumMemberName("voted")] Voted,

    /// <summary>No vote can be taken (a contestant failed, or the user stopped the battle); it never counts on a leaderboard.</summary>
    [JsonStringEnumMemberName("void")] Void,
}

/// <summary>The kind of an Arena vote. Sent as <c>pick</c>, <c>tie</c>, <c>all-bad</c>, <c>rank</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VoteKind>))]
public enum VoteKind
{
    /// <summary>One column is the best (<see cref="ArenaVote.Pick"/>): it beats each of the others.</summary>
    [JsonStringEnumMemberName("pick")] Pick,

    /// <summary>All columns are as good: every pair is a tie (half a win each).</summary>
    [JsonStringEnumMemberName("tie")] Tie,

    /// <summary>All columns are bad: counted as a tie, and marked all bad.</summary>
    [JsonStringEnumMemberName("all-bad")] AllBad,

    /// <summary>Every column ranked, best first (<see cref="ArenaVote.Ranking"/>; 3 or more answers): each beats every one ranked below it.</summary>
    [JsonStringEnumMemberName("rank")] Rank,
}

/// <summary>Column letters: position 0 is A (the leftmost column), up to D.</summary>
public static class ArenaLetters
{
    /// <summary>The fewest contestants in a battle.</summary>
    public const int MinContestants = 2;

    /// <summary>The most contestants in a battle.</summary>
    public const int MaxContestants = 4;

    /// <summary>The letters, A first.</summary>
    public static IReadOnlyList<string> All { get; } = ["A", "B", "C", "D"];

    /// <summary>The letter of <paramref name="position"/> (0 is A).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not 0 to 3.</exception>
    public static string Of(int position) => position >= 0 && position < All.Count ? All[position]
        : throw new ArgumentOutOfRangeException(nameof(position), position, "A battle has columns A to D.");

    /// <summary>The position of <paramref name="letter"/> (A is 0; case and spaces are ignored), or -1.</summary>
    public static int PositionOf(string? letter)
    {
        var text = letter?.Trim().ToUpperInvariant();
        for (var index = 0; index < All.Count; index++) if (All[index] == text) return index;
        return -1;
    }
}

/// <summary>One contestant of a battle: where the shuffle put it, who it is, and the run that answered for it.</summary>
/// <param name="Position">Its column, 0 (A, the leftmost) to 3 (D).</param>
/// <param name="Identity">The model and settings (never the account).</param>
/// <param name="RunId">The runs.db row of its answer (request mode) or its agent compare model run (agent mode), once started; never sent to
/// the page before the vote.</param>
public sealed record ArenaContestant(int Position, ModelIdentity Identity, string? RunId = null)
{
    /// <summary>Its column letter (A to D).</summary>
    public string Letter => ArenaLetters.Of(Position);
}

/// <summary>A battle as a page may see it before the vote: the columns' letters and the battle's state, and nothing that names, links to or
/// times a contestant.</summary>
/// <param name="Id">The battle id.</param>
/// <param name="Mode">Request or agent.</param>
/// <param name="Status">Its state.</param>
/// <param name="Letters">The column letters, A first.</param>
/// <param name="StreamAnyway">Whether answers stream as they come instead of being held.</param>
/// <param name="Tag">The tag proposed for the vote, or null.</param>
public sealed record ArenaBlindBattle(string Id, ArenaMode Mode, ArenaStatus Status, IReadOnlyList<string> Letters, bool StreamAnyway, string? Tag);

/// <summary>One pair of contestants from a vote, as the rater reads it: the left (lower) column first, and how the left one did.</summary>
/// <param name="BattleId">The battle.</param>
/// <param name="Mode">Request or agent (separate leaderboards).</param>
/// <param name="Tag">The vote's tag.</param>
/// <param name="Kind">The vote's kind (a rater may weigh a ranking's pairs differently from a pick's).</param>
/// <param name="Contestants">How many contestants the battle had.</param>
/// <param name="Left">The contestant in the left column of the pair.</param>
/// <param name="Right">The contestant in the right column of the pair.</param>
/// <param name="LeftPosition">The left one's column (0 is A).</param>
/// <param name="RightPosition">The right one's column; always greater than <paramref name="LeftPosition"/>.</param>
/// <param name="LeftScore">1 when the left one won, 0 when the right one won, 0.5 for a tie (and for all bad).</param>
public sealed record ArenaComparison(string BattleId, ArenaMode Mode, string Tag, VoteKind Kind, int Contestants, ModelIdentity Left, ModelIdentity Right,
    int LeftPosition, int RightPosition, double LeftScore);

/// <summary>An Arena battle: its shuffled contestants, its seed and its state.</summary>
public sealed record ArenaBattle
{
    /// <summary>The battle id (<see cref="EvalIds.Battle"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Request or agent.</summary>
    public ArenaMode Mode { get; init; } = ArenaMode.Request;

    /// <summary>The tag: the one proposed for the vote (the last one used, or <see cref="EvalTags.Agent"/> in agent mode) until the vote,
    /// then the vote's; null when none was proposed.</summary>
    public string? Tag { get; init; }

    /// <summary>Its state.</summary>
    public ArenaStatus Status { get; init; } = ArenaStatus.Held;

    /// <summary>The seed of the shuffle that placed the contestants (0 to <see cref="EvalRandom.MaxSeed"/>).</summary>
    public required long Seed { get; init; }

    /// <summary>The contestants, in column order (A first), 2 to 4.</summary>
    public IReadOnlyList<ArenaContestant> Contestants { get; init; } = [];

    /// <summary>The Compare run group (request mode) or the agent compare run (agent mode) the answers belong to, or null before it starts.
    /// Never sent to the page before the vote.</summary>
    public string? GroupId { get; init; }

    /// <summary>Whether answers stream as they come ("stream anyway") instead of being held until all finish.</summary>
    public bool StreamAnyway { get; init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When it was voted on, or null.</summary>
    public DateTimeOffset? Voted { get; init; }

    /// <summary>The column letters, A first.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Letters => [.. Contestants.Select(contestant => contestant.Letter)];

    /// <summary>The same battle checked and in canonical form: contestants sorted by column, columns 0 to n−1 each used once, identities
    /// normalized and distinct, the tag normalized.</summary>
    /// <exception cref="ArgumentException">Something is not allowed; the message says what.</exception>
    public ArenaBattle Normalize()
    {
        var id = EvalIds.Require(EvalIds.Battle, Id, "battle");
        if (!Enum.IsDefined(Mode)) throw new ArgumentException($"Unknown arena mode {(int)Mode}.");
        if (!Enum.IsDefined(Status)) throw new ArgumentException($"Unknown battle status {(int)Status}.");
        if (Seed < 0 || Seed > EvalRandom.MaxSeed) throw new ArgumentException("A battle's seed is 0 to 2^53 − 1.");
        var contestants = (Contestants ?? []).ToList();
        if (contestants.Count is < ArenaLetters.MinContestants or > ArenaLetters.MaxContestants)
            throw new ArgumentException($"A battle has {ArenaLetters.MinContestants} to {ArenaLetters.MaxContestants} contestants.");
        if (contestants.Any(contestant => contestant is null)) throw new ArgumentException("A contestant is empty.");
        contestants = [.. contestants.Select(contestant => contestant with
        {
            Identity = (contestant.Identity ?? throw new ArgumentException("A contestant needs its model.")).Normalize(),
            RunId = string.IsNullOrWhiteSpace(contestant.RunId) ? null : contestant.RunId.Trim(),
        }).OrderBy(contestant => contestant.Position)];
        for (var index = 0; index < contestants.Count; index++)
            if (contestants[index].Position != index) throw new ArgumentException($"A battle of {contestants.Count} uses columns A to {ArenaLetters.Of(contestants.Count - 1)}, each once.");
        if (contestants.Select(contestant => contestant.Identity.Key).Distinct(StringComparer.Ordinal).Count() != contestants.Count)
            throw new ArgumentException("Two contestants are the same model with the same settings; a vote between them would rate nothing.");
        if (contestants.Where(contestant => contestant.RunId is not null).GroupBy(contestant => contestant.RunId).Any(group => group.Count() > 1))
            throw new ArgumentException("Two contestants share a run.");
        return this with
        {
            Id = id,
            Tag = string.IsNullOrWhiteSpace(Tag) ? null : EvalTags.Normalize(Tag),
            Contestants = contestants,
            GroupId = string.IsNullOrWhiteSpace(GroupId) ? null : GroupId.Trim(),
        };
    }

    /// <summary>The contestant in column <paramref name="letter"/>, or null.</summary>
    public ArenaContestant? At(string? letter)
    {
        var position = ArenaLetters.PositionOf(letter);
        return Contestants.FirstOrDefault(contestant => contestant.Position == position && position >= 0);
    }

    /// <summary>What a page may see before the vote (<see cref="ArenaBlindBattle"/>).</summary>
    public ArenaBlindBattle ToBlind() => new(Id, Mode, Status, Letters, StreamAnyway, Tag);

    /// <summary>The pairs <paramref name="vote"/> decides, left column first: a pick beats each other column; a tie and all bad make every
    /// pair a tie; a ranking makes each column beat every one ranked below it. Pairs come in column order (A–B, A–C, ..., C–D), so the result
    /// does not depend on how the vote was written.</summary>
    /// <exception cref="ArgumentException">The vote is not for this battle, or does not fit it.</exception>
    public IReadOnlyList<ArenaComparison> Comparisons(ArenaVote vote)
    {
        ArgumentNullException.ThrowIfNull(vote);
        var battle = Normalize();
        var normalized = vote.Normalize(battle.Contestants.Count);
        if (normalized.BattleId != battle.Id) throw new ArgumentException("The vote is for another battle.");
        if (normalized.Mode != battle.Mode) throw new ArgumentException("The vote's mode is not the battle's.");
        // Rank of each column: 0 for the best. A pick ranks its column first and every other column second (equal); a tie ranks all equal.
        var rank = new int[battle.Contestants.Count];
        switch (normalized.Kind)
        {
            case VoteKind.Pick:
                var picked = ArenaLetters.PositionOf(normalized.Pick);
                for (var position = 0; position < rank.Length; position++) rank[position] = position == picked ? 0 : 1;
                break;
            case VoteKind.Rank:
                for (var place = 0; place < normalized.Ranking!.Count; place++) rank[ArenaLetters.PositionOf(normalized.Ranking[place])] = place;
                break;
        }
        var pairs = new List<ArenaComparison>();
        for (var left = 0; left < rank.Length; left++)
        {
            for (var right = left + 1; right < rank.Length; right++)
            {
                // A pick says nothing about two columns it did not choose.
                if (normalized.Kind == VoteKind.Pick && rank[left] == 1 && rank[right] == 1) continue;
                var score = rank[left] < rank[right] ? 1.0 : rank[left] > rank[right] ? 0.0 : 0.5;
                pairs.Add(new ArenaComparison(battle.Id, battle.Mode, normalized.Tag, normalized.Kind, rank.Length,
                    battle.Contestants[left].Identity, battle.Contestants[right].Identity, left, right, score));
            }
        }
        return pairs;
    }
}

/// <summary>The one vote of a battle.</summary>
public sealed record ArenaVote
{
    /// <summary>The battle (<see cref="EvalIds.Battle"/>).</summary>
    public required string BattleId { get; init; }

    /// <summary>Request or agent: the battle's.</summary>
    public ArenaMode Mode { get; init; } = ArenaMode.Request;

    /// <summary>The leaderboard it counts toward (<see cref="EvalTags"/>).</summary>
    public required string Tag { get; init; }

    /// <summary>Pick, tie, all bad or rank.</summary>
    public VoteKind Kind { get; init; }

    /// <summary>The picked column's letter (<see cref="VoteKind.Pick"/> only).</summary>
    public string? Pick { get; init; }

    /// <summary>Every column's letter, best first (<see cref="VoteKind.Rank"/> only).</summary>
    public IReadOnlyList<string>? Ranking { get; init; }

    /// <summary>When it was cast.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>The column the user liked best: the pick, or the ranking's first; null for a tie or all bad.</summary>
    [JsonIgnore]
    public string? Best => Kind switch { VoteKind.Pick => Pick, VoteKind.Rank => Ranking?.FirstOrDefault(), _ => null };

    /// <summary>The same vote checked against a battle of <paramref name="contestants"/> columns: letters upper case, the tag normalized, the
    /// fields its kind does not use cleared.</summary>
    /// <exception cref="ArgumentException">Something is not allowed; the message says what, for the page to show.</exception>
    public ArenaVote Normalize(int contestants)
    {
        if (contestants is < ArenaLetters.MinContestants or > ArenaLetters.MaxContestants)
            throw new ArgumentException($"A battle has {ArenaLetters.MinContestants} to {ArenaLetters.MaxContestants} contestants.");
        var battle = EvalIds.Require(EvalIds.Battle, BattleId, "battle");
        if (!Enum.IsDefined(Mode)) throw new ArgumentException($"Unknown arena mode {(int)Mode}.");
        var tag = EvalTags.Normalize(Tag);
        string? pick = null;
        IReadOnlyList<string>? ranking = null;
        switch (Kind)
        {
            case VoteKind.Pick:
                var position = ArenaLetters.PositionOf(Pick);
                if (position < 0 || position >= contestants) throw new ArgumentException($"Pick one of A to {ArenaLetters.Of(contestants - 1)}.");
                pick = ArenaLetters.Of(position);
                break;
            case VoteKind.Tie or VoteKind.AllBad:
                break;
            case VoteKind.Rank:
                if (contestants < 3) throw new ArgumentException("Ranking needs 3 or more answers; pick one instead.");
                var places = (Ranking ?? []).Select(ArenaLetters.PositionOf).ToList();
                if (places.Count != contestants || places.Any(place => place < 0 || place >= contestants) || places.Distinct().Count() != contestants)
                    throw new ArgumentException($"Rank every answer, A to {ArenaLetters.Of(contestants - 1)}, each once.");
                ranking = [.. places.Select(ArenaLetters.Of)];
                break;
            default:
                throw new ArgumentException($"Unknown vote kind {(int)Kind}.");
        }
        return this with { BattleId = battle, Tag = tag, Pick = pick, Ranking = ranking };
    }
}
