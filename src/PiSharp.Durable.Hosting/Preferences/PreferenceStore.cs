// PiSharp-only (decision 0010, M5): explicit feedback and the device-local preference profiles it builds. Feedback events are append-only
// and idempotent by (profile, request ID); an undo, a reset, a forgotten example and a settings change are events too, so a profile's weights
// and examples are a deterministic fold of its events and come out the same on every read, replay or restart. A profile is one device's
// named preferences, not a person's; nothing here trains a model or writes anywhere else.
using System.Globalization;
using Microsoft.Data.Sqlite;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>A feedback request after the host resolved its target and actor.</summary>
/// <param name="ProfileId">The profile it changes: <c>^[a-z0-9][a-z0-9_-]{0,39}$</c>.</param>
/// <param name="RequestId">The caller's request ID (1–200 characters); a repeat returns the first event.</param>
/// <param name="Action"><c>more</c>, <c>less</c>, <c>label</c>, <c>dismiss</c> or <c>undo</c>.</param>
public sealed record FeedbackRequest(string ProfileId, string RequestId, string Action)
{
    /// <summary>The target's session, resolved by the host.</summary>
    public required string SessionId { get; init; }
    /// <summary>The target artifact, resolved by the host.</summary>
    public required string ArtifactId { get; init; }
    /// <summary>The target's title, kept with an example.</summary>
    public string? Title { get; init; }
    /// <summary>The labels or topics the feedback is about (at most 10).</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];
    /// <summary>A note of at most 1000 characters; it is data, never an instruction.</summary>
    public string? Note { get; init; }
    /// <summary>Whether the person chose this target as an example for judgments that use the profile.</summary>
    public bool Example { get; init; }
    /// <summary>For <c>undo</c>: the event it reverses; null reverses the newest effective event on the same target.</summary>
    public string? Reverses { get; init; }
    /// <summary>Who gave it: the authenticated credential, never a body field.</summary>
    public string Actor { get; init; } = "device-admin";
}

/// <summary>How a profile weighs feedback.</summary>
/// <param name="More">What a <c>more</c> adds to each of its labels (0–1).</param>
/// <param name="Less">What a <c>less</c> takes from each of its labels (0–1).</param>
/// <param name="Label">What a <c>label</c> adds to each label it names (0–1).</param>
/// <param name="MaxExamples">How many chosen examples judgments see (0–20).</param>
public sealed record PreferenceSettings(double More = 0.10, double Less = 0.15, double Label = 0.10, int MaxExamples = 20)
{
    /// <summary>The settings as JSON.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object([new("more", More), new("less", Less), new("label", Label), new("maxExamples", MaxExamples)]);

    /// <summary>These settings with <paramref name="changes"/> applied; a field may be left out.</summary>
    /// <exception cref="HostValidationException">A field is unknown or out of range.</exception>
    public PreferenceSettings With(JsonObjectValue changes)
    {
        var errors = new List<FieldError>();
        foreach (var key in changes.Keys.Where(key => key is not ("more" or "less" or "label" or "maxExamples")))
            errors.Add(new FieldError("/settings/" + key, "is not a preference setting"));
        double Weight(string key, double current)
        {
            if (!changes.TryGetValue(key, out var value)) return current;
            if (value is JsonNumberValue number && double.IsFinite(number.Value) && number.Value is >= 0 and <= 1) return number.Value;
            errors.Add(new FieldError("/settings/" + key, "must be a number from 0 to 1"));
            return current;
        }
        var more = Weight("more", More);
        var less = Weight("less", Less);
        var label = Weight("label", Label);
        var examples = MaxExamples;
        if (changes.TryGetValue("maxExamples", out var max))
        {
            if (max is JsonNumberValue count && count.Value == Math.Floor(count.Value) && count.Value is >= 0 and <= PreferenceStore.MaxExamples) examples = (int)count.Value;
            else errors.Add(new FieldError("/settings/maxExamples", $"must be an integer from 0 to {PreferenceStore.MaxExamples}"));
        }
        if (errors.Count > 0) throw HostValidationException.Of("Invalid preference settings", errors);
        return new PreferenceSettings(more, less, label, examples);
    }

    internal static PreferenceSettings Of(JsonObjectValue? json) => json is null ? new PreferenceSettings() : new PreferenceSettings().With(json);
}

