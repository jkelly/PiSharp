// PiSharp-only (decision 0010, M6): the remote command table. Each command is one DurableDeviceClient call; a command of an application
// feature names it, and contracts/durable-capabilities.json lists the command as that feature's cli binding (the PiSharp.DurableCli tests
// check that the two agree). Sessions, executions, schedules and trigger credentials predate the manifest, so their commands need no feature.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Durable.Client;

namespace PiSharp.Cli.Durable;

internal static partial class DurableRemote
{
    private const string InSession = "--session <session>";
    private const string Paged = "[--limit <n>] [--cursor <cursor>]";

    /// <summary>Every remote command, in help order.</summary>
    internal static IReadOnlyList<RemoteCommand> Commands { get; } =
    [
        // ─── Device ─────────────────────────────────────────────────────────
        new("device", "", "", "The device: name, versions, start directories, models and features.", null,
            call => call.Client.GetAsync("/v1/device", cancellationToken: call.Token)) { Text = DeviceText },
        new("capabilities", "", "", "The capability manifest: each feature's version, routes, scopes and client bindings.", "capabilities",
            call => call.Client.CapabilitiesAsync(call.Token)) { Text = CapabilitiesText },

        // ─── Sessions and executions ────────────────────────────────────────
        new("sessions", "list", "", "Sessions and their agents.", null, call => call.Client.SessionsAsync(call.Token)) { Columns = ["id", "name", "cwd", "#agents"] },
        new("sessions", "show", "<session>", "One session, by ID or name.", null, call => call.Client.SessionAsync(call.Arg(0), call.Token)),
        new("sessions", "create", "--name <name> --cwd <dir> [--model <provider/id>] [--request <id>]",
            "Creates a session in one of the device's start directories; the same --request finds it again.", null,
            call => call.Client.CreateSessionAsync(SessionBody(call), call.RequestId, call.Token)),
        new("executions", "list", $"{InSession} [--kind <job|chat>] [--status <status>] [--query <text>] {Paged}", "A session's executions, newest first.", null,
            call => call.Client.ExecutionsAsync(call.Session, call.Args.Option("--kind"), call.Args.Option("--status"), call.Args.Option("--query"),
                Limit(call, 20), ObjectCursor(call.Args.Option("--cursor")), call.Token)) { Columns = ["id", "kind", "status", "title"] },
        new("executions", "show", $"{InSession} <task>", "An execution: its tasks, entries, job audit and usage.", null,
            call => call.Client.ExecutionAsync(call.Session, call.LongArg(0), call.Token)),
        new("executions", "cancel", $"{InSession} <task>", "Cancels an execution's task.", null,
            call => call.Client.AbortTaskAsync(call.Session, call.LongArg(0), call.Token)),
        new("executions", "usage", $"{InSession} <task>", "An execution's budget reservations, totals and whether its cost is known.", "budgets",
            call => call.Client.ExecutionUsageAsync(call.Session, call.LongArg(0), call.Token)),
        new("executions", "artifacts", $"{InSession} <task>", "The artifacts an execution kept.", "artifacts",
            call => call.Client.ExecutionArtifactsAsync(call.Session, call.LongArg(0), call.Token)) { Columns = ["id", "kind", "title"] },

        // ─── Schedules and trigger credentials ──────────────────────────────
        new("schedules", "list", "", "Recurring schedules.", null, call => call.Client.SchedulesAsync(call.Token))
            { Columns = ["id", "jobId", "sessionId", "everyMinutes", "enabled", "lastStatus"] },
        new("schedules", "create", $"{InSession} --job <id> --every <minutes> [--agent <id>] [--input <file>]",
            "Runs a job every N minutes; the first run is one interval away.", null, call => call.Client.CreateScheduleAsync(ScheduleBody(call), call.Token)),
        new("schedules", "pause", "<schedule>", "Pauses a schedule.", null,
            call => call.Client.UpdateScheduleAsync(call.Arg(0), new JsonObject { ["enabled"] = false }, call.Token)),
        new("schedules", "resume", "<schedule>", "Resumes a paused schedule.", null,
            call => call.Client.UpdateScheduleAsync(call.Arg(0), new JsonObject { ["enabled"] = true }, call.Token)),
        new("schedules", "every", "<schedule> <minutes>", "Changes a schedule's interval; a new clock starts.", null,
            call => call.Client.UpdateScheduleAsync(call.Arg(0), new JsonObject { ["everyMinutes"] = call.LongArg(1) }, call.Token)),
        new("schedules", "delete", "<schedule>", "Deletes a schedule and revokes the trigger credentials restricted to it.", null,
            call => call.Client.DeleteScheduleAsync(call.Arg(0), call.Token)),
        new("schedules", "tick", "<schedule>", "Runs a due schedule now; an early, paused or overlapping run is not forced.", null,
            call => call.Client.TickScheduleAsync(call.Arg(0), call.Token)),
        new("credentials", "list", "", "Trigger credentials, without their tokens.", null, call => call.Client.TriggerCredentialsAsync(call.Token))
            { Columns = ["id", "label", "scheduleId", "expiresAt"] },
        new("credentials", "issue", "--label <label> [--schedule <id>] [--hours <n>]",
            "Issues a trigger credential, restricted to one schedule when given; its token is shown once.", null,
            call => call.Client.IssueTriggerCredentialAsync(call.Args.Required("--label"), call.Args.Option("--schedule"), (int)(call.Args.Long("--hours") ?? 720),
                call.Token)) { Text = IssuedText },
        new("credentials", "revoke", "<credential>", "Revokes a trigger credential.", null, call => call.Client.RevokeTriggerCredentialAsync(call.Arg(0), call.Token)),

        // ─── Budgets ────────────────────────────────────────────────────────
        new("budgets", "show", "", "Today's limits, use, and open or uncertain reservations.", "budgets", call => call.Client.BudgetsAsync(call.Token))
            { Text = BudgetsText },
        new("budgets", "set", "<meter=limit|none>... --revision <n>", "Sets per-day limits (none removes one).", "budgets",
            call => call.Client.UpdateBudgetsAsync(PerDay(call.Args.Positional), call.Revision, call.Token)),
        new("budgets", "resolve", "<operation> settled|released [--detail <text>]", "Resolves an uncertain reservation by hand.", "budgets",
            call => call.Client.ResolveReservationAsync(call.Arg(0), call.Arg(1), null, call.Args.Option("--detail"), call.Token)),
        new("retention", "show", "", "What a purge would remove now and what it always keeps (a dry run).", "retention",
            call => call.Client.RetentionAsync(call.Token)) { Text = RetentionText },
        new("retention", "purge", "[--batch <n>] [--all]", "Removes catalog records past retention in batches, then unreferenced blobs; --all repeats until done.",
            "retention", PurgeAsync) { Text = RetentionText },

        // ─── Policies and judgments ─────────────────────────────────────────
        new("policies", "list", "", "Judgment policies.", "policies", call => call.Client.PoliciesAsync(call.Token))
            { Columns = ["id", "activeVersion", "revision", "title"] },
        new("policies", "show", "<policy> [--version <n>]", "A policy with its versions, or one version.", "policies",
            call => call.Args.Long("--version") is { } version ? call.Client.PolicyVersionAsync(call.Arg(0), (int)version, call.Token)
                : call.Client.PolicyAsync(call.Arg(0), call.Token)),
        new("policies", "publish", "<policy> --file <spec.json> [--request <id>]", "Publishes a new version of a policy; activate it to use it.", "policies",
            call => call.Client.PublishPolicyAsync(call.Arg(0), call.File("--file"), call.RequestId, call.Token)),
        new("policies", "activate", "<policy> <version> --revision <n>", "Makes a published version the active one.", "policies",
            call => call.Client.ActivatePolicyAsync(call.Arg(0), (int)call.LongArg(1), call.Revision, call.Token)),
        new("judgments", "list", $"{InSession} {Paged}", "A session's judgments, newest first.", "judgments",
            call => call.Client.JudgmentsAsync(call.Session, Limit(call, 20), call.Args.Option("--cursor"), call.Token))
            { Columns = ["id", "policy.id", "result.label", "result.confidence", "route", "decision"] },
        new("judgments", "show", $"{InSession} <judgment>", "A judgment: its policy version, input, result, route and decision.", "judgments",
            call => call.Client.JudgmentAsync(call.Session, call.Arg(0), call.Token)),
        new("judgments", "start", $"{InSession} --file <request.json> [--request <id>]",
            "Judges an input under a policy: {policyId, input, evidenceIds?, title?}.", "judgments",
            call => call.Client.StartJudgmentAsync(call.Session, call.File("--file"), call.RequestId, call.Token)),
        new("judgments", "decide", $"{InSession} <judgment> approve|reject [--reason <text>]", "Approves or rejects a judgment waiting for review.", "judgments",
            call => call.Client.DecideJudgmentAsync(call.Session, call.Arg(0), call.Arg(1), call.Args.Option("--reason"), call.Token)),

        // ─── Artifacts and evidence ─────────────────────────────────────────
        new("artifacts", "list", $"{InSession} {Paged}", "A session's artifacts, newest first.", "artifacts",
            call => call.Client.ArtifactsAsync(call.Session, Limit(call, 20), call.Args.Option("--cursor"), call.Token)) { Columns = ["id", "kind", "title"] },
        new("artifacts", "show", $"{InSession} <artifact>", "An artifact's envelope.", "artifacts", call => call.Client.ArtifactAsync(call.Session, call.Arg(0), call.Token)),
        new("artifacts", "text", $"{InSession} <artifact>", "An artifact as plain text.", "artifacts",
            async call => JsonValue.Create(await call.Client.ArtifactTextAsync(call.Session, call.Arg(0), call.Token).ConfigureAwait(false))),
        new("artifacts", "export", $"{InSession} <artifact> [--out <file>]", "An artifact with its evidence, as JSON.", "artifacts",
            async call => call.Save(await call.Client.ExportArtifactAsync(call.Session, call.Arg(0), call.Token).ConfigureAwait(false))),
        new("artifacts", "action", $"{InSession} <artifact> <action> [--request <id>]", "Runs one of an artifact's confirmed actions.", "artifacts",
            call => call.Client.InvokeArtifactActionAsync(call.Session, call.Arg(0), call.Arg(1), call.RequestId, call.Token)),
        new("evidence", "list", $"{InSession} {Paged}", "A session's evidence, newest first.", "evidence",
            call => call.Client.EvidenceListAsync(call.Session, Limit(call, 20), call.Args.Option("--cursor"), call.Token)) { Columns = ["id", "title", "url"] },
        new("evidence", "show", $"{InSession} <evidence>", "An evidence record.", "evidence", call => call.Client.EvidenceAsync(call.Session, call.Arg(0), call.Token)),
        new("evidence", "text", $"{InSession} <evidence>", "An evidence record's text.", "evidence",
            async call => JsonValue.Create(await call.Client.EvidenceTextAsync(call.Session, call.Arg(0), call.Token).ConfigureAwait(false))),
        new("evidence", "add", $"{InSession} --file <evidence.json>", "Adds evidence: {title, text, url?, contentType?}.", "evidence",
            call => call.Client.AddEvidenceAsync(call.Session, call.File("--file"), call.Token)),

        // ─── Sources ────────────────────────────────────────────────────────
        new("sources", "list", "", "Monitored sources.", "sources", call => call.Client.SourcesAsync(call.Token)) { Columns = ["id", "kind", "title", "revision"] },
        new("sources", "show", "<source>", "A source with its state and health.", "sources", call => call.Client.SourceAsync(call.Arg(0), call.Token)),
        new("sources", "create", "--file <source.json> [--request <id>]", "Adds a feed, web page or GitHub releases source.", "sources",
            call => call.Client.CreateSourceAsync(call.File("--file"), call.RequestId, call.Token)),
        new("sources", "update", "<source> --file <changes.json> --revision <n>", "Changes a source.", "sources",
            call => call.Client.UpdateSourceAsync(call.Arg(0), call.File("--file"), call.Revision, call.Token)),
        new("sources", "delete", "<source> --revision <n>", "Deletes a source; its items stay so recreating it announces nothing old.", "sources",
            call => call.Client.DeleteSourceAsync(call.Arg(0), call.Revision, call.Token)),
        new("sources", "items", $"<source> {Paged}", "A source's items, most recently changed first.", "sources",
            call => call.Client.SourceItemsAsync(call.Arg(0), Limit(call, 50), call.Args.Option("--cursor"), call.Token))
            { Columns = ["itemId", "title", "version", "changedAt"] },
        new("sources", "health", "", "Retrieval health by provider and origin: failures and cooldowns.", "sources",
            call => call.Client.RetrievalHealthAsync(call.Token)) { Columns = ["provider", "origin", "failures", "cooldownUntil"] },
        new("sources", "check", $"{InSession} [--source <id>] [--policy <id>] [--reevaluate] [--force] [--request <id>]",
            "Checks sources now (--source repeats; none checks all) and optionally judges what changed.", "sources",
            call => call.Client.StartSourceCheckAsync(call.Session, CheckBody(call), call.RequestId, call.Token)),

        // ─── Research ───────────────────────────────────────────────────────
        new("research", "start", $"{InSession} --file <request.json> [--request <id>]",
            "Starts supervised research: {question, angles?, sourceIds?, evidenceIds?, retrieval? {maxFetches?, origins?}, policyId?, budget?}.", "research",
            call => call.Client.StartResearchAsync(call.Session, call.File("--file"), call.RequestId, call.Token)),

        // ─── Feedback and preferences ───────────────────────────────────────
        new("feedback", "send", $"{InSession} <artifact> more|less|dismiss|label|undo [--labels <a,b>] [--note <text>] [--profile <id>] [--example] [--request <id>]",
            "Records feedback on an artifact in a preference profile; the same --request cannot record it twice.", "feedback",
            call => call.Client.SendFeedbackAsync(call.Session, call.Arg(0), FeedbackBody(call), call.RequestId, call.Token)),
        new("feedback", "show", $"{InSession} <artifact> [--profile <id>]", "An artifact's feedback: vote, labels, example, and what undo reverses.", "feedback",
            call => call.Client.ArtifactFeedbackAsync(call.Session, call.Arg(0), call.Args.Option("--profile"), call.Token)),
        new("preferences", "list", "", "Preference profiles.", "preferences", call => call.Client.PreferencesAsync(call.Token)) { Columns = ["id", "revision", "events"] },
        new("preferences", "show", "<profile>", "A profile: revision, settings, weights and examples.", "preferences",
            call => call.Client.PreferenceAsync(call.Arg(0), call.Token)),
        new("preferences", "set", "<profile> --revision <n> [--setting <name=value>] [--forget <example>]",
            "Changes settings (more, less, label, maxExamples; --setting repeats) or forgets examples (--forget repeats).", "preferences",
            call => call.Client.UpdatePreferenceAsync(call.Arg(0), Settings(call.Args.Options("--setting")), call.Args.Options("--forget") is { Count: > 0 } forget ? forget : null,
                call.Revision, call.Token)),
        new("preferences", "reset", "<profile> --revision <n>", "Clears a profile's weights and examples; its events stay for export.", "preferences",
            call => call.Client.ResetPreferenceAsync(call.Arg(0), call.Revision, call.Token)),
        new("preferences", "export", "<profile> [--out <file>]", "A profile with its events, as JSON.", "preferences",
            async call => call.Save(await call.Client.ExportPreferenceAsync(call.Arg(0), call.Token).ConfigureAwait(false))),
        new("preferences", "delete", "<profile> --revision <n>", "Deletes a profile and its events.", "preferences",
            call => call.Client.DeletePreferenceAsync(call.Arg(0), call.Revision, call.Token)),
    ];

