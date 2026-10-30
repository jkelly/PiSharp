// Decision 0011 (run W5, E7): Pin as test case. PinService turns a PinTarget into a draft (the request as the case stores it, the reference
// output, where it came from, a name, tags and checks to start with), the draft into _TestCaseModal's view (one pane per suite with
// SuiteService.EstimateWith: the worst case before and after the case, against the suite-run cap), and a save into a case (and, for New
// suite…, a suite whose only target is the request's own provider, model, account and settings). Saves are idempotent: the modal carries the
// case id and the new suite's id it was opened with, and SuiteService's creates return the row that has the id. Adding a case that makes a
// suite stop fitting its cap pauses its schedule (SuiteService reconciles it; the pane said so).
//
// Rules: a pinned run is one call that completed (PinRules); an Arena answer is pinned only after its battle's vote (or once it is void),
// and an agent run never (agent test cases come later); a chat answer is pinned from the branch it is in, with the request the provider
// received (PinMessages), and its run row when runs.db has it (matched by the Spend Guard's op, else by model, usage and time). Refusals
// never name a model, provider or account. The case never holds an account or a secret (TestCase.CaseSpec, RunSpec's header rules), and the
// reference output loses the Spend Guard's field (pisharp) so it is the answer as the provider gave it.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Evals.Arena;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Evals.Suites;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals.Pins;

/// <summary>A save from the modal.</summary>
/// <param name="Target">What is pinned (or the suite or case).</param>
/// <param name="CaseId">The case id the modal was opened with.</param>
/// <param name="Name">The name, or null for the draft's.</param>
/// <param name="Tags">The tags, comma-separated, or null for the draft's.</param>
/// <param name="SuiteId">The suite to add it to, or null for a new one.</param>
/// <param name="NewSuiteId">The new suite's id the modal was opened with.</param>
/// <param name="NewSuiteName">The new suite's name.</param>
/// <param name="Checks">The checks, or null for the draft's.</param>
/// <param name="SystemPrompt">Add mode: the system prompt.</param>
/// <param name="Prompt">Add mode: the user message.</param>
/// <param name="MaxTokens">Add mode: the most output tokens, or null.</param>
public sealed record PinSaveRequest(PinTarget Target, string CaseId, string? Name = null, string? Tags = null, string? SuiteId = null, string? NewSuiteId = null,
    string? NewSuiteName = null, IReadOnlyList<Check>? Checks = null, string? SystemPrompt = null, string? Prompt = null, int? MaxTokens = null)
{
    /// <summary>A save as pin.js posts it: <c>{target, caseId, name?, tags?, suiteId?, newSuite?: {id, name}, checks?, systemPrompt?, prompt?,
    /// maxTokens?}</c>.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public static PinSaveRequest FromJson(JsonObject? body)
    {
        if (body is null) throw new ArgumentException("Send a JSON body.");
        var target = PinTarget.FromJson(body["target"] as JsonObject);
        var caseId = EvalIds.Require(EvalIds.Case, Text(body, "caseId"), "test case");
        var newSuite = body["newSuite"] as JsonObject;
        IReadOnlyList<Check>? checks = null;
        if (body["checks"] is JsonArray list)
        {
            if (list.Count > TestCase.MaxChecks) throw new ArgumentException($"A test case has at most {TestCase.MaxChecks} checks.");
            checks = [.. list.Select(item => item is JsonObject check ? ReadCheck(check) : throw new ArgumentException("A check is a JSON object."))];
        }
        else if (body["checks"] is not null) throw new ArgumentException("checks is a list.");
        int? maxTokens = body["maxTokens"] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<int>(out var number) => number,
            JsonValue value when value.TryGetValue<string>(out var text) && text.Trim().Length == 0 => null,
            JsonValue value when value.TryGetValue<string>(out var text) && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ArgumentException("maxTokens is a whole number."),
        };
        var suite = Text(body, "suiteId");
        return new PinSaveRequest(target, caseId, Text(body, "name"), Text(body, "tags"), string.IsNullOrWhiteSpace(suite) ? null : suite.Trim(),
            newSuite is null ? null : Text(newSuite, "id"), newSuite is null ? null : Text(newSuite, "name"), checks, Text(body, "systemPrompt"), Text(body, "prompt"), maxTokens);
    }

    /// <summary>A check from its JSON (EvalJson's names: <c>{kind: "contains", text: "..."}</c>).</summary>
    /// <exception cref="ArgumentException">It is not a check.</exception>
    public static Check ReadCheck(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return json.Deserialize<Check>(EvalJson.Options) ?? throw new ArgumentException("A check is empty.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("A check is not valid: " + error.Message);
        }
    }

    private static string? Text(JsonObject json, string key) => json[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new ArgumentException($"{key} is text."),
    };
}