/// <summary>Feedback events and the preference profiles they build.</summary>
public sealed class PreferenceStore
{
    /// <summary>The profile feedback goes to when none is named.</summary>
    public const string DefaultProfile = "default";
    /// <summary>The most labels one event names.</summary>
    public const int MaxLabels = 10;
    /// <summary>The most examples a profile keeps.</summary>
    public const int MaxExamples = 20;
    /// <summary>The most events one profile keeps; a reset does not free them, a delete does.</summary>
    public const int MaxEvents = 10_000;

    private readonly SessionCatalog catalog;
    private readonly TimeProvider time;

    internal PreferenceStore(SessionCatalog catalog, TimeProvider time)
    {
        this.catalog = catalog;
        this.time = time;
    }

    /// <summary>A label ID: lower case letters, digits and <c>_ - . :</c>, at most 40 characters.</summary>
    public static bool IsLabel(string label) => label.Length is >= 1 and <= 40 && label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.' or ':');

    /// <summary>
    /// Records feedback and returns the event and the profile after it. The same request ID with the same request returns the first event
    /// (<c>Replayed</c>); with a different request it conflicts. An undo appends a reversal of an event on the same target in the same
    /// profile; reversing one twice conflicts.
    /// </summary>
    /// <exception cref="HostValidationException">The request is invalid.</exception>
    /// <exception cref="HostConflictException">The request ID was used differently, or the undo has nothing to reverse.</exception>
    /// <exception cref="HostLimitException">The profile has <see cref="MaxEvents"/> events.</exception>
    public (JsonObjectValue Event, JsonObjectValue Profile, bool Replayed) Record(FeedbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var labels = Check(request);
        var requestJson = JsonValue.Object(
        [
            new("profileId", request.ProfileId), new("requestId", request.RequestId), new("action", request.Action),
            new("target", Target(request.SessionId, request.ArtifactId)), new("labels", JsonValue.Array(labels.Select(label => (JsonValue)label))),
            new("note", request.Note is null ? JsonValue.Null : request.Note), new("example", request.Example),
            new("reverses", request.Reverses is null ? JsonValue.Null : request.Reverses),
        ]);
        var hash = CanonicalJson.Hash(requestJson);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return catalog.Transact(transaction =>
        {
            var replay = catalog.Rows(transaction, "SELECT request_hash, event FROM feedback_events WHERE profile_id = $profile AND request_id = $request;",
                reader => (Hash: reader.GetString(0), Event: reader.GetString(1)), ("$profile", request.ProfileId), ("$request", request.RequestId)).FirstOrDefault();
            if (replay.Event is not null)
            {
                if (replay.Hash != hash) throw new HostConflictException($"Feedback request {request.RequestId} was already used for different feedback.");
                return ((JsonObjectValue)JsonValue.Parse(replay.Event), Project(transaction, request.ProfileId), true);
            }
            var events = Events(transaction, request.ProfileId);
            if (events.Count >= MaxEvents) throw new HostLimitException($"Profile {request.ProfileId} has {MaxEvents} feedback events; delete it to start again.");
            string? reverses = null;
            if (request.Action == "undo")
            {
                var current = events.Skip(LastReset(events) + 1).ToList();
                var undone = current.Where(item => Jobs.String(item, "action") == "undo").Select(item => Jobs.String(item, "reverses")).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var key = Key(request.SessionId, request.ArtifactId);
                bool Reversible(JsonObjectValue item) => Jobs.String(item, "action") is "more" or "less" or "label" or "dismiss";
                JsonObjectValue? target;
                if (request.Reverses is { } named)
                {
                    target = events.FirstOrDefault(item => Jobs.String(item, "id") == named)
                        ?? throw new HostNotFoundException($"Profile {request.ProfileId} has no feedback event {named}.");
                    if (!Reversible(target)) throw new HostConflictException($"Feedback event {named} cannot be undone.");
                    if (TargetKey(target) != key) throw new HostConflictException($"Feedback event {named} is about another artifact.");
                    if (!current.Contains(target)) throw new HostConflictException($"Feedback event {named} is from before the profile was reset.");
                    if (undone.Contains(named)) throw new HostConflictException($"Feedback event {named} is already undone.");
                }
                else
                {
                    target = current.LastOrDefault(item => Reversible(item) && TargetKey(item) == key && !undone.Contains(Jobs.String(item, "id")!))
                        ?? throw new HostConflictException("There is no feedback on this artifact to undo.");
                }
                reverses = Jobs.String(target, "id");
            }
            var revision = Revision(transaction, request.ProfileId, now) + 1;
            var feedback = JsonValue.Object(
            [
                new("id", CanonicalJson.Id("fb_", request.ProfileId, request.RequestId)),
                new("profileId", request.ProfileId),
                new("requestId", request.RequestId),
                new("target", Target(request.SessionId, request.ArtifactId)),
                new("title", request.Title is { } title ? Bound(title, 200) : JsonValue.Null),
                new("action", request.Action),
                new("labels", JsonValue.Array(labels.Select(label => (JsonValue)label))),
                new("note", request.Note is null ? JsonValue.Null : request.Note),
                new("example", request.Example),
                new("actor", request.Actor),
                new("at", now),
                new("reverses", reverses is null ? JsonValue.Null : reverses),
                new("revision", revision),
            ]);
            Append(transaction, feedback, hash, revision, now);
            return (feedback, Project(transaction, request.ProfileId), false);
        });
    }

