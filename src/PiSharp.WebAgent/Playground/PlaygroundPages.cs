// Decision 0011 (run W4, R1): what the Playground page (GET /playground) and its endpoints show. The page opens on a draft: the run named
// by ?run= (its request as runs.db stored it, and its output: live while it runs, so a tab closed mid-run and opened again reconnects to
// it), else ?provider=&model=, else the default model (Settings › Defaults, as a new chat's). The draft is planned and estimated by the run
// engine and the Spend Guard themselves, so the page shows what a run would really send and cost. The account is resolved as the engine
// resolves it (none offline, else the chosen account or the provider's active one) and shown by its label only, never a credential.
using System.Text.Json.Nodes;
using PiSharp.Tools.Files;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Playground;

/// <summary>The model of <c>Views/Playground/Index.cshtml</c>.</summary>
public sealed record PlaygroundPage
{
    /// <summary>The request options.</summary>
    public required RequestConfigView Config { get; init; }

    /// <summary>The messages.</summary>
    public required MessageListView Messages { get; init; }

    /// <summary>The output pane.</summary>
    public required RunOutputView Output { get; init; }

    /// <summary>The estimate by Run.</summary>
    public RunEstimateView Estimate { get; init; } = RunEstimateView.None;

    /// <summary>The tool editor's templates.</summary>
    public ToolEditorView Tools { get; init; } = new();

    /// <summary>The run's error details, or null.</summary>
    public ErrorDetailsView? Error { get; init; }

    /// <summary>A notice above the panes (a run that is gone, a run whose messages live in a chat), or null.</summary>
    public string? Notice { get; init; }

    /// <summary>The model's display name.</summary>
    public string ModelName { get; init; } = "";

    /// <summary>The provider's display name.</summary>
    public string ProviderName { get; init; } = "";

    /// <summary>Whether the model is known (Run is possible).</summary>
    public bool Known => Config.Capabilities is not null;
}

/// <summary>The Playground's answer for one draft (POST /api/playground/estimate): the estimate chip and the request's input tokens.</summary>
/// <param name="Valid">Whether Run would be accepted: a valid run whose options the model takes.</param>
/// <param name="Message">Why not, or the refusal of options the model does not take, or null.</param>
/// <param name="Estimate">The estimate.</param>
/// <param name="Tokens">The request's input tokens as the Spend Guard counts them (bytes ÷ 3 × 1.1), or null when not valid.</param>
/// <param name="Parts">The tokens of the system prompt, the tools and each message, in the editor's order.</param>
/// <param name="Refused">The options the model does not take that the draft sets.</param>
public sealed record PlaygroundDraftAnswer(bool Valid, string? Message, RunEstimateView Estimate, double? Tokens, PlaygroundTokenParts? Parts,
    IReadOnlyList<RunOptionSupport> Refused);

/// <summary>The input tokens of a request's parts.</summary>
/// <param name="System">The system prompt.</param>
/// <param name="Tools">The tool definitions.</param>
/// <param name="Messages">Each message after the system prompt.</param>
public sealed record PlaygroundTokenParts(double System, double Tools, IReadOnlyList<double> Messages);

/// <summary>Builds the Playground's page and answers.</summary>
public sealed class PlaygroundPages(IServiceProvider services, ILogger<PlaygroundPages>? log = null)
{
    /// <summary>The verb of the Playground's estimate.</summary>
    public const string Verb = "Run";

    private RunEngine Engine => services.GetRequiredService<RunEngine>();

    private IModelCatalog? Catalog => services.GetService<IModelCatalog>();