/// <summary>A saved pin.</summary>
/// <param name="Case">The case as stored.</param>
/// <param name="Created">Whether the case was made by this save (false: it already had this id, or it was edited).</param>
/// <param name="Suite">Its suite, as saved (a schedule the case pushed over its cap is paused).</param>
/// <param name="SuiteCreated">Whether the suite was made by this save.</param>
/// <param name="Toast">The toast's text.</param>
public sealed record PinSaved(TestCase Case, bool Created, Suite Suite, bool SuiteCreated, string Toast);

/// <summary>Pin as test case: drafts, the modal's view, check previews and saves.</summary>
public sealed partial class PinService
{
    private readonly IServiceProvider services;

    /// <summary>A service over the app's services (the run store, the suites and checks; chats, the Arena, the catalog and accounts when
    /// the host has them).</summary>
    public PinService(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
    }

    private RunStore Runs => services.GetRequiredService<RunStore>();

    private SuiteService Suites => services.GetRequiredService<SuiteService>();

    private CheckRunner CheckRunner => services.GetRequiredService<CheckRunner>();

    private TimeProvider Time => services.GetService<TimeProvider>() ?? TimeProvider.System;

    // ----- drafts -----

    /// <summary>The draft of <paramref name="target"/>.</summary>
    /// <exception cref="KeyNotFoundException">The run, chat, entry, suite or case does not exist.</exception>
    /// <exception cref="PinRefusedException">It cannot be pinned; the message says why.</exception>
    /// <exception cref="ArgumentException">The target is not valid.</exception>
    public async Task<PinDraft> DraftAsync(PinTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        target = target.Normalize();
        switch (target.Mode)
        {
            case PinMode.Edit:
                var existing = Suites.Store.Case(target.CaseId!) ?? throw new KeyNotFoundException($"There is no test case {target.CaseId}.");
                return new PinDraft
                {
                    Mode = PinMode.Edit, Target = target, Spec = existing.Spec, Reference = existing.Reference, Origin = existing.Origin, Name = existing.Name,
                    Tags = existing.Tags, Checks = existing.Checks, Existing = existing, Suite = Suites.RequireSuite(existing.SuiteId),
                };
            case PinMode.Add:
                var suite = Suites.RequireSuite(target.SuiteId!);
                if (suite.Targets.Count == 0) throw new PinRefusedException("Add a target to this suite first: a case's request starts on one of its targets.");
                return new PinDraft { Mode = PinMode.Add, Target = target, Origin = new TestCaseOrigin(PinSource.Suite), Suite = suite };
            default:
                return target.RunId is null
                    ? await ChatDraftAsync(target, target.ChatId!, null, cancellationToken).ConfigureAwait(false)
                    : await RunDraftAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PinDraft> RunDraftAsync(PinTarget target, CancellationToken cancellationToken)
    {
        var run = Runs.Get(target.RunId!) ?? throw new KeyNotFoundException($"There is no run {target.RunId}.");
        if (PinRules.Refusal(run) is { } refusal) throw new PinRefusedException(refusal);
        string? battleId = null;
        string? tag = null;
        if (run.Source == "arena")
        {
            var battle = run.GroupId is { } group ? services.GetService<ArenaStore>()?.ByGroup(group).FirstOrDefault(item => item.Contestants.Any(contestant => contestant.RunId == run.Id)) : null;
            if (battle is not null)
            {
                if (battle.Mode == ArenaMode.Agent) throw new PinRefusedException(PinRules.AgentLater);
                if (battle.Status is ArenaStatus.Held or ArenaStatus.Ready)
                    throw new PinRefusedException("This answer is part of a blind Arena battle that hasn't been voted on. Vote first, then pin it.");
                battleId = battle.Id;
                tag = battle.Tag;
            }
        }
        if (run.Source == "chat") return await ChatDraftAsync(target, run.Session!, run, cancellationToken).ConfigureAwait(false);
        var spec = PlaygroundPages.FromRun(run) ?? throw new PinRefusedException("This run's stored request can't be read back, so it can't be a test case.");
        var origin = new TestCaseOrigin(target.Source, run.Id, BattleId: battleId);
        return Draft(target, CaseSpec(spec), Reference(TestCaseReference.FromRun(run)), origin, run.Account, tag);
    }

    private async Task<PinDraft> ChatDraftAsync(PinTarget target, string chatId, RunRecord? run, CancellationToken cancellationToken)
    {
        var chats = services.GetService<ChatService>() ?? throw new PinRefusedException("Chats aren't available here, so a chat answer can't be pinned.");
        var binding = chats.Find(chatId);
        if (binding is null || binding.IsArchived) throw new KeyNotFoundException($"There is no chat {chatId}.");
        PiSharp.Durable.Hosting.HostedSession session;
        try
        {
            session = chats.Session(chatId);
        }
        catch (ChatException error)
        {
            throw new KeyNotFoundException(error.Message);
        }
        IReadOnlyList<JsonObject> entries = [];
        var index = -1;
        long? agent = null;
        if (target.EntryId is { } entryId)
        {
            agent = target.AgentId ?? binding.AgentId;
            try
            {
                entries = await PiSharp.Durable.Server.AgentFrames.ActiveEntriesAsync(session, agent.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (PiSharp.Durable.Hosting.HostNotFoundException)
            {
                throw new KeyNotFoundException($"Chat {chatId} has no branch {agent}.");
            }
            index = IndexOf(entries, entryId);
            if (index < 0 || ChatDisplay.Str(entries[index], "kind") != "pi.assistant")
                throw new PinRefusedException("Only an answer in this branch's current context can be pinned.");
        }
        else if (run is not null)
        {
            foreach (var hosted in await session.AgentsAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyList<JsonObject> branch;
                try { branch = await PiSharp.Durable.Server.AgentFrames.ActiveEntriesAsync(session, hosted.Id, cancellationToken).ConfigureAwait(false); }
                catch (PiSharp.Durable.Hosting.HostNotFoundException) { continue; }
                for (var at = 0; at < branch.Count && index < 0; at++)
                    if (ChatDisplay.Str(branch[at], "kind") == "pi.assistant" && ChatEntryMeta.Of(branch[at]) is { } meta && Matches(meta, run))
                    {
                        entries = branch;
                        index = at;
                        agent = hosted.Id;
                    }
                if (index >= 0) break;
            }
            if (index < 0) throw new PinRefusedException("The chat answer this call made is no longer in the chat's context, so its request can't be read.");
        }
        var entry = entries[index];
        var message = (entry["model"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => ChatDisplay.Str(item, "role") == "assistant")
            ?? throw new PinRefusedException("This chat entry has no answer to keep as the reference output.");
        var entryMeta = ChatEntryMeta.Of(entry)!;
        if (entryMeta.StopReason is "error" or "aborted")
            throw new PinRefusedException("This answer ended without finishing, so it can't be the reference output.");
        run ??= FindRun(chatId, entryMeta);
        var (system, messages, tools) = PinMessages.Request(entries, index);
        var options = run?.Options;
        var thinking = run is null ? binding.Thinking : OptionText(options, "thinking") ?? "off";
        var tier = run is null ? binding.Tier : OptionText(options, "tier");
        var spec = new RunSpec
        {
            Provider = entryMeta.Provider ?? run?.Provider ?? binding.Provider,
            Model = entryMeta.Model ?? run?.Model ?? binding.Model,
            SystemPrompt = system,
            Messages = messages,
            Tools = tools.Count == 0 ? null : tools,
            Thinking = RunSpec.ThinkingLevels.Contains(thinking) ? thinking : null,
            MaxTokens = options?["maxTokens"] is JsonValue max && max.TryGetValue<double>(out var most) && most is >= 1 and <= int.MaxValue ? (int)most : null,
            ServiceTier = tier is not null && RunSpec.ServiceTiers.Contains(tier) ? tier : null,
        };
        var reference = run is { Status: RunStatus.Completed } ? TestCaseReference.FromRun(run) : new TestCaseReference
        {
            Text = PinMessages.Text(message),
            Output = message.DeepClone(),
            Provider = entryMeta.Provider,
            Model = entryMeta.Model,
            ResponseModel = ChatDisplay.Str(message, "responseModel"),
            StopReason = entryMeta.StopReason,
            InputTokens = entryMeta.HasUsage ? entryMeta.Input : null,
            OutputTokens = entryMeta.HasUsage ? entryMeta.Output : null,
            CostMicros = entryMeta.Cost > 0 ? (long)Math.Round(entryMeta.Cost * 1_000_000) : null,
            DurationMs = entryMeta.DurationMs is { } duration ? (long)duration : null,
        };
        var entryText = ChatDisplay.Long(entry, "id")?.ToString(CultureInfo.InvariantCulture);
        var origin = new TestCaseOrigin(target.Source, run?.Id, chatId, entryText);
        var draft = Draft(target with { ChatId = target.RunId is null ? chatId : null, AgentId = target.RunId is null ? agent : null }, CaseSpec(spec), Reference(reference), origin,
            run?.Account ?? binding.AccountId, null);
        return draft;
    }

    private PinDraft Draft(PinTarget target, RunSpec spec, TestCaseReference reference, TestCaseOrigin origin, string? account, string? tag)
    {
        var last = spec.Messages.LastOrDefault(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Text))?.Text;
        var name = last is null ? "Pinned case" : Line(last, TestCase.MaxName);
        IReadOnlyList<string> tags = [];
        if (tag is not null)
        {
            try { tags = EvalTags.NormalizeList([tag]); }
            catch (ArgumentException) { }
        }
        return new PinDraft
        {
            Mode = PinMode.Pin, Target = target, Spec = spec, Reference = reference, Origin = origin, Name = name, Tags = tags,
            Checks = Suggested(spec, reference), Account = account,
        };
    }

    /// <summary>The checks a pin starts with: a tool check for the first tool the reference called, a JSON schema check when the request
    /// asked for one, and a cost and latency check at 1.5 times the reference's (cost rounded up to a cent, time to 5 s).</summary>
    public static IReadOnlyList<Check> Suggested(RunSpec spec, TestCaseReference? reference)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var checks = new List<Check>();
        void Add(Check check)
        {
            try { checks.Add(check.Normalize()); }
            catch (ArgumentException) { }
        }
        var answer = reference?.ToAnswer();
        if (answer?.ToolCalls.FirstOrDefault() is { } call) Add(new Check { Kind = CheckKind.Tool, Tool = call.Name, Calls = ToolCallCount.AtLeastOnce });
        if (spec.ResponseFormat is { } format && format["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind == "json_schema"
            && (format["json_schema"]?["schema"] ?? format["schema"]) is JsonObject schema)
            Add(new Check { Kind = CheckKind.JsonSchema, Schema = (JsonObject)schema.DeepClone() });
        long? cost = reference?.CostMicros is > 0 and var micros ? Math.Max(10_000, (long)Math.Ceiling(micros * 1.5 / 10_000) * 10_000) : null;
        long? latency = reference?.DurationMs is > 0 and var duration ? Math.Max(5_000, (long)Math.Ceiling(duration * 1.5 / 5_000) * 5_000) : null;
        if (cost is not null || latency is not null) Add(new Check { Kind = CheckKind.Limits, MaxCostMicros = cost, MaxLatencyMs = latency });
        return checks;
    }

    // ----- the modal -----

    /// <summary>The modal for <paramref name="target"/>: its draft as <see cref="TestCaseModalView"/>, or a refused view when it cannot be
    /// pinned.</summary>
    /// <exception cref="KeyNotFoundException">The run, chat, entry, suite or case does not exist.</exception>
    /// <exception cref="ArgumentException">The target is not valid.</exception>
    public async Task<TestCaseModalView> ModalAsync(PinTarget target, CancellationToken cancellationToken = default)
    {
        PinDraft draft;
        try
        {
            draft = await DraftAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (PinRefusedException refused)
        {
            return Refused(refused.Message);
        }
        return Modal(draft);
    }

    /// <summary>A modal that only says why nothing can be pinned.</summary>
    public static TestCaseModalView Refused(string message) => new() { Refusal = message, Subtitle = "This can't be saved as a test case." };

    /// <summary>The modal of <paramref name="draft"/>.</summary>
    public TestCaseModalView Modal(PinDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var caseId = draft.Existing?.Id ?? EvalIds.New(EvalIds.Case);
        var rows = draft.Checks.Select(check => PinText.Row(check, draft.Reference)).ToList();
        var view = new TestCaseModalView
        {
            Mode = draft.Mode,
            Target = draft.Target.ToJson().ToJsonString(),
            CaseId = caseId,
            NewSuiteId = EvalIds.New(EvalIds.Suite),
            Name = draft.Name,
            Tags = string.Join(", ", draft.Tags),
            Checks = rows,
            ChecksLine = PinText.ChecksLine(rows.Count, rows.Count(row => row.Verdict == "passed"), draft.Reference is not null),
            HasReference = draft.Reference is not null,
        };
        view = draft.Mode switch
        {
            PinMode.Edit => view with
            {
                Title = "Edit test case",
                Subtitle = "Changes its name, tags and checks. Its request and reference output stay as they were pinned.",
                SaveText = "Save changes",
                SuiteFixed = true,
                Suites = [Fixed(draft.Suite!)],
            },
            PinMode.Add => view with
            {
                Title = "Add test case",
                Subtitle = "Write the request; each of the suite's targets answers it. A case added here has no reference output.",
                SaveText = "Add test case",
                SuiteFixed = true,
                Suites = [Fixed(draft.Suite!)],
                AddTarget = draft.Suite!.Targets[0].Provider + "/" + draft.Suite.Targets[0].Model,
            },
            _ => Choices(view, draft, caseId),
        };
        if (draft.Spec is { } spec) view = Request(view, draft, spec);
        if (draft.Reference is { } reference) view = Reference(view, reference);
        return view;
    }

    private TestCaseModalView Choices(TestCaseModalView view, PinDraft draft, string caseId)
    {
        var suites = Suites.Store.Suites();
        var newest = suites.OrderByDescending(suite => suite.Updated).ThenBy(suite => suite.Id, StringComparer.Ordinal).FirstOrDefault()?.Id;
        var options = new List<PinSuiteOption>(suites.Count);
        foreach (var suite in suites)
        {
            var cases = Suites.Store.Cases(suite.Id).Count;
            var label = $"{suite.Name} · {cases.ToString(CultureInfo.InvariantCulture)} case{(cases == 1 ? "" : "s")}";
            try
            {
                var (before, after) = Suites.EstimateWith(suite, draft.ToCase(caseId, suite.Id));
                var (pane, over, pauses) = PinText.Pane(suite, before, after ?? before);
                options.Add(new PinSuiteOption(suite.Id, label, pane, over, pauses, suite.Id == newest));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                options.Add(new PinSuiteOption(suite.Id, label, "Its worst case can't be worked out with this case: " + error.Message, false, false, suite.Id == newest));
            }
        }
        return view with { Suites = options, NewSuiteOffered = true, NewSuiteSelected = options.Count == 0 };
    }

    private PinSuiteOption Fixed(Suite suite)
    {
        var cases = Suites.Store.Cases(suite.Id);
        var estimate = Suites.Estimate(suite, cases);
        var targets = suite.Targets.Count;
        var schedule = suite.Schedule switch
        {
            { Enabled: true, Paused: not null } active => ", " + char.ToLowerInvariant(active.Label[0]) + active.Label[1..] + " (paused)",
            { Enabled: true } active => ", " + char.ToLowerInvariant(active.Label[0]) + active.Label[1..],
            _ => ", by hand only",
        };
        var pane = targets == 0 ? "Has no targets yet." : $"Runs on {targets} target{(targets == 1 ? "" : "s")} with k {suite.K}{schedule}. Its metered worst case is "
            + Drift.DriftText.Money(estimate.WorstMicros) + (suite.CapMicros is { } cap ? $", against a {Drift.DriftText.Money(cap)} cap." : ".");
        return new PinSuiteOption(suite.Id, $"{suite.Name} · {cases.Count.ToString(CultureInfo.InvariantCulture)} case{(cases.Count == 1 ? "" : "s")}", pane,
            false, false, true);
    }

    private TestCaseModalView Request(TestCaseModalView view, PinDraft draft, RunSpec spec)
    {
        var catalog = services.GetService<IModelCatalog>();
        var provider = catalog?.FindProvider(spec.Provider);
        var logo = provider?.LogoKey is { } key ? ProviderLogos.KeyUrl(key) : ProviderLogos.Url(spec.Provider);
        var label = draft.Account is { } account ? services.GetService<IAccountStore>()?.Get(account)?.Label : null;
        var prompt = spec.Messages.LastOrDefault(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Text))?.Text;
        return view with
        {
            Provider = spec.Provider,
            Model = spec.Model,
            ProviderName = provider?.DisplayName ?? spec.Provider,
            LogoUrl = logo,
            OptionsLine = PinText.Options(spec),
            Prompt = prompt is null ? null : Cut(prompt, 600),
            MessagesLine = PinText.Messages(spec, draft.Reference?.InputTokens),
            RunId = draft.Origin.RunId,
            AccountLine = (label is { Length: > 0 } ? $"The account ({label}) is not part of the case" : "The account is not part of the case")
                + ": each suite target picks its own provider, model and account. API keys and tokens are never stored.",
        };
    }

    private static TestCaseModalView Reference(TestCaseModalView view, TestCaseReference reference)
    {
        var text = reference.Text ?? "";
        var shown = text.Length <= PinText.ReferenceShown ? text : text[..PinText.ReferenceShown];
        string? more = null;
        if (text.Length > PinText.ReferenceShown)
            more = text.Length <= PinText.ReferenceMost ? text[PinText.ReferenceShown..] : text[PinText.ReferenceShown..PinText.ReferenceMost] + "…";
        var calls = reference.ToAnswer().ToolCalls.Select(call => call.Name + (call.Arguments is { } arguments ? " " + Cut(arguments.ToJsonString(), 200) : "")).ToList();
        return view with { ReferenceLine = PinText.Reference(reference), ReferenceText = shown, ReferenceMore = more, ReferenceCalls = calls };
    }

    // ----- checks -----

    /// <summary>A check from the modal's quick Add check panel: <c>contains</c> (the text), <c>regex</c> (the pattern), <c>json-schema</c> (a
    /// schema, or nothing for "a JSON object"), <c>tool</c> (the tool's name) or <c>limits</c> (<c>$0.03 30 s</c>). A judge needs the full
    /// check editor on the suite page.</summary>
    /// <exception cref="ArgumentException">It is not a check; the message says why.</exception>
    public Check QuickCheck(string? kind, string? value)
    {
        var parsed = EvalJson.FromWire<CheckKind>(kind?.Trim()) ?? throw new ArgumentException("Choose the kind of check.");
        var check = parsed switch
        {
            CheckKind.Contains => new Check { Kind = parsed, Text = value },
            CheckKind.Regex => new Check { Kind = parsed, Pattern = value },
            CheckKind.JsonSchema => new Check { Kind = parsed, Schema = Schema(value) },
            CheckKind.Tool => new Check { Kind = parsed, Tool = value?.Trim(), Calls = ToolCallCount.AtLeastOnce },
            CheckKind.Limits => Limits(value),
            _ => throw new ArgumentException("Set up an LLM judge in the full check editor on the suite page, after you save."),
        };
        return CheckRunner.Validate(check);
    }

    /// <summary>The row of <paramref name="check"/> for <paramref name="target"/>'s modal: what it makes of the reference output.</summary>
    /// <exception cref="KeyNotFoundException">The target does not exist.</exception>
    /// <exception cref="PinRefusedException">It cannot be pinned.</exception>
    public async Task<PinCheckRow> CheckRowAsync(PinTarget target, Check check, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        var draft = await DraftAsync(target, cancellationToken).ConfigureAwait(false);
        return PinText.Row(check, draft.Reference);
    }

    private static JsonObject Schema(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new JsonObject { ["type"] = "object" };
        try
        {
            return JsonNode.Parse(value) as JsonObject ?? throw new ArgumentException("A JSON schema is an object, such as {\"type\":\"object\"}.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The schema isn't valid JSON: " + error.Message);
        }
    }

    /// <summary>A cost and latency check from text such as <c>$0.03 30 s</c>, <c>$0.5</c>, <c>500 ms</c> or <c>2 min</c>.</summary>
    /// <exception cref="ArgumentException">Neither a cost nor a time is in it.</exception>
    public static Check Limits(string? value)
    {
        var text = value ?? "";
        long? cost = null;
        long? latency = null;
        if (CostPattern().Match(text) is { Success: true } money)
        {
            cost = (long)Math.Round(decimal.Parse(money.Groups[1].Value, CultureInfo.InvariantCulture) * 1_000_000m);
            text = text.Remove(money.Index, money.Length);
        }
        if (TimePattern().Match(text) is { Success: true } time)
        {
            var amount = decimal.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = time.Groups[2].Value.ToLowerInvariant();
            latency = (long)Math.Round(amount * (unit.StartsWith("ms", StringComparison.Ordinal) ? 1m : unit.StartsWith('m') ? 60_000m : 1000m));
        }
        if (cost is null && latency is null) throw new ArgumentException("Write a cost, a time or both, such as $0.03 30 s.");
        return new Check { Kind = CheckKind.Limits, MaxCostMicros = cost, MaxLatencyMs = latency };
    }

    // ----- saving -----

    /// <summary>Saves <paramref name="request"/>: a pinned case in its suite (made first for New suite…), a case added to a suite, or an
    /// edited case. A retry with the same case id (and new suite id) saves once.</summary>
    /// <exception cref="KeyNotFoundException">The target or suite does not exist.</exception>
    /// <exception cref="PinRefusedException">It cannot be pinned.</exception>
    /// <exception cref="ArgumentException">Something is not valid; the message says what.</exception>
    public async Task<PinSaved> SaveAsync(PinSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var draft = await DraftAsync(request.Target, cancellationToken).ConfigureAwait(false);
        var name = request.Name is null ? draft.Name : request.Name.Trim();
        var tags = request.Tags is null ? draft.Tags : EvalTags.ParseList(request.Tags);
        var checks = request.Checks ?? draft.Checks;
        var now = Time.GetUtcNow();
        switch (draft.Mode)
        {
            case PinMode.Edit:
                if (request.CaseId != draft.Existing!.Id) throw new ArgumentException("The case id is not the case being edited.");
                var edited = Suites.UpdateCase(draft.Existing.Id, current => current with { Name = name, Tags = tags, Checks = checks });
                var home = Suites.RequireSuite(edited.SuiteId);
                return new PinSaved(edited, false, home, false, $"Saved the changes to {edited.Name}.");
            case PinMode.Add:
                var suite = draft.Suite!;
                var first = suite.Targets[0];
                if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Write the user message the case asks.");
                var spec = new RunSpec
                {
                    Provider = first.Provider, Model = first.Model, Thinking = first.Thinking, Temperature = first.Temperature, ServiceTier = first.ServiceTier,
                    SystemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt,
                    Messages = [new RunMessage { Role = "user", Text = request.Prompt }],
                    MaxTokens = request.MaxTokens,
                };
                var added = new TestCase
                {
                    Id = request.CaseId, SuiteId = suite.Id, Name = name, Tags = tags, Spec = spec, Checks = checks, Origin = new TestCaseOrigin(PinSource.Suite),
                };
                var (addedCase, addedNow) = Suites.CreateCase(added);
                var after = Suites.RequireSuite(suite.Id);
                return new PinSaved(addedCase, addedNow, after, false, PinText.Saved(after, false, now));
        }
        var testCase = draft with { Name = name, Tags = tags, Checks = checks };
        Suite target;
        var made = false;
        if (request.SuiteId is { } suiteId)
        {
            target = Suites.RequireSuite(suiteId);
        }
        else
        {
            var suiteName = request.NewSuiteName?.Trim() ?? "";
            if (suiteName.Length == 0) throw new ArgumentException("Give the new suite a name.");
            var newId = EvalIds.Require(EvalIds.Suite, request.NewSuiteId, "new suite");
            // Checked before the suite is made, so a case that can't be saved leaves no empty suite behind.
            CheckRunner.Validate(testCase.ToCase(request.CaseId, newId));
            var spec = testCase.Spec!;
            (target, made) = Suites.CreateSuite(new Suite
            {
                Id = newId,
                Name = suiteName,
                Targets = [new SuiteTarget(spec.Provider, spec.Model, draft.Account, spec.Thinking, spec.Temperature, spec.ServiceTier)],
            });
        }
        var (saved, created) = Suites.CreateCase(testCase.ToCase(request.CaseId, target.Id));
        var stored = Suites.RequireSuite(target.Id);
        return new PinSaved(saved, created, stored, made, PinText.Saved(stored, made, now));
    }

    // ----- helpers -----

    private static RunSpec CaseSpec(RunSpec spec)
    {
        try
        {
            return TestCase.CaseSpec(spec);
        }
        catch (ArgumentException error)
        {
            throw new PinRefusedException("This request can't be a test case: " + error.Message);
        }
    }

    /// <summary>The reference as a case keeps it: without the Spend Guard's field on the message, checked.</summary>
    private static TestCaseReference Reference(TestCaseReference reference)
    {
        var output = reference.Output?.DeepClone();
        if (output is JsonObject message) message.Remove(PiSharp.WebAgent.Spend.GuardedDurableModels.AnswerField);
        try
        {
            return (reference with { Output = output }).Normalize();
        }
        catch (ArgumentException error)
        {
            throw new PinRefusedException("This answer can't be a reference output: " + error.Message);
        }
    }

    private RunRecord? FindRun(string chatId, ChatEntryMeta meta)
    {
        if (meta.Time is not { } time) return null;
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)time);
        var page = Runs.List(new RunQuery { Source = "chat", From = at.AddHours(-1), To = at.AddHours(1), Limit = RunQuery.MaxLimit });
        var row = page.Items.FirstOrDefault(item => item.Session == chatId && item.Kind == RunKind.Call && Matches(meta, item));
        return row is null ? null : Runs.Get(row.Id);
    }

    private static bool Matches(ChatEntryMeta meta, RunRecord run)
    {
        if (meta.SpendOp is { } op && run.SpendOp is { } recorded) return op == recorded;
        return meta.Provider == run.Provider && meta.Model == run.Model && meta.HasUsage && meta.Input == run.Usage.Input && meta.Output == run.Usage.Output
            && meta.Time is { } time && Math.Abs(time - run.Created.ToUnixTimeMilliseconds()) <= TimeSpan.FromMinutes(30).TotalMilliseconds;
    }

    private static int IndexOf(IReadOnlyList<JsonObject> entries, long entryId)
    {
        for (var index = 0; index < entries.Count; index++)
            if (ChatDisplay.Long(entries[index], "id") == entryId) return index;
        return -1;
    }

    private static string? OptionText(JsonObject? options, string key) =>
        options?[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string Line(string text, int most)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        while (line.Contains("  ", StringComparison.Ordinal)) line = line.Replace("  ", " ", StringComparison.Ordinal);
        line = new string([.. line.Where(character => !char.IsControl(character))]);
        return line.Length <= most ? line : line[..(most - 1)].TrimEnd() + "…";
    }

    private static string Cut(string text, int most) => text.Length <= most ? text : text[..most] + "…";

    [GeneratedRegex(@"\$\s*(\d+(?:\.\d+)?)")]
    private static partial Regex CostPattern();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(ms|milliseconds?|s|secs?|seconds?|m|mins?|minutes?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();
}