    /// <summary>A profile's projection: <c>{id, revision, settings, weights, examples, events, targets, createdAt, updatedAt}</c>. A
    /// profile nobody wrote is empty at revision 0.</summary>
    /// <exception cref="HostInvalidException">The ID is not a profile ID.</exception>
    public JsonObjectValue Get(string profileId)
    {
        CheckId(profileId);
        return catalog.Read(_ => Project(null, profileId));
    }

    /// <summary>
    /// A profile's current feedback on one artifact: <c>{profileId, revision, target, vote, labels, example, undo}</c>. <c>vote</c> is the
    /// effective more, less or dismiss; <c>undo</c> is the event an undo would reverse, or null.
    /// </summary>
    /// <exception cref="HostInvalidException">The ID is not a profile ID.</exception>
    public JsonObjectValue Feedback(string profileId, string sessionId, string artifactId)
    {
        CheckId(profileId);
        return catalog.Read(_ =>
        {
            var events = Events(null, profileId);
            var revision = catalog.Value(null, "SELECT revision FROM preference_profiles WHERE id = $id;", ("$id", profileId)) is long stored ? stored : 0;
            var current = events.Skip(LastReset(events) + 1).ToList();
            var undone = current.Where(item => Jobs.String(item, "action") == "undo").Select(item => Jobs.String(item, "reverses")).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var forgotten = current.Where(item => Jobs.String(item, "action") == "forget").Select(item => Jobs.String(item, "reverses")).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var key = Key(sessionId, artifactId);
            string? vote = null, undo = null;
            var labels = new SortedSet<string>(StringComparer.Ordinal);
            var example = false;
            foreach (var item in current.Where(item => TargetKey(item) == key && !undone.Contains(Jobs.String(item, "id")!)))
            {
                var action = Jobs.String(item, "action");
                if (action is not ("more" or "less" or "label" or "dismiss")) continue;
                if (action == "label") labels.UnionWith(Jobs.Items(item, "labels").OfType<JsonStringValue>().Select(label => label.Value));
                else vote = action;
                if (item.TryGetValue("example", out var chosen) && chosen is JsonBooleanValue { Value: true } && !forgotten.Contains(Jobs.String(item, "id")!)) example = true;
                undo = Jobs.String(item, "id");
            }
            return JsonValue.Object(
            [
                new("profileId", profileId), new("revision", revision), new("target", Target(sessionId, artifactId)),
                new("vote", vote is null ? JsonValue.Null : vote), new("labels", JsonValue.Array(labels.Select(label => (JsonValue)label))),
                new("example", example), new("undo", undo is null ? JsonValue.Null : undo),
            ]);
        });
    }

    /// <summary>Every written profile, and <see cref="DefaultProfile"/>, by ID.</summary>
    public IReadOnlyList<JsonObjectValue> List() => catalog.Read(_ =>
    {
        var ids = catalog.Rows(null, "SELECT id FROM preference_profiles ORDER BY id;", reader => reader.GetString(0));
        if (!ids.Contains(DefaultProfile, StringComparer.Ordinal)) ids.Insert(0, DefaultProfile);
        return ids.Order(StringComparer.Ordinal).Select(id => Project(null, id)).ToList();
    });

