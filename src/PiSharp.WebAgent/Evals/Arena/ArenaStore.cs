// Decision 0011 (run W5, E5): the Arena's battles and votes in webagent.db (E0's eval_battles and eval_votes, migration 2000) and, in the
// arena block of the evals range (2500-2599), what each battle asked (eval_arena_rounds, migration 2500): the Compare draft of a request
// battle, or the agent compare create request of an agent battle, so a battle refused by the Spend Guard can be started again with the
// user's answer, its page can show the shared prompt or task, and New round can ask the same again with a new shuffle. Neither holds a
// secret: a draft names models and account ids, never a key. A battle row keeps the whole ArenaBattle as JSON (its contestants, their
// identities and runs, the seed) beside the columns E0's constraints and indexes use; a vote row the whole ArenaVote. One vote per battle is
// the table's primary key, so a second vote cannot be stored even by two requests at once. Votes are read back in the order they were cast
// (and by id), but nothing computed from them depends on that order (the rater sorts its comparisons).
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Evals.Arena;

/// <summary>What a battle asked: a request battle's Compare draft (overrides cleared: blind battles use the shared settings), or an agent
/// battle's create request (as the page sent it, without its request id).</summary>
public sealed record ArenaRound
{
    /// <summary>The shared prompt and the models, in the order the user chose them (request mode).</summary>
    public CompareDraft? Draft { get; init; }

    /// <summary>The agent compare create request, models in the order the user chose them (agent mode).</summary>
    public JsonObject? Agent { get; init; }
}

/// <summary>Battles, votes and rounds in webagent.db.</summary>
public sealed class ArenaStore
{
    /// <summary>2500: <c>eval_arena_rounds</c>, what each battle asked (one row per battle, deleted with it).</summary>
    public static SqlMigration Rounds { get; } = new(2500, "evals: arena rounds", """
        CREATE TABLE IF NOT EXISTS eval_arena_rounds (
          battle_id TEXT PRIMARY KEY REFERENCES eval_battles (id) ON DELETE CASCADE,
          mode TEXT NOT NULL CHECK (mode IN ('request', 'agent')),
          data TEXT NOT NULL
        );
        """);

    private readonly WebAgentDb db;
    private readonly Lock gate = new();

    /// <summary>A store over <paramref name="db"/> (migrated with <see cref="EvalMigrations.Arena"/> and <see cref="Rounds"/>).</summary>
    public ArenaStore(WebAgentDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
    }

    /// <summary>Adds a new battle and what it asked.</summary>
    /// <exception cref="ArgumentException">The battle is not valid (<see cref="ArenaBattle.Normalize"/>).</exception>
    public ArenaBattle Add(ArenaBattle battle, ArenaRound round)
    {
        ArgumentNullException.ThrowIfNull(battle);
        ArgumentNullException.ThrowIfNull(round);
        var normalized = battle.Normalize();
        lock (gate)
        {
            db.Transaction((connection, transaction) =>
            {
                WebAgentDb.Execute(connection, transaction, """
                    INSERT INTO eval_battles (id, mode, tag, status, seed, group_id, data, created_at, voted_at)
                    VALUES ($id, $mode, $tag, $status, $seed, $group, $data, $created, $voted);
                    """, Columns(normalized));
                WebAgentDb.Execute(connection, transaction, "INSERT INTO eval_arena_rounds (battle_id, mode, data) VALUES ($id, $mode, $data);",
                    ("$id", normalized.Id), ("$mode", EvalJson.Wire(normalized.Mode)), ("$data", EvalJson.Serialize(round)));
                return 0;
            });
        }
        return normalized;
    }