    /// <summary>The page for <c>?run=</c>, else <c>?provider=&amp;model=</c>, else the default model.</summary>
    public async Task<PlaygroundPage> PageAsync(string? runId, string? provider, string? model, CancellationToken cancellationToken = default)
    {
        string? notice = null;
        RunRecord? run = null;
        RunSpec? draft = null;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            run = services.GetService<RunStore>()?.Get(runId.Trim());
            // W5 integration fix 1: a blind round's run would name its model before the vote (RunBlinds).
            if (run is not null && RunBlinds.Of(services).Hidden(run) is not null)
            {
                run = null;
                notice = "That run is part of a blind round that has no vote yet, so it isn't loaded here. Vote on the round first. The Playground opened on the default model.";
            }
            else if (run is null) notice = $"There is no run {runId.Trim()}; it may have been deleted. The Playground opened on the default model.";
            else
            {
                draft = FromRun(run);
                if (draft is null) notice = run.MessagesRef is { } where && where.StartsWith("chat:", StringComparison.Ordinal)
                    ? "This run was a chat's request: its messages are in the chat, so only its output is shown here."
                    : "This run's request could not be loaded, so only its output is shown here.";
            }
        }
        if (draft is null && !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(model))
        {
            if (Engine.Capabilities(provider, model) is not null) draft = Empty(provider.Trim(), model.Trim());
            else notice ??= $"There is no model {provider.Trim()}/{model.Trim()}; the Playground opened on the default model.";
        }
        draft ??= Default();
        var capabilities = Engine.Capabilities(draft.Provider, draft.Model);
        var auth = await AuthAsync(draft, capabilities, cancellationToken).ConfigureAwait(false);
        var estimate = capabilities is null ? RunEstimateView.None : (await DraftAsync(draft, cancellationToken).ConfigureAwait(false)).Estimate;
        var catalogModel = Catalog?.FindModel(draft.Provider, draft.Model);
        var output = Output(run, draft);
        return new PlaygroundPage
        {
            Config = new RequestConfigView { Draft = draft, Capabilities = capabilities, Auth = auth, Sampling = Sampling(draft.Provider, draft.Model) },
            Messages = new MessageListView
            {
                SystemPrompt = draft.SystemPrompt,
                Messages = draft.Messages,
                Images = capabilities?.Images == true,
                ContextWindow = capabilities?.ContextWindow,
            },
            Output = output,
            Estimate = estimate,
            Tools = new ToolEditorView { Templates = Templates(), Strict = true },
            Error = output.State == "error" ? ErrorDetails(run!) : null,
            Notice = notice,
            ModelName = catalogModel?.Name ?? capabilities?.Name ?? draft.Model,
            ProviderName = auth?.ProviderName ?? draft.Provider,
        };
    }

    /// <summary>The output pane of run <paramref name="id"/>, or null when there is no such run.</summary>
    public RunOutputView? Output(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var run = services.GetService<RunStore>()?.Get(id);
        return run is null || RunBlinds.Of(services).Hidden(run) is not null ? null : Output(run, FromRun(run));
    }

    /// <summary>The error details of run <paramref name="id"/>, or null when there is no such run.</summary>
    public ErrorDetailsView? ErrorDetails(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return services.GetService<RunStore>()?.Get(id) is { } run && RunBlinds.Of(services).Hidden(run) is null ? ErrorDetails(run) : null;
    }

    /// <summary>The estimate and input tokens of <paramref name="spec"/>; nothing is reserved or sent.</summary>
    public async Task<PlaygroundDraftAnswer> DraftAsync(RunSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        RunRequestPlan plan;
        try
        {
            plan = Engine.Plan(spec);
        }
        catch (ArgumentException error)
        {
            return new PlaygroundDraftAnswer(false, error.Message, RunEstimateView.None, null, null, []);
        }
        var bytes = CostEstimator.InputBytes(plan.Messages);
        var parts = Parts(plan);
        RunEstimateView estimate;
        try
        {
            estimate = await EstimateAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The estimate is advice: a failure leaves the chip out, and Run still asks the guard for real.
            log?.LogWarning(error, "The Playground estimate of {Provider}/{Model} failed.", plan.Model.Provider, plan.Model.Id);
            estimate = RunEstimateView.None;
        }
        return new PlaygroundDraftAnswer(plan.Allowed, plan.RefusalMessage, estimate, CostEstimator.InputTokens(bytes), parts, plan.Refused);
    }

    /// <summary>View code of <paramref name="spec"/>, with "Open last run in inspector" when <paramref name="lastRun"/> names a run runs.db
    /// has (the run the Playground shows); the inspector's own View code passes none, so the link stays hidden there.</summary>
    /// <exception cref="ArgumentException">The spec is not valid, or the model is unknown.</exception>
    public ViewCodeView Code(RunSpec spec, string origin, string? lastRun = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var plan = Engine.Plan(spec);
        var code = RequestCode.Generate(plan, origin, BaseUrl(plan));
        return !string.IsNullOrWhiteSpace(lastRun) && services.GetService<RunStore>()?.Get(lastRun.Trim()) is { } run
            ? code with { InspectorUrl = "/runs/" + Uri.EscapeDataString(run.Id) }
            : code;
    }

    /// <summary>What Model &amp; auth shows for <paramref name="provider"/>/<paramref name="model"/>: its capabilities and account, or null.</summary>
    public async Task<(RunCapabilities Capabilities, RequestAuthView? Auth)?> ModelAsync(string provider, string model, string? account,
        CancellationToken cancellationToken = default)
    {
        if (Engine.Capabilities(provider, model) is not { } capabilities) return null;
        var auth = await AuthAsync(Empty(provider, model) with { Account = account }, capabilities, cancellationToken).ConfigureAwait(false);
        return (capabilities, auth);
    }

    /// <summary>The catalog sampling values of <paramref name="provider"/>/<paramref name="model"/> (Sampling's read-only samplingParams row,
    /// W5 L4), or <see cref="RequestSamplingView.None"/> when it is not a model PiSharp knows.</summary>
    public RequestSamplingView Sampling(string provider, string model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        return RequestSamplingView.Of(Engine.Models.GetModel(provider.Trim(), model.Trim()));
    }

    /// <summary>The run's request as the Playground edits it (its options and messages as runs.db stored them; the group, parent, source and
    /// title left out), or null when the run did not store its messages (a chat's call) or they are not a run spec.</summary>
    public static RunSpec? FromRun(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Messages is not JsonObject messages || run.Options is not JsonObject options) return null;
        var json = (JsonObject)options.DeepClone();
        foreach (var key in new[] { "applied", "inputBytes", "toolNames", "groupId", "parentId", "source", "title" }) json.Remove(key);
        foreach (var (key, value) in messages) json[key] = value?.DeepClone();
        json["provider"] ??= run.Provider;
        json["model"] ??= run.Model;
        try
        {
            return RunSpec.Parse(json);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The tool editor's templates: PiSharp's own read and grep definitions, and two common examples.</summary>
    public static IReadOnlyList<ToolTemplate> Templates()
    {
        var templates = new List<ToolTemplate>
        {
            new("weather", "weather", "bi-cloud-sun", new RunTool("get_weather",
                "Get the current weather for a city. Call this when the user asks about weather conditions or temperature.",
                JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string","description":"City and country, e.g. \"Oslo, Norway\""},"unit":{"type":"string","enum":["celsius","fahrenheit"],"default":"celsius"}},"required":["location"],"additionalProperties":false}""")!.AsObject())),
            new("calc", "calculator", "bi-calculator", new RunTool("calculate",
                "Evaluate an arithmetic expression and return the result.",
                JsonNode.Parse("""{"type":"object","properties":{"expression":{"type":"string","description":"The expression, e.g. \"(12 + 30) * 2\""}},"required":["expression"],"additionalProperties":false}""")!.AsObject())),
        };
        if (Declaration(ReadWriteTools.ReadDeclaration.ToString()) is { } read) templates.Add(new("read", "PiSharp read", "bi-file-earmark-code", read));
        if (GrepDeclaration() is { } grep) templates.Add(new("grep", "PiSharp grep", "bi-search", grep));
        return templates;
    }

    private static RunTool? GrepDeclaration()
    {
        try
        {
            var root = Path.GetTempPath();
            return Declaration(new PiGrepTool(root, root, _ => ValueTask.FromResult<string?>(null)).Declaration.ToString());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static RunTool? Declaration(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject declaration || declaration["name"] is not JsonValue name || !name.TryGetValue<string>(out var text)) return null;
        var description = declaration["description"] is JsonValue value && value.TryGetValue<string>(out var said) ? said : null;
        return new RunTool(text, description, declaration["parameters"]?.DeepClone() as JsonObject);
    }

    private RunOutputView Output(RunRecord? run, RunSpec? draft)
    {
        if (run is null) return new RunOutputView();
        var live = services.GetService<RunLive>()?.Find(run.Id);
        IReadOnlyList<RunEventRecord> events;
        var kept = true;
        if (live is { Ended: false })
            events = [.. live.Items.Select(item => new RunEventRecord(item.Seq, item.AtMs, item.Event))];
        else
        {
            events = services.GetService<RunStore>()?.Events(run.Id, 0, RunOutputView.MaxEventRows * 4) ?? [];
            kept = events.Count > 0 || run.Status == RunStatus.Running;
        }
        var thinking = run.Options?["thinking"] is JsonValue level && level.TryGetValue<string>(out var text) && text != "off" ? text : null;
        return new RunOutputView
        {
            Run = run,
            Live = live is { Ended: false },
            Events = events,
            EventsKept = kept,
            ModelName = Catalog?.FindModel(run.Provider, run.Model)?.Name,
            Thinking = thinking,
        };
    }

    private ErrorDetailsView ErrorDetails(RunRecord run)
    {
        var spend = SpendErrors.Parse(run.Error);
        var kind = run.Status switch
        {
            RunStatus.Aborted => "Cancelled",
            RunStatus.Interrupted => "Interrupted",
            _ when spend is not null => "Spend Guard",
            _ => "Error",
        };
        string? status = null;
        string? body = null;
        string? note = null;
        if (run.RawState == RunRawState.Stored && services.GetService<RunStore>() is { } store)
        {
            var raw = store.Raw(run.Id);
            if (raw.LastOrDefault(item => item.Kind == "response") is { } response && JsonNode.Parse(response.Data) is JsonObject head)
                status = $"{head["status"]} {head["reason"]}".Trim();
            var lines = raw.Where(item => item.Kind is not ("request" or "response" or "timing")).TakeLast(20).Select(item => item.Data).ToList();
            body = lines.Count > 0 ? string.Join("\n", lines) : null;
            if (body is { Length: > 8_000 }) body = body[^8_000..];
        }
        else note = run.RawState == RunRawState.Expired
            ? "This run's raw payloads were removed after their retention."
            : "Raw payloads weren't stored for this run. Turn them on in Settings › Data to keep the provider's response.";
        var when = (run.Finished ?? run.Updated).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return new ErrorDetailsView
        {
            Title = run.Status == RunStatus.Interrupted ? "Run interrupted" : "Request failed",
            Subtitle = $"{run.Provider}/{run.Model} · {when} · run {run.Id}",
            Kind = kind,
            HttpStatus = status,
            StopReason = run.StopReason ?? run.Status,
            Message = spend?.Text ?? run.Error ?? "The run ended without an answer.",
            RawBody = body,
            RawNote = body is null ? note ?? "The provider sent no response body." : null,
            InspectorUrl = "/runs/" + Uri.EscapeDataString(run.Id),
        };
    }

    private async Task<RunEstimateView> EstimateAsync(RunRequestPlan plan, CancellationToken cancellationToken)
    {
        var account = await AccountAsync(plan.Spec, plan.Model.Api == "faux", cancellationToken).ConfigureAwait(false);
        var call = new SpendCall
        {
            Provider = plan.Model.Provider,
            Model = plan.Model.Id,
            Account = account?.Id,
            Source = plan.Spec.SpendSource,
            Run = "preflight-" + Guid.NewGuid().ToString("N"),
            InputBytes = CostEstimator.InputBytes(plan.Messages),
            MaxTokens = plan.Spec.MaxTokens,
        };
        SpendDecision decision;
        if (services.GetService<GuardedRunExecutor>() is { } executor)
            decision = await executor.Guard.PreflightAsync(call, withApprovals: false, cancellationToken).ConfigureAwait(false);
        else if (services.GetService<SpendPages>() is { } pages)
            decision = await pages.PreflightAsync(call, cancellationToken).ConfigureAwait(false);
        else
            return RunEstimateView.None;
        var provider = Catalog?.FindProvider(plan.Model.Provider)?.DisplayName ?? plan.Model.Provider;
        var who = account?.Label is { Length: > 0 } label ? $"{provider} · {label}" : provider;
        return RunEstimate.Build(decision, who, services.GetService<SpendPages>() is { } named ? named.CapName : null, Verb);
    }

    private async Task<ChatAccount?> AccountAsync(RunSpec spec, bool offline, CancellationToken cancellationToken)
    {
        if (offline || services.GetService<IChatAccounts>() is not { } accounts) return null;
        try
        {
            return spec.Account is { } id
                ? await accounts.FindAsync(spec.Provider, id, cancellationToken).ConfigureAwait(false)
                : await accounts.BindAsync(spec.Provider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.LogDebug(error, "The account of {Provider} could not be read for the Playground.", spec.Provider);
            return null;
        }
    }

    private async Task<RequestAuthView?> AuthAsync(RunSpec draft, RunCapabilities? capabilities, CancellationToken cancellationToken)
    {
        var provider = Catalog?.FindProvider(draft.Provider);
        var offline = capabilities?.Offline == true;
        var account = await AccountAsync(draft, offline, cancellationToken).ConfigureAwait(false);
        // W4 fix 2: the provider's accounts, for Model & auth's account choice (ids and labels only); a preset or a run may name one.
        var accounts = RequestAccounts.List(services, draft.Provider, offline, log);
        var active = draft.Account is null ? account : await AccountAsync(draft with { Account = null }, offline, cancellationToken).ConfigureAwait(false);
        return new RequestAuthView(provider?.DisplayName ?? draft.Provider, account?.Label, provider?.Billing ?? BillingKind.Unknown, offline)
        {
            Chosen = offline ? null : draft.Account,
            Accounts = accounts,
            ActiveLabel = active?.Label,
        };
    }

    private string? BaseUrl(RunRequestPlan plan) => string.IsNullOrWhiteSpace(plan.Model.BaseUrl) ? null : plan.Model.BaseUrl;

    private static PlaygroundTokenParts Parts(RunRequestPlan plan)
    {
        var spec = plan.Spec;
        var system = spec.SystemPrompt is { } prompt ? CostEstimator.InputTokens(System.Text.Encoding.UTF8.GetByteCount(prompt)) : 0;
        var tools = spec.Tools is { Count: > 0 } list
            ? CostEstimator.InputTokens(System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(list, PiSharp.WebAgent.Data.SettingsStore.JsonOptions)))
            : 0;
        var offset = spec.SystemPrompt is not null || spec.Tools is { Count: > 0 } ? 1 : 0;
        var messages = plan.Messages.Skip(offset).Select(message => CostEstimator.InputTokens(System.Text.Encoding.UTF8.GetByteCount(message.ToJson()))).ToList();
        return new PlaygroundTokenParts(system, tools, messages);
    }

    private RunSpec Default()
    {
        try
        {
            if (services.GetService<ChatService>()?.NewChat() is { Provider: { } provider, Model: { } model } && Engine.Capabilities(provider, model) is not null)
                return Empty(provider, model);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.LogDebug(error, "The default model could not be read for the Playground.");
        }
        var first = Catalog?.Providers.SelectMany(provider => Catalog.Models(provider.Id)).FirstOrDefault(model => Engine.Capabilities(model.Provider, model.Id) is not null);
        return first is null ? Empty("", "") : Empty(first.Provider, first.Id);
    }

    private static RunSpec Empty(string provider, string model) => new()
    {
        Provider = provider,
        Model = model,
        Messages = [new RunMessage { Role = "user", Text = "" }],
    };
}