    /// <summary>
    /// Changes a profile's settings and forgets chosen examples (by the event IDs that chose them), each as an event. A forgotten example's
    /// note is removed from its event too.
    /// </summary>
    /// <exception cref="HostConflictException">The profile is not at <paramref name="expectedRevision"/>.</exception>
    public JsonObjectValue Update(string profileId, JsonObjectValue? settings, IReadOnlyList<string> forgetExamples, long expectedRevision, string actor = "device-admin")
    {
        CheckId(profileId);
        ArgumentNullException.ThrowIfNull(forgetExamples);
        if (settings is null && forgetExamples.Count == 0) throw new HostInvalidException("Change settings or forget at least one example.");
        if (forgetExamples.Count > MaxExamples) throw new HostInvalidException($"Forget at most {MaxExamples} examples at once.");
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return catalog.Transact(transaction =>
        {
            var events = Events(transaction, profileId);
            var current = Expect(transaction, profileId, expectedRevision);
            var revision = current;
            if (settings is not null)
            {
                var changed = SettingsOf(events).With(settings);
                revision = Revision(transaction, profileId, now) + 1;
                var change = Internal(profileId, "settings", revision, actor, now).SetItem("settings", changed.ToJson());
                Append(transaction, change, CanonicalJson.Hash(change), revision, now);
                catalog.Run(transaction, "UPDATE preference_profiles SET settings = $settings WHERE id = $id;",
                    ("$id", profileId), ("$settings", CanonicalJson.Serialize(changed.ToJson())));
                events.Add(change);
            }
            var examples = ((JsonArrayValue)Project(events, profileId, revision, 0, 0)["examples"]).Items.OfType<JsonObjectValue>()
                .Select(item => Jobs.String(item, "eventId")).ToHashSet(StringComparer.Ordinal);
            foreach (var eventId in forgetExamples.Distinct(StringComparer.Ordinal))
            {
                if (!examples.Contains(eventId)) throw new HostNotFoundException($"Profile {profileId} has no example chosen by {eventId}.");
                revision = Revision(transaction, profileId, now) + 1;
                var forget = Internal(profileId, "forget", revision, actor, now).SetItem("reverses", eventId);
                Append(transaction, forget, CanonicalJson.Hash(forget), revision, now);
                Scrub(transaction, eventId);
            }
            return Project(transaction, profileId);
        });
    }

    /// <summary>Starts the profile's weights and examples over; its settings and its event history stay.</summary>
    /// <exception cref="HostConflictException">The profile is not at <paramref name="expectedRevision"/>.</exception>
    public JsonObjectValue Reset(string profileId, long expectedRevision, string actor = "device-admin")
    {
        CheckId(profileId);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return catalog.Transact(transaction =>
        {
            Expect(transaction, profileId, expectedRevision);
            var revision = Revision(transaction, profileId, now) + 1;
            var reset = Internal(profileId, "reset", revision, actor, now);
            Append(transaction, reset, CanonicalJson.Hash(reset), revision, now);
            return Project(transaction, profileId);
        });
    }

    /// <summary>The profile and every event it keeps, oldest first.</summary>
    public JsonObjectValue Export(string profileId)
    {
        CheckId(profileId);
        return catalog.Read(_ => JsonValue.Object([new("profile", Project(null, profileId)),
            new("events", JsonValue.Array(Events(null, profileId).Select(item => (JsonValue)item))),
            new("exportedAt", time.GetUtcNow().ToUnixTimeMilliseconds())]));
    }

    /// <summary>Deletes the profile and all its events; false when it had none.</summary>
    /// <exception cref="HostConflictException">The profile is not at <paramref name="expectedRevision"/>.</exception>
    public bool Delete(string profileId, long expectedRevision)
    {
        CheckId(profileId);
        return catalog.Transact(transaction =>
        {
            Expect(transaction, profileId, expectedRevision);
            var removed = Convert.ToInt64(catalog.Value(transaction, "SELECT COUNT(*) FROM preference_profiles WHERE id = $id;", ("$id", profileId)), CultureInfo.InvariantCulture) > 0;
            catalog.Run(transaction, "DELETE FROM feedback_events WHERE profile_id = $id;", ("$id", profileId));
            catalog.Run(transaction, "DELETE FROM preference_profiles WHERE id = $id;", ("$id", profileId));
            return removed;
        });
    }