    /// <summary>The battle <paramref name="id"/>, or null.</summary>
    public ArenaBattle? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return db.Query("SELECT data FROM eval_battles WHERE id = $id;", reader => EvalJson.TryDeserialize<ArenaBattle>(reader.GetString(0)), ("$id", id))
            .FirstOrDefault();
    }

    /// <summary>What battle <paramref name="id"/> asked, or null.</summary>
    public ArenaRound? Round(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return db.Query("SELECT data FROM eval_arena_rounds WHERE battle_id = $id;", reader => EvalJson.TryDeserialize<ArenaRound>(reader.GetString(0)), ("$id", id))
            .FirstOrDefault();
    }

    /// <summary>The battles whose answers belong to run group (or agent compare run) <paramref name="groupId"/>, newest first.</summary>
    public IReadOnlyList<ArenaBattle> ByGroup(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        return [.. db.Query("SELECT data FROM eval_battles WHERE group_id = $group ORDER BY created_at DESC, id;",
            reader => EvalJson.TryDeserialize<ArenaBattle>(reader.GetString(0)), ("$group", groupId)).OfType<ArenaBattle>()];
    }

    /// <summary>The battles in <paramref name="status"/>, oldest first.</summary>
    public IReadOnlyList<ArenaBattle> WithStatus(ArenaStatus status) =>
        [.. db.Query("SELECT data FROM eval_battles WHERE status = $status ORDER BY created_at, id;",
            reader => EvalJson.TryDeserialize<ArenaBattle>(reader.GetString(0)), ("$status", EvalJson.Wire(status))).OfType<ArenaBattle>()];

    /// <summary>Changes battle <paramref name="id"/> under the store's lock: <paramref name="change"/> sees the stored battle and returns the
    /// new one (the same instance to leave it). A voted battle is never changed back.</summary>
    /// <returns>The stored battle after the change, or null when there is none.</returns>
    public ArenaBattle? Update(string id, Func<ArenaBattle, ArenaBattle> change)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            if (Get(id) is not { } current) return null;
            var next = change(current);
            if (ReferenceEquals(next, current)) return current;
            if (current.Status == ArenaStatus.Voted && next.Status != ArenaStatus.Voted) throw new InvalidOperationException("A voted battle stays voted.");
            next = next.Normalize();
            if (next.Id != current.Id) throw new InvalidOperationException("A battle keeps its id.");
            db.Execute("""
                UPDATE eval_battles SET mode = $mode, tag = $tag, status = $status, seed = $seed, group_id = $group, data = $data, created_at = $created,
                  voted_at = $voted WHERE id = $id;
                """, Columns(next));
            return next;
        }
    }

    /// <summary>Saves the one vote of a battle that is ready and marks it voted, with the vote's tag, in one transaction. The same vote
    /// again (a repeated request) answers the stored one.</summary>
    /// <returns>The battle and its vote, and whether this call saved it.</returns>
    /// <exception cref="KeyNotFoundException">There is no such battle.</exception>
    /// <exception cref="InvalidOperationException">The battle is not ready, or another vote was saved.</exception>
    /// <exception cref="ArgumentException">The vote does not fit the battle.</exception>
    public (ArenaBattle Battle, ArenaVote Vote, bool Saved) Vote(ArenaVote vote)
    {
        ArgumentNullException.ThrowIfNull(vote);
        lock (gate)
        {
            var battle = Get(vote.BattleId) ?? throw new KeyNotFoundException($"There is no battle {vote.BattleId}.");
            var normalized = vote.Normalize(battle.Contestants.Count);
            if (normalized.Mode != battle.Mode) throw new ArgumentException("The vote's mode is not the battle's.");
            if (VoteOf(battle.Id) is { } stored)
            {
                if (Same(stored, normalized)) return (battle, stored, false);
                throw new InvalidOperationException("This battle already has a vote.");
            }
            if (battle.Status != ArenaStatus.Ready)
                throw new InvalidOperationException(battle.Status == ArenaStatus.Held ? "Voting opens when every answer is in." : "This battle can't be voted on.");
            _ = battle.Comparisons(normalized);
            var voted = battle with { Status = ArenaStatus.Voted, Tag = normalized.Tag, Voted = normalized.Created };
            db.Transaction((connection, transaction) =>
            {
                WebAgentDb.Execute(connection, transaction, """
                    INSERT INTO eval_votes (battle_id, mode, tag, kind, data, created_at) VALUES ($id, $mode, $tag, $kind, $data, $created);
                    """, ("$id", normalized.BattleId), ("$mode", EvalJson.Wire(normalized.Mode)), ("$tag", normalized.Tag),
                    ("$kind", EvalJson.Wire(normalized.Kind)), ("$data", EvalJson.Serialize(normalized)), ("$created", EvalJson.ToColumn(normalized.Created)));
                WebAgentDb.Execute(connection, transaction, """
                    UPDATE eval_battles SET tag = $tag, status = $status, data = $data, voted_at = $voted WHERE id = $id;
                    """, ("$id", voted.Id), ("$tag", voted.Tag), ("$status", EvalJson.Wire(voted.Status)), ("$data", EvalJson.Serialize(voted)),
                    ("$voted", EvalJson.ToColumn(normalized.Created)));
                return 0;
            });
            return (voted, normalized, true);
        }
    }

    /// <summary>The vote of battle <paramref name="id"/>, or null.</summary>
    public ArenaVote? VoteOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return db.Query("SELECT data FROM eval_votes WHERE battle_id = $id;", reader => EvalJson.TryDeserialize<ArenaVote>(reader.GetString(0)), ("$id", id))
            .FirstOrDefault();
    }

    /// <summary>Every voted battle with its vote (of <paramref name="mode"/>, or both), in the order they were cast: what the rater and the
    /// leaderboards read (<see cref="VotedBattle"/>).</summary>
    public IReadOnlyList<VotedBattle> Voted(ArenaMode? mode = null)
    {
        var sql = """
            SELECT b.data, v.data FROM eval_votes v JOIN eval_battles b ON b.id = v.battle_id
            WHERE b.status = 'voted' AND ($mode IS NULL OR v.mode = $mode) ORDER BY v.created_at, v.battle_id;
            """;
        return [.. db.Query(sql, reader =>
        {
            var battle = EvalJson.TryDeserialize<ArenaBattle>(reader.GetString(0));
            var vote = EvalJson.TryDeserialize<ArenaVote>(reader.GetString(1));
            return battle is null || vote is null ? null : new VotedBattle(battle, vote);
        }, ("$mode", mode is { } value ? EvalJson.Wire(value) : null)).OfType<VotedBattle>()];
    }

    /// <summary>The tags votes of <paramref name="mode"/> used, the most recently used first.</summary>
    public IReadOnlyList<string> Tags(ArenaMode mode) =>
        db.Query("SELECT tag FROM eval_votes WHERE mode = $mode GROUP BY tag ORDER BY MAX(created_at) DESC, tag;", reader => reader.GetString(0),
            ("$mode", EvalJson.Wire(mode)));

    /// <summary>Deletes battle <paramref name="id"/> (its vote and round go with it).</summary>
    public bool Delete(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (gate) return db.Execute("DELETE FROM eval_battles WHERE id = $id;", ("$id", id)) > 0;
    }

    private static bool Same(ArenaVote left, ArenaVote right) =>
        left.Kind == right.Kind && left.Tag == right.Tag && left.Pick == right.Pick
        && (left.Ranking ?? []).SequenceEqual(right.Ranking ?? [], StringComparer.Ordinal);

    private static (string Name, object? Value)[] Columns(ArenaBattle battle) =>
    [
        ("$id", battle.Id),
        ("$mode", EvalJson.Wire(battle.Mode)),
        ("$tag", battle.Tag),
        ("$status", EvalJson.Wire(battle.Status)),
        ("$seed", battle.Seed),
        ("$group", battle.GroupId),
        ("$data", EvalJson.Serialize(battle)),
        ("$created", EvalJson.ToColumn(battle.Created)),
        ("$voted", battle.Voted is { } voted ? EvalJson.ToColumn(voted) : null),
    ];
}