    private static int Limit(RemoteCall call, int fallback) =>
        call.Args.Long("--limit") is { } limit ? limit is >= 1 and <= 1000 ? (int)limit : throw new RemoteUsageException("--limit must be 1 to 1000.") : fallback;

    private static JsonObject SessionBody(RemoteCall call)
    {
        var body = new JsonObject { ["name"] = call.Args.Required("--name"), ["cwd"] = call.Args.Required("--cwd") };
        if (call.Args.Option("--model") is { } model)
        {
            var slash = model.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == model.Length - 1) throw new RemoteUsageException("--model must be <provider>/<model id>.");
            body["model"] = new JsonObject { ["provider"] = model[..slash], ["modelId"] = model[(slash + 1)..] };
        }
        return body;
    }

    private static JsonObject ScheduleBody(RemoteCall call)
    {
        var body = new JsonObject
        {
            ["sessionId"] = call.Session,
            ["jobId"] = call.Args.Required("--job"),
            ["everyMinutes"] = call.Args.Long("--every") ?? throw new RemoteUsageException("--every is required."),
        };
        if (call.Args.Long("--agent") is { } agent) body["agentId"] = agent;
        if (call.Args.Option("--input") is not null) body["input"] = call.File("--input");
        return body;
    }

    private static JsonObject CheckBody(RemoteCall call)
    {
        var body = new JsonObject();
        if (call.Args.Options("--source") is { Count: > 0 } sources) body["sourceIds"] = new JsonArray([.. sources.Select(id => (JsonNode)id)]);
        if (call.Args.Option("--policy") is { } policy) body["policyId"] = policy;
        if (call.Args.Flag("--reevaluate")) body["reevaluate"] = true;
        if (call.Args.Flag("--force")) body["force"] = true;
        return body;
    }

    private static JsonObject FeedbackBody(RemoteCall call)
    {
        var body = new JsonObject { ["action"] = call.Arg(1) };
        if (call.Args.Option("--labels") is { } labels)
            body["labels"] = new JsonArray([.. labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(label => (JsonNode)label)]);
        if (call.Args.Option("--note") is { } note) body["note"] = note;
        if (call.Args.Option("--profile") is { } profile) body["profileId"] = profile;
        if (call.Args.Flag("--example")) body["example"] = true;
        return body;
    }

    /// <exception cref="RemoteUsageException">A limit is not <c>meter=n</c> or <c>meter=none</c>.</exception>
    private static JsonObject PerDay(IEnumerable<string> limits)
    {
        var perDay = new JsonObject();
        foreach (var limit in limits)
        {
            var equals = limit.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0) throw new RemoteUsageException($"{limit} is not <meter>=<limit> or <meter>=none.");
            var value = limit[(equals + 1)..];
            perDay[limit[..equals]] = value == "none" ? null
                : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number
                : throw new RemoteUsageException($"{limit}: the limit must be a non-negative whole number or none.");
        }
        return perDay;
    }

    /// <exception cref="RemoteUsageException">A setting is not <c>name=number</c>.</exception>
    private static JsonObject? Settings(IReadOnlyList<string> settings)
    {
        if (settings.Count == 0) return null;
        var json = new JsonObject();
        foreach (var setting in settings)
        {
            var equals = setting.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !double.TryParse(setting[(equals + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new RemoteUsageException($"{setting} is not <name>=<number>.");
            json[setting[..equals]] = value;
        }
        return json;
    }

    private static string DeviceText(JsonNode? node)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{At(node, "name")} ({At(node, "uid")}) on {At(node, "hostname")}, {At(node, "platform")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"API v{At(node, "apiVersion")}, durable {At(node, "durableVersion")}");
        text.AppendLine("Start directories: " + string.Join(", ", (At(node, "startDirs") as JsonArray ?? []).Select(dir => dir?.ToString())));
        text.AppendLine(CultureInfo.InvariantCulture, $"Models: {(At(node, "models") as JsonArray)?.Count ?? 0}");
        text.Append("Features: " + string.Join(", ", (At(node, "features") as JsonArray ?? []).Select(feature => $"{At(feature, "id")} v{At(feature, "version")}")));
        return text.ToString();
    }

    private static string CapabilitiesText(JsonNode? node) => string.Join('\n', (At(node, "features") as JsonArray ?? []).Select(feature =>
    {
        var cli = At(feature, "cli") as JsonArray ?? [];
        return $"{At(feature, "id")} v{At(feature, "version")}: {At(feature, "title")}" +
            (cli.Count > 0 ? "\n  " + string.Join("\n  ", cli.Select(command => command?.ToString())) : "");
    }));

    private static string IssuedText(JsonNode? node)
    {
        var credential = At(node, "credential");
        var schedule = At(credential, "scheduleId") is JsonValue id ? $"restricted to schedule {id}" : "for every schedule";
        return $"Credential {At(credential, "id")} ({At(credential, "label")}), {schedule}, expires {At(credential, "expiresAt")}.\n" +
            $"Token (shown once; it can only tick schedules): {At(node, "token")}";
    }

    /// <summary>One purge batch, or with --all batches until none remain (at most 1000); the purged counts are totals over the batches.</summary>
    private static async Task<JsonNode?> PurgeAsync(RemoteCall call)
    {
        var batch = (int)Math.Clamp(call.Args.Long("--batch") ?? 500, 0, int.MaxValue);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        JsonObject? report = null;
        var passes = 0;
        do
        {
            report = await call.Client.PurgeRetentionAsync(batch, call.Token).ConfigureAwait(false) as JsonObject;
            passes++;
            foreach (var category in (report?["categories"] as JsonArray ?? []).OfType<JsonObject>())
                totals[category["id"]!.GetValue<string>()] = totals.GetValueOrDefault(category["id"]!.GetValue<string>()) + category["purged"]!.GetValue<long>();
        } while (call.Args.Flag("--all") && report?["more"]?.GetValue<bool>() == true && passes < 1000);
        if (report is null) return null;
        foreach (var category in (report["categories"] as JsonArray ?? []).OfType<JsonObject>()) category["purged"] = totals[category["id"]!.GetValue<string>()];
        report["passes"] = passes;
        return report;
    }

    private static string RetentionText(JsonNode? node)
    {
        var text = new StringBuilder();
        var dry = At(node, "dryRun")?.GetValue<bool>() == true;
        text.AppendLine(dry ? "Past retention (dry run; nothing removed):" : $"Purged in {At(node, "passes")?.ToJsonString() ?? "1"} batch(es):");
        foreach (var category in (At(node, "categories") as JsonArray ?? []).OfType<JsonObject>())
            text.AppendLine(CultureInfo.InvariantCulture, $"  {category["id"]}: {category["eligible"]} past retention, {category["purged"]} purged, {category["kept"]} kept because still needed");
        text.AppendLine(CultureInfo.InvariantCulture, $"  blobs: {At(node, "blobs.blobs")} unreferenced ({At(node, "blobs.bytes")} bytes), {At(node, "blobs.orphanFiles")} unknown files, " +
            $"{At(node, "blobs.temporaryFiles")} temporary files{(dry ? " to collect" : " collected")}");
        if (At(node, "more")?.GetValue<bool>() == true) text.AppendLine("More remain: run pisharp durable retention purge again, or with --all.");
        text.Append("Always kept: " + string.Join("; ", (At(node, "alwaysKept") as JsonArray ?? []).Select(item => item?.GetValue<string>())));
        return text.ToString();
    }

    private static string BudgetsText(JsonNode? node)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Day {At(node, "day")}, limits revision {At(node, "limits.revision")}");
        var perDay = At(node, "limits.perDay") as JsonObject ?? [];
        var used = At(node, "used") as JsonObject ?? [];
        foreach (var meter in perDay.Select(pair => pair.Key).Union(used.Select(pair => pair.Key)).Order(StringComparer.Ordinal))
            text.AppendLine(CultureInfo.InvariantCulture, $"  {meter}: {used[meter]?.ToJsonString() ?? "0"} used of {perDay[meter]?.ToJsonString() ?? "no limit"}");
        text.Append(CultureInfo.InvariantCulture, $"{At(node, "active")} open reservation(s), {At(node, "uncertain")} uncertain");
        return text.ToString();
    }
}