    /// <summary>What a judgment freezes of a profile: <c>{profileId, revision, weights, examples}</c>, examples as
    /// <c>{title, action, labels, note}</c>.</summary>
    public JsonObjectValue Snapshot(string profileId)
    {
        var profile = Get(profileId);
        return JsonValue.Object([new("profileId", profileId), new("revision", profile["revision"]), new("weights", profile["weights"]),
            new("examples", JsonValue.Array(Jobs.Items(profile, "examples").OfType<JsonObjectValue>().Select(item => (JsonValue)JsonValue.Object(
                [new("title", item["title"]), new("action", item["action"]), new("labels", item["labels"]), new("note", item["note"])]))))]);
    }

    /// <summary>
    /// How a frozen <paramref name="snapshot"/> ranks a judged item: the base is the policy's weighted score (0.5 without scores), plus the
    /// weights of the judged label and the item's own labels, clamped to [0, 1]. Policy confidence and routing are never changed by it.
    /// </summary>
    public static JsonObjectValue Rank(JsonObjectValue snapshot, string label, double? score, IEnumerable<string>? itemLabels = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var weights = snapshot.TryGetValue("weights", out var value) && value is JsonObjectValue given ? given : JsonObjectValue.Empty;
        var reasons = new List<JsonValue>();
        double adjustment = 0;
        foreach (var name in new[] { label }.Concat(itemLabels ?? []).Distinct(StringComparer.Ordinal).Take(MaxLabels + 1))
            if (weights.TryGetValue(name, out var weight) && weight is JsonNumberValue number && number.Value != 0)
            {
                adjustment += number.Value;
                reasons.Add(JsonValue.Object([new("label", name), new("weight", number.Value)]));
            }
        var baseline = score ?? 0.5;
        return JsonValue.Object([new("profileId", snapshot["profileId"]), new("revision", snapshot["revision"]), new("base", Round(baseline)),
            new("adjustment", Round(adjustment)), new("rank", Round(Math.Clamp(baseline + adjustment, 0, 1))), new("reasons", JsonValue.Array(reasons))]);
    }

    // ─── Projection ─────────────────────────────────────────────────────────

    private JsonObjectValue Project(SqliteTransaction? transaction, string profileId)
    {
        var row = catalog.Rows(transaction, "SELECT revision, created_at, updated_at FROM preference_profiles WHERE id = $id;",
            reader => (Revision: reader.GetInt64(0), Created: reader.GetInt64(1), Updated: reader.GetInt64(2)), ("$id", profileId)).FirstOrDefault();
        return Project(Events(transaction, profileId), profileId, row.Revision, row.Created, row.Updated);
    }

    /// <summary>The fold: after the last reset, each artifact keeps its latest <c>more</c>, <c>less</c> or <c>dismiss</c> and the union of
    /// its labels, so repeating feedback counts once; undone events and forgotten examples drop out.</summary>
    private static JsonObjectValue Project(IReadOnlyList<JsonObjectValue> events, string profileId, long revision, long createdAt, long updatedAt)
    {
        var settings = SettingsOf(events);
        var current = events.Skip(LastReset(events) + 1).ToList();
        var undone = current.Where(item => Jobs.String(item, "action") == "undo").Select(item => Jobs.String(item, "reverses")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var forgotten = current.Where(item => Jobs.String(item, "action") == "forget").Select(item => Jobs.String(item, "reverses")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var targets = new Dictionary<string, (string? Vote, List<string> VoteLabels, SortedSet<string> Labels, JsonObjectValue? Example)>(StringComparer.Ordinal);
        var order = new List<string>();
        var effective = 0;
        foreach (var item in current)
        {
            var action = Jobs.String(item, "action");
            if (action is not ("more" or "less" or "label" or "dismiss") || undone.Contains(Jobs.String(item, "id")!)) continue;
            effective++;
            var key = TargetKey(item);
            if (!targets.TryGetValue(key, out var state))
            {
                state = (null, [], new SortedSet<string>(StringComparer.Ordinal), null);
                order.Add(key);
            }
            var labels = Jobs.Items(item, "labels").OfType<JsonStringValue>().Select(label => label.Value).ToList();
            if (action == "label") state.Labels.UnionWith(labels);
            else state = (action, labels, state.Labels, state.Example);
            if (item.TryGetValue("example", out var chosen) && chosen is JsonBooleanValue { Value: true } && !forgotten.Contains(Jobs.String(item, "id")!))
                state = (state.Vote, state.VoteLabels, state.Labels, item);
            targets[key] = state;
        }
        var weights = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            var state = targets[key];
            var delta = state.Vote switch { "more" => settings.More, "less" => -settings.Less, _ => 0 };
            if (delta != 0) foreach (var label in state.VoteLabels) weights[label] = weights.GetValueOrDefault(label) + delta;
            foreach (var label in state.Labels) weights[label] = weights.GetValueOrDefault(label) + settings.Label;
        }
        var examples = order.Select(key => targets[key].Example).OfType<JsonObjectValue>()
            .OrderByDescending(item => Jobs.Long(item, "revision") ?? 0).Take(settings.MaxExamples)
            .Select(item => (JsonValue)JsonValue.Object(
            [
                new("eventId", item["id"]), new("target", item["target"]), new("title", item.TryGetValue("title", out var title) ? title : JsonValue.Null),
                new("action", item["action"]), new("labels", item["labels"]),
                new("note", Jobs.String(item, "note") is { } note ? Bound(note, 300) : JsonValue.Null), new("at", item["at"]),
            ]));
        return JsonValue.Object(
        [
            new("id", profileId),
            new("revision", revision),
            new("settings", settings.ToJson()),
            new("weights", JsonValue.Object(weights.Select(pair => new KeyValuePair<string, JsonValue>(pair.Key, Round(Math.Clamp(pair.Value, -1, 1)))))),
            new("examples", JsonValue.Array(examples)),
            new("events", events.Count),
            new("effective", effective),
            new("targets", targets.Count),
            new("createdAt", createdAt == 0 ? JsonValue.Null : createdAt),
            new("updatedAt", updatedAt == 0 ? JsonValue.Null : updatedAt),
        ]);
    }

    private static PreferenceSettings SettingsOf(IReadOnlyList<JsonObjectValue> events) =>
        PreferenceSettings.Of(events.LastOrDefault(item => Jobs.String(item, "action") == "settings")?["settings"] as JsonObjectValue);

    private static int LastReset(IReadOnlyList<JsonObjectValue> events)
    {
        for (var index = events.Count - 1; index >= 0; index--)
            if (Jobs.String(events[index], "action") == "reset") return index;
        return -1;
    }

    // ─── Rows ───────────────────────────────────────────────────────────────

    private List<JsonObjectValue> Events(SqliteTransaction? transaction, string profileId) =>
        catalog.Rows(transaction, "SELECT event FROM feedback_events WHERE profile_id = $id ORDER BY seq;", reader => (JsonObjectValue)JsonValue.Parse(reader.GetString(0)),
            ("$id", profileId));

    /// <summary>The profile's revision, creating its row at revision 0 when it has none.</summary>
    private long Revision(SqliteTransaction transaction, string profileId, long now)
    {
        if (catalog.Value(transaction, "SELECT revision FROM preference_profiles WHERE id = $id;", ("$id", profileId)) is long revision) return revision;
        catalog.Run(transaction, "INSERT INTO preference_profiles (id, revision, settings, created_at, updated_at) VALUES ($id, 0, $settings, $at, $at);",
            ("$id", profileId), ("$settings", CanonicalJson.Serialize(new PreferenceSettings().ToJson())), ("$at", now));
        return 0;
    }

    private long Expect(SqliteTransaction transaction, string profileId, long expectedRevision)
    {
        var revision = catalog.Value(transaction, "SELECT revision FROM preference_profiles WHERE id = $id;", ("$id", profileId)) is long stored ? stored : 0;
        if (revision != expectedRevision) throw new HostConflictException($"Profile {profileId} is at revision {revision}, not {expectedRevision}.");
        return revision;
    }

    private void Append(SqliteTransaction transaction, JsonObjectValue feedback, string hash, long revision, long now)
    {
        var target = feedback.TryGetValue("target", out var value) ? value as JsonObjectValue : null;
        catalog.Run(transaction, """
            INSERT INTO feedback_events (id, profile_id, request_id, request_hash, session_id, artifact_id, action, event, at)
            VALUES ($id, $profile, $request, $hash, $session, $artifact, $action, $event, $at);
            """, ("$id", Jobs.String(feedback, "id")), ("$profile", Jobs.String(feedback, "profileId")), ("$request", Jobs.String(feedback, "requestId")),
            ("$hash", hash), ("$session", target is null ? null : Jobs.String(target, "sessionId")), ("$artifact", target is null ? null : Jobs.String(target, "artifactId")),
            ("$action", Jobs.String(feedback, "action")), ("$event", CanonicalJson.Serialize(feedback)), ("$at", now));
        catalog.Run(transaction, "UPDATE preference_profiles SET revision = $revision, updated_at = $at WHERE id = $id;",
            ("$id", Jobs.String(feedback, "profileId")), ("$revision", revision), ("$at", now));
    }

    /// <summary>Removes a forgotten example's note from the event that chose it; notes never affect weights.</summary>
    private void Scrub(SqliteTransaction transaction, string eventId)
    {
        if (catalog.Value(transaction, "SELECT event FROM feedback_events WHERE id = $id;", ("$id", eventId)) is not string json) return;
        var scrubbed = ((JsonObjectValue)JsonValue.Parse(json)).SetItem("note", JsonValue.Null);
        catalog.Run(transaction, "UPDATE feedback_events SET event = $event WHERE id = $id;", ("$id", eventId), ("$event", CanonicalJson.Serialize(scrubbed)));
    }

    private static JsonObjectValue Internal(string profileId, string action, long revision, string actor, long now) => JsonValue.Object(
    [
        new("id", CanonicalJson.Id("fb_", profileId, action, revision.ToString(CultureInfo.InvariantCulture))),
        new("profileId", profileId),
        new("requestId", $"{action}:{revision.ToString(CultureInfo.InvariantCulture)}"),
        new("target", JsonValue.Null),
        new("action", action),
        new("actor", actor),
        new("at", now),
        new("reverses", JsonValue.Null),
        new("revision", revision),
    ]);

    // ─── Validation ─────────────────────────────────────────────────────────

    private static List<string> Check(FeedbackRequest request)
    {
        var errors = new List<FieldError>();
        if (!JudgmentPolicy.IsId(request.ProfileId)) errors.Add(new FieldError("/profileId", "must match ^[a-z0-9][a-z0-9_-]{0,39}$"));
        if (request.RequestId.Length is 0 or > 200) errors.Add(new FieldError("/requestId", "must have 1 to 200 characters"));
        if (request.Action is not ("more" or "less" or "label" or "dismiss" or "undo")) errors.Add(new FieldError("/action", "must be more, less, label, dismiss or undo"));
        var labels = request.Labels.Select(label => label.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (labels.Count > MaxLabels) errors.Add(new FieldError("/labels", $"may name at most {MaxLabels} labels"));
        foreach (var (label, index) in labels.Select((label, index) => (label, index)))
            if (!IsLabel(label)) errors.Add(new FieldError($"/labels/{index}", "must be 1 to 40 characters of a-z, 0-9, _ - . :"));
        if (request.Action == "label" && labels.Count == 0) errors.Add(new FieldError("/labels", "a label action names at least one label"));
        if (request.Note is { Length: > 1000 }) errors.Add(new FieldError("/note", "must have at most 1000 characters"));
        if (request.Action == "undo" && (labels.Count > 0 || request.Example || request.Note is not null))
            errors.Add(new FieldError("/action", "an undo carries no labels, note or example"));
        if (request.Action != "undo" && request.Reverses is not null) errors.Add(new FieldError("/reverses", "only an undo reverses an event"));
        if (request.Action == "dismiss" && request.Example) errors.Add(new FieldError("/example", "a dismissed artifact is not an example"));
        if (errors.Count > 0) throw HostValidationException.Of("Invalid feedback", errors);
        return labels;
    }

    private static void CheckId(string profileId)
    {
        if (!JudgmentPolicy.IsId(profileId)) throw new HostInvalidException($"A profile ID must match ^[a-z0-9][a-z0-9_-]{{0,39}}$: {profileId}");
    }

    private static JsonObjectValue Target(string sessionId, string artifactId) => JsonValue.Object([new("sessionId", sessionId), new("artifactId", artifactId)]);

    private static string Key(string sessionId, string artifactId) => sessionId + "/" + artifactId;

    private static string TargetKey(JsonObjectValue feedback) =>
        feedback.TryGetValue("target", out var value) && value is JsonObjectValue target ? Key(Jobs.String(target, "sessionId")!, Jobs.String(target, "artifactId")!) : "";

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.ToEven);

    private static string Bound(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
