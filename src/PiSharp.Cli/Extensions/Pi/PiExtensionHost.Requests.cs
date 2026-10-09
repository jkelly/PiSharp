// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (bindExtensions: the actions,
// context actions and command context actions the session gives the runner: sendMessage, sendUserMessage, appendEntry, setSessionName,
// getSessionName, setLabel, getActiveTools, getAllTools, setActiveTools, getCommands, setModel, getThinkingLevel, setThinkingLevel,
// isIdle, abort, hasPendingMessages, getContextUsage, compact, getSystemPrompt, waitForIdle, newSession, fork, navigateTree,
// switchSession), packages/coding-agent/src/core/session-manager.ts (the ReadonlySessionManager reads) and
// packages/coding-agent/src/core/extensions/runner.ts (createContext, withUIPrompt).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Compatibility.Node.Pi;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Tree;

namespace PiSharp.Cli.Extensions.Pi;

internal sealed partial class PiExtensionHost
{
    private ReplaceableAgentSession? _owner;
    private NativeExtensionActivation? _activation;
    private NativeExistingSessionRegistrationActions? _actions;
    private readonly ConcurrentDictionary<string, (ExtensionShellOutputCallback Callback, Task Chain)> _shellOutputs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (IExtensionCustomComponentUi Ui, ExtensionCustomComponentIdentity Identity)> _components = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _statuses = new(StringComparer.Ordinal);

    /// <summary>The session the extensions act on (agent-session.ts bindExtensions).</summary>
    internal void AttachSession(ReplaceableAgentSession owner)
    {
        _owner = owner;
        if (IsRunning) _ = Node.RequestAsync("bind", new JsonObject(), CancellationToken.None);
    }
    internal void BindActivation(NativeExtensionActivation activation, ExtensionRegistry registry, NativeExistingSessionRegistrationActions actions,
        NativeExtensionContextFacadeHost facadeHost)
    {
        _activation = activation; _actions = actions; _ = facadeHost;
        // pi.events: the Node extensions and the native extensions of the session share one event bus.
        _eventBus = registry.SharedEventBus;
        _eventBus.Tap = (channel, data) =>
        {
            if (_deliveringFromNode) return;
            JsonNode? json;
            try { json = data switch { null => null, JsonData value => JsonNode.Parse(value.ToString()), JsonElement element => JsonNode.Parse(element.GetRawText()),
                JsonNode node => node.DeepClone(), _ => JsonSerializer.SerializeToNode(data, data.GetType()) }; }
            catch (Exception error) when (error is NotSupportedException or JsonException or InvalidOperationException) { return; }
            if (IsRunning) _ = Node.NotifyAsync("events.deliver", new JsonObject { ["channel"] = channel, ["data"] = json });
        };
    }

    private PiSharp.Extensions.Runtime.ExtensionEventBus? _eventBus;
    private ImmutableArray<PiNodeOwner> _owners = [];
    private readonly SemaphoreSlim _sync = new(1, 1);

    private readonly object _commandSync = new();
    private void SyncCommands()
    {
        lock (_commandSync)
        {
            ImmutableArray<PiNodeOwner> owners;
            lock (_extensions) owners = _owners;
            if (owners.IsEmpty) return;
            var names = CommandInvocationNames();
            try
            {
                foreach (var owner in owners) owner.RetireStaleCommands(names);
                foreach (var owner in owners) owner.RegisterMissingCommands(names);
            }
            catch (Exception error) when (error is InvalidOperationException or PiSharp.Extensions.Runtime.ExtensionRegistrationException) { }
        }
    }

    /// <summary>Brings the session's registrations in line with the extensions' current ones: commands of every extension (their
    /// name:N invocation names may change), then the tools of <paramref name="index"/> (all extensions with -1) in the live catalog.</summary>
    internal Task SyncRegistrationsAsync(int index, bool force)
    {
        // Commands first, synchronously: the notification is processed before the callback that registered them returns.
        SyncCommands();
        // The catalog publication waits for the session's mutation boundary; it must not inherit a running input or lifecycle callback's
        // context (a /reload or session_start handler), so it runs detached and the session takes it at its next request.
        Task pending;
        using (ExecutionContext.SuppressFlow()) pending = Task.Run(() => SyncToolsAsync(index, force));
        lock (_pendingSyncs) { _pendingSyncs.RemoveAll(task => task.IsCompleted); _pendingSyncs.Add(pending); }
        return pending;
    }

    private readonly List<Task> _pendingSyncs = [];

    /// <summary>Waits (bounded) until the registrations the extensions made so far reached the session: a prompt sees a tool registered
    /// before it, as upstream's synchronous refreshTools gives.</summary>
    internal async Task WaitForRegistrationsAsync(CancellationToken token)
    {
        Task[] pending;
        lock (_pendingSyncs) pending = [.. _pendingSyncs.Where(task => !task.IsCompleted)];
        if (pending.Length == 0) return;
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private async Task SyncToolsAsync(int index, bool force)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            ImmutableArray<PiNodeOwner> owners;
            lock (_extensions) owners = _owners;
            if (owners.IsEmpty) return;
            if (_activation is { } activation && _owner is { } session)
                foreach (var owner in owners.Where(owner => index < 0 || owner.Index == index))
                    await owner.SyncToolsAsync(activation, session, force, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or PiSharp.Extensions.Runtime.ExtensionRegistrationException or
            PiSharp.CodingAgent.SessionRuntimeRegistryException or OperationCanceledException or ObjectDisposedException)
        {
            var path = Extensions.FirstOrDefault(extension => extension.Index == index)?.Path ?? "<runtime>";
            await ReportAsync(path, "register", error.Message).ConfigureAwait(false);
        }
        finally { _sync.Release(); }
    }
    [ThreadStatic] private static bool _deliveringFromNode;

    /// <summary>A Node extension's pi.events.emit: the native extensions' listeners receive the data as <see cref="JsonData"/>.</summary>
    private void DeliverFromNode(JsonElement parameters)
    {
        if (_eventBus is not { } bus) return;
        var data = parameters.TryGetProperty("data", out var value) && value.ValueKind != JsonValueKind.Null ? JsonData.Parse(value.GetRawText()) : null;
        _deliveringFromNode = true;
        try { bus.Emit(parameters.GetProperty("channel").GetString()!, data); }
        catch (Exception error) when (error is PiSharp.Extensions.ExtensionEventBusUnhandledErrorException or InvalidOperationException) { }
        finally { _deliveringFromNode = false; }
    }

    /// <summary>The extension statuses set with <c>ctx.ui.setStatus</c> (the footer reads them; IMPL-I).</summary>
    internal IReadOnlyDictionary<string, string?> Statuses => _statuses;

    private AgentSessionAttachment? Attached => _owner?.Current;
    private AgentSessionAttachment RequireAttached() => Attached ?? throw new InvalidOperationException("Extension runtime not initialized. Action methods cannot be called during extension loading.");

    // ----------------------------------------------------------------------------------------------------------------- peer

    public async ValueTask<JsonNode?> RequestAsync(PiNodeHostRequest request, CancellationToken token)
    {
        var p = request.Parameters;
        string Op() => p.GetProperty("op").GetString()!;
        JsonElement Args() => p.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array ? args : default;
        switch (request.Method)
        {
            case "pi.read": return PiRead(Op());
            case "pi.setActiveTools":
            {
                var names = p.GetProperty("toolNames").EnumerateArray().Select(name => name.GetString()!).ToImmutableArray();
                await RequireAttached().Session.SetActiveToolsAsync(names, token).ConfigureAwait(false);
                return null;
            }
            case "pi.setThinkingLevel":
                await RequireAttached().Session.ConfigureAsync(new SessionRuntimeUpdate(ThinkingLevel: p.GetProperty("level").GetString()), token).ConfigureAwait(false);
                return null;
            case "pi.setModel": return await SetModelAsync(p.GetProperty("model"), token).ConfigureAwait(false);
            case "pi.sendMessage": await SendMessageAsync(p, token).ConfigureAwait(false); return null;
            case "pi.sendUserMessage": await SendUserMessageAsync(p, token).ConfigureAwait(false); return null;
            case "ctx.read": return ContextRead(p, Op());
            case "session.read": return SessionRead(Op(), Args());
            case "ui.dialog": return await DialogAsync(p, Op(), Args(), token).ConfigureAwait(false);
            case "ui.custom": await OpenComponentAsync(p, token).ConfigureAwait(false); return null;
            case "ui.read": return UiRead(Op());
            case "ui.setTheme": return new JsonObject { ["success"] = false, ["error"] = "Theme switching from extensions is not available in this PiSharp host" };
            case "ctx.executeTool": return await ExecuteToolAsync(p, request, token).ConfigureAwait(false);
            case "ctx.compact":
                // Source ExtensionContext.compact(): abort, then the session's manual compaction; onComplete gets the CompactionResult.
                if (Compact is null) throw new NotSupportedException("ctx.compact() needs a session host with compaction (RPC, interactive)");
                return await Compact(p.TryGetProperty("customInstructions", out var instructions) && instructions.ValueKind == JsonValueKind.String
                    ? instructions.GetString() : null, token).ConfigureAwait(false);
            case "command.waitForIdle": await RequireAttached().Session.WaitForIdleAsync(token).ConfigureAwait(false); return null;
            case "command.session": return await SessionCommandAsync(p, token).ConfigureAwait(false);
            case "command.reload":
                // ctx.reload(): the mode's reload; the command's pi and ctx objects are stale afterwards.
                if (Reload is null) throw new NotSupportedException("ctx.reload() needs a session host that reloads (RPC, interactive)");
                await Reload(token).ConfigureAwait(false);
                return null;
            case "models.read": return await ModelsAsync(Op(), Args(), token).ConfigureAwait(false);
            case "models.call": return await ModelsAsync(Op(), Args(), token).ConfigureAwait(false);
            case "bridge.call": return await BridgeCallAsync(p, request, token).ConfigureAwait(false);
            case "autocomplete.base": return await AutocompleteBaseAsync(p, token).ConfigureAwait(false);
            case "editor.shortcut": return EditorShortcut?.Invoke(p.GetProperty("data").GetString() ?? "") ?? false;
            default: throw new NotSupportedException($"{request.Method} is not available in this PiSharp host");
        }
    }

    public async ValueTask NotifyAsync(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "pi.sendMessage": _ = Guard(SendMessageAsync(parameters, CancellationToken.None)); return;
            case "pi.sendUserMessage": _ = Guard(SendUserMessageAsync(parameters, CancellationToken.None)); return;
            case "pi.appendEntry":
                await RequireAttached().Session.AppendCustomEntryAsync(parameters.GetProperty("customType").GetString()!,
                    parameters.TryGetProperty("data", out var data) ? JsonData.Parse(data.GetRawText()) : null).ConfigureAwait(false);
                return;
            case "pi.setSessionName":
            {
                // Source appendSessionInfo: newlines become spaces and the name is trimmed.
                var name = System.Text.RegularExpressions.Regex.Replace(parameters.GetProperty("name").GetString() ?? "", "[\r\n]+", " ").Trim();
                var attached = RequireAttached();
                try { if (name.Length > 0) await attached.Session.SetSessionNameAsync(attached.Session.Snapshot.Log.Header.Id, name).ConfigureAwait(false); else throw new InvalidOperationException(); }
                catch (InvalidOperationException) { await attached.Session.AppendSessionInfoAsync(name).ConfigureAwait(false); }
                return;
            }
            case "pi.setLabel":
                await RequireAttached().Session.AppendLabelChangeAsync(parameters.GetProperty("entryId").GetString()!,
                    parameters.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null).ConfigureAwait(false);
                return;
            case "ui.publish": await PublishAsync(parameters).ConfigureAwait(false); return;
            case "ui.prompt": return; // The host's UI provider raises ui_prompt_start/ui_prompt_end around every dialog.
            case "ctx.action":
                if (parameters.GetProperty("op").GetString() == "abort") Attached?.Session.Abort();
                else if (parameters.GetProperty("op").GetString() == "shutdown") ShutdownRequested?.Invoke();
                return;
            case "component.invalidate":
                if (_components.TryGetValue(parameters.GetProperty("id").GetString()!, out var component))
                    await component.Ui.InvalidateCustomComponentAsync(component.Identity).ConfigureAwait(false);
                else ComponentInvalidated?.Invoke(parameters.GetProperty("id").GetString()!); // IMPL-I: widget, header and footer components
                return;
            case "component.done":
                if (_components.TryRemove(parameters.GetProperty("id").GetString()!, out var done))
                    await done.Ui.SignalCustomComponentDoneAsync(done.Identity).ConfigureAwait(false);
                return;
            case "userBash.data":
            {
                var callId = parameters.GetProperty("callId").GetString()!;
                var bytes = Convert.FromBase64String(parameters.GetProperty("data").GetString() ?? "");
                if (_shellOutputs.TryGetValue(callId, out var output))
                    _shellOutputs[callId] = (output.Callback, output.Chain.ContinueWith(_ => output.Callback(bytes).AsTask(), TaskScheduler.Default).Unwrap());
                return;
            }
            case "provider.register": lock (_providers) _providers.Add(JsonNode.Parse(parameters.GetRawText())!.AsObject()); RegistrationsChanged?.Invoke(); ProvidersChanged?.Invoke(); return;
            case "provider.unregister":
                lock (_providers) _providers.RemoveAll(item => item["name"]?.GetValue<string>() == parameters.GetProperty("name").GetString());
                RegistrationsChanged?.Invoke(); ProvidersChanged?.Invoke(); return;
            case "virtualModel.register": lock (_virtualModels) _virtualModels.Add(JsonNode.Parse(parameters.GetRawText())!.AsObject()); RegistrationsChanged?.Invoke(); ProvidersChanged?.Invoke(); return;
            case "virtualModel.unregister":
                lock (_virtualModels) _virtualModels.RemoveAll(item => item["definition"]?["provider"]?.GetValue<string>() == parameters.GetProperty("provider").GetString() &&
                    item["definition"]?["id"]?.GetValue<string>() == parameters.GetProperty("id").GetString());
                RegistrationsChanged?.Invoke(); ProvidersChanged?.Invoke(); return;
            case "mcp.register": lock (_mcpServers) _mcpServers.Add(JsonNode.Parse(parameters.GetRawText())!.AsObject()); return;
            case "mcp.unregister": lock (_mcpServers) _mcpServers.RemoveAll(item => item["name"]?.GetValue<string>() == parameters.GetProperty("name").GetString()); return;
            case "events.emit": DeliverFromNode(parameters); return;
            case "component.event": ComponentEvent?.Invoke(parameters.GetProperty("id").GetString()!, parameters.Clone()); return;
            case "registrations.changed":
            {
                var index = parameters.GetProperty("ext").GetInt32();
                lock (_extensions) foreach (var extension in _extensions.Where(item => item.Index == index))
                        extension.Descriptor = JsonNode.Parse(parameters.GetProperty("extension").GetRawText())!.AsObject();
                // Registrations made after the factory returned take effect in the running session (commands, tools, handlers).
                _ = SyncRegistrationsAsync(index, force: false);
                RegistrationsChanged?.Invoke();
                return;
            }
            default: return;
        }
    }

    /// <summary>The system prompt of the current run after the Node before_agent_start handlers.</summary>
    internal string? RunSystemPrompt { get; set; }

    /// <summary>ctx.isProjectTrusted(): the run's resolved project trust (null before it is resolved).</summary>
    internal bool? ProjectTrusted { get; set; }

    /// <summary>ctx.shutdown(): the mode's graceful shutdown.</summary>
    internal Action? ShutdownRequested { get; set; }

    /// <summary>A provider or virtual model was registered or unregistered after startup: the session re-reads its models.</summary>
    internal Action? ProvidersChanged { get; set; }

    /// <summary>The session's extension UI (RPC or terminal) for UI calls made after their callback returned.</summary>
    internal IExtensionUiProvider? UiProvider { get; set; }

    /// <summary>ctx.compact(): the mode's manual compaction (the RPC dispatcher's), returning the CompactionResult.</summary>
    internal Func<string?, CancellationToken, Task<JsonNode?>>? Compact { get; set; }

    private static async Task Guard(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning("Extension action failed: {0}", error.Message);
            if (Environment.GetEnvironmentVariable("PISHARP_DEBUG") == "1") Console.Error.WriteLine("Extension action failed: " + error);
        }
    }

    // ----------------------------------------------------------------------------------------------------------------- actions

    private async Task SendMessageAsync(JsonElement p, CancellationToken token)
    {
        var actions = _actions ?? throw new InvalidOperationException("Extension runtime not initialized.");
        var message = p.GetProperty("message");
        var options = p.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        var deliverAs = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("deliverAs", out var d) ? d.GetString() : null;
        // agent-session.ts sendCustomMessage: an idle session appends (and emits) the message at once. A command's own input admission
        // is still settling while its handler runs, so the append waits for that admission to finish.
        for (var attempt = 0; ; attempt++)
        {
            try { await Send().ConfigureAwait(false); return; }
            catch (Exception error) when (attempt < 1200 && InputBusy(error) && !token.IsCancellationRequested)
            { await Task.Delay(25, token).ConfigureAwait(false); }
        }

        static bool InputBusy(Exception? error) => error is not null &&
            (error is InvalidOperationException { Message: "Input admission is already processing." } || InputBusy(error.InnerException));
        ValueTask Send() => actions.SendMessageAsync(new ExtensionCustomMessage(message.GetProperty("customType").GetString() ?? "",
                message.TryGetProperty("content", out var content) ? JsonData.Parse(content.GetRawText()) : JsonData.Parse("\"\""),
                !message.TryGetProperty("display", out var display) || display.ValueKind != JsonValueKind.False,
                message.TryGetProperty("details", out var details) ? JsonData.Parse(details.GetRawText()) : null),
            new ExtensionMessageOptions(options.ValueKind == JsonValueKind.Object && options.TryGetProperty("triggerTurn", out var trigger) && trigger.ValueKind is JsonValueKind.True or JsonValueKind.False ? trigger.GetBoolean() : null,
                deliverAs switch { "followUp" => ExtensionMessageDelivery.FollowUp, "nextTurn" => ExtensionMessageDelivery.NextTurn, "steer" => ExtensionMessageDelivery.Steer, _ => null }),
            token);
    }

    private async Task SendUserMessageAsync(JsonElement p, CancellationToken token)
    {
        var actions = _actions ?? throw new InvalidOperationException("Extension runtime not initialized.");
        var options = p.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        var deliverAs = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("deliverAs", out var d) ? d.GetString() : null;
        await actions.SendUserMessageAsync(JsonData.Parse(p.GetProperty("content").GetRawText()),
            new ExtensionUserMessageOptions(deliverAs == "followUp" ? ExtensionMessageDelivery.FollowUp : deliverAs == "steer" ? ExtensionMessageDelivery.Steer : null,
                options.ValueKind == JsonValueKind.Object && options.TryGetProperty("expandPromptTemplates", out var expand) && expand.ValueKind == JsonValueKind.True ? true : null),
            token).ConfigureAwait(false);
    }

    private async Task<JsonNode?> SetModelAsync(JsonElement model, CancellationToken token)
    {
        var actions = _actions ?? throw new InvalidOperationException("Extension runtime not initialized.");
        var descriptor = new ModelDescriptor(model.GetProperty("id").GetString()!, model.TryGetProperty("api", out var api) ? api.GetString() ?? "" : "",
            model.GetProperty("provider").GetString()!);
        return await actions.SetModelAsync(descriptor, token).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------------------------------------------------- reads

    private JsonNode? PiRead(string op)
    {
        var attached = Attached;
        switch (op)
        {
            case "getSessionName": return attached is null ? null : SessionName(attached);
            case "getActiveTools": return new JsonArray([.. (attached?.Session.GetActiveTools() ?? []).Select(name => (JsonNode)name)]);
            case "getThinkingLevel": return attached?.Session.Snapshot.Context.ThinkingLevel ?? "off";
            case "getAllTools": return AllTools(attached);
            case "getCommands": return Commands();
            case "getSettings": return Settings?.Invoke() ?? new JsonObject();
            default: throw new NotSupportedException($"pi.{op}() is not available in this PiSharp host");
        }
    }

    /// <summary>The effective settings object getSettings returns (the CLI supplies the merged settings).</summary>
    internal Func<JsonNode?>? Settings { get; set; }

    private static JsonNode? SessionName(AgentSessionAttachment attached)
    {
        var tree = new SessionTreeQueries().Build(attached.Session.Snapshot.Log.Entries, attached.LifetimeToken);
        return tree.SessionNameAvailable && tree.SessionName is { Length: > 0 } name ? name : null;
    }

    /// <summary>Source getAllTools: name, description, parameters, promptGuidelines, exposure, namespace, annotations and sourceInfo.</summary>
    private JsonArray AllTools(AgentSessionAttachment? attached)
    {
        var tools = new JsonArray();
        var annotations = Extensions.SelectMany(extension => (extension.Descriptor["tools"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(tool => (Name: tool["name"]!.GetValue<string>(), Extension: extension, Tool: tool)))
            .GroupBy(item => item.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (attached is null) return tools;
        foreach (var registered in attached.Session.CaptureToolCatalogRegistry().RegisteredTools)
        {
            var declaration = registered.Declaration.Value;
            var name = declaration.GetProperty("name").GetString()!;
            var info = new JsonObject
            {
                ["name"] = name, ["description"] = declaration.TryGetProperty("description", out var description) ? description.GetString() : "",
                ["parameters"] = declaration.TryGetProperty("parameters", out var parameters) ? JsonNode.Parse(parameters.GetRawText()) : new JsonObject(),
                ["exposure"] = registered.Exposure switch
                {
                    ToolExposure.ModelOnly => "model-only", ToolExposure.Codemode => "codemode", ToolExposure.Deferred => "deferred", ToolExposure.Hidden => "hidden", _ => "direct"
                }
            };
            if (!registered.PromptGuidelines.IsDefaultOrEmpty) info["promptGuidelines"] = new JsonArray([.. registered.PromptGuidelines.Select(text => (JsonNode)text)]);
            if (registered.Namespace is { } space)
                info["namespace"] = new JsonObject { ["name"] = space.Name, ["description"] = space.Description, ["instructions"] = space.Instructions };
            if (registered.Annotations is { Count: > 0 } hints)
                info["annotations"] = new JsonObject([.. hints.OrderBy(hint => Array.IndexOf(AnnotationOrder, hint.Key) is var at && at < 0 ? int.MaxValue : at)
                    .Select(hint => KeyValuePair.Create(hint.Key, (JsonNode?)hint.Value))]);
            if (annotations.TryGetValue(name, out var source) && registered.IsExtension)
            {
                info["sourceInfo"] = SourceInfoOf(source.Extension);
            }
            else info["sourceInfo"] = new JsonObject { ["path"] = "<builtin:" + name + ">", ["source"] = "builtin", ["scope"] = "temporary", ["origin"] = "top-level" };
            tools.Add(info);
        }
        return tools;
    }

    private static readonly string[] AnnotationOrder = ["readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint"];

    /// <summary>Source getCommands: extension commands (with invocation names), then prompt templates and skills (IMPL-I adds those).</summary>
    private JsonArray Commands()
    {
        // The session's command catalog (extension commands, prompt templates, skills) when a session is bound; else this host's own.
        var names = CommandInvocationNames();
        var byOwner = Extensions.ToDictionary(extension => extension.OwnerId, StringComparer.Ordinal);
        if (CommandCatalog?.Invoke() is { } catalog)
        {
            var rows = new JsonArray();
            foreach (var row in catalog.Value.EnumerateArray())
            {
                var item = JsonNode.Parse(row.GetRawText())!.AsObject();
                var owner = item["ownerId"]?.GetValue<string>();
                item.Remove("ownerId"); item.Remove("ownerGeneration"); item.Remove("registrationId");
                if (owner is not null && byOwner.TryGetValue(owner, out var extension)) item["sourceInfo"] = SourceInfoOf(extension);
                rows.Add(item);
            }
            return rows;
        }
        var commands = new JsonArray();
        foreach (var extension in Extensions)
            foreach (var command in (extension.Descriptor["commands"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var name = command["name"]!.GetValue<string>();
                commands.Add(new JsonObject
                {
                    ["name"] = names.GetValueOrDefault((extension.Index, name)) ?? name, ["description"] = command["description"]?.DeepClone(), ["source"] = "extension",
                    ["sourceInfo"] = SourceInfoOf(extension)
                });
            }
        return commands;
    }

    private JsonNode? ContextRead(JsonElement p, string op)
    {
        var attached = Attached;
        var state = attached?.Session.Snapshot;
        switch (op)
        {
            case "model":
                if (state is null) return null;
                var model = state.Agent.Model;
                return ModelJson?.Invoke(model.Provider, model.Id) ?? new JsonObject { ["id"] = model.Id, ["provider"] = model.Provider, ["api"] = model.Api };
            case "scopedModels": return new JsonArray();
            case "thinkingLevel": return state?.Context.ThinkingLevel;
            case "isIdle":
                return state is null || !state.Agent.IsRunning && !state.IsProcessingOperation && !state.IsConfiguring && !state.IsAdmittingInput &&
                    !state.IsAppendingExtensionEntry && !state.IsEditingContext && !state.IsCompacting;
            case "isProjectTrusted": return ProjectTrusted ?? _options.ProjectTrusted(Cwd);
            case "hasPendingMessages":
                if (attached is null) return false;
                var queues = attached.Session.GetPendingInputQueueSnapshot();
                return !queues.SteeringMessages.IsEmpty || !queues.FollowUpMessages.IsEmpty || !state!.Agent.PendingInputs.IsEmpty;
            case "systemPrompt":
                // agent-session.ts: during a run the prompt is the one before_agent_start handlers produced (agent.state.systemPrompt).
                if (state is { Agent.IsRunning: true } && RunSystemPrompt is { } runPrompt) return runPrompt;
                return state is null ? "" : new PiSharp.Sessions.Context.SessionSystemReplay().Replay(state.Agent.Messages, CancellationToken.None).Prompt;
            case "contextUsage":
            {
                if (ContextUsage?.Invoke() is { } supplied) return supplied;
                // compaction.ts estimateContextTokens: the last assistant usage (input + output + cache) over the model's context window.
                if (state is null || ModelJson?.Invoke(state.Agent.Model.Provider, state.Agent.Model.Id)?["contextWindow"] is not JsonValue window ||
                    !window.TryGetValue<double>(out var contextWindow) || contextWindow <= 0) return null;
                double? tokens = null;
                foreach (var message in state.Context.Messages.Reverse())
                {
                    var body = message.WireBody.Value;
                    if (message.Role != "assistant" || !body.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                    double Field(string name) => usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
                    tokens = Field("input") + Field("output") + Field("cacheRead") + Field("cacheWrite");
                    break;
                }
                return new JsonObject { ["tokens"] = tokens, ["contextWindow"] = contextWindow, ["percent"] = tokens is { } used ? used / contextWindow * 100 : null };
            }
            case "callableTools":
                return ContextOf(p) is IExtensionToolContext tool ? new JsonArray([.. tool.Tools.Select(name => (JsonNode)new JsonObject { ["name"] = name })]) : new JsonArray();
            case "systemPromptOptions": return new JsonObject { ["cwd"] = Cwd };
            default: throw new NotSupportedException($"ctx.{op} is not available in this PiSharp host");
        }
    }

    /// <summary>The full model object of a provider/id (the CLI supplies its registry's Pi model JSON).</summary>
    internal Func<string, string, JsonNode?>? ModelJson { get; set; }
    /// <summary>ctx.getContextUsage() (the CLI supplies the session's usage estimate).</summary>
    internal Func<JsonNode?>? ContextUsage { get; set; }
    /// <summary>pi-ai stream/complete from extension code: the final AssistantMessage of the model's live route (supplied by the CLI).</summary>
    internal Func<JsonElement, JsonElement, CancellationToken, Task<JsonNode?>>? Stream { get; set; }
    /// <summary>The model registry calls of ctx.modelRegistry (find, getAll, classify, generateImages, …), supplied by the CLI.</summary>
    internal Func<string, JsonElement, CancellationToken, Task<JsonNode?>>? Models { get; set; }

    private async Task<JsonNode?> ModelsAsync(string op, JsonElement args, CancellationToken token) =>
        Models is null ? throw new NotSupportedException($"ctx.modelRegistry.{op}() is not available in this PiSharp host") : await Models(op, args, token).ConfigureAwait(false);

    private JsonNode? SessionRead(string op, JsonElement args)
    {
        var attached = Attached;
        if (attached is null) return op is "getEntries" or "getBranch" or "getTree" ? new JsonArray() : null;
        var state = attached.Session.Snapshot;
        SessionTreeSnapshot Tree() => new SessionTreeQueries().Build(state.Log.Entries, attached.LifetimeToken);
        string? Arg(int index) => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > index && args[index].ValueKind == JsonValueKind.String ? args[index].GetString() : null;
        static JsonNode Node(JsonData value) => JsonNode.Parse(value.ToString())!;
        switch (op)
        {
            case "getCwd": return state.Log.Header.WireBody.Value.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : Cwd;
            case "getSessionDir": return attached.Session.SessionFile is { } file ? Path.GetDirectoryName(file) : null;
            case "getSessionId": return state.Log.Header.Id;
            case "getSessionFile": return attached.Session.SessionFile;
            case "getLeafId": return state.Context.LeafId;
            case "getLeafEntry": return state.Context.LeafId is { } leaf && Tree().GetEntry(leaf) is { } leafEntry ? Node(leafEntry.WireBody) : null;
            case "getEntry": return Arg(0) is { } id && Tree().GetEntry(id) is { } entry ? Node(entry.WireBody) : null;
            case "getLabel": { var tree = Tree(); return Arg(0) is { } target && tree.LabelsAvailable && tree.Labels.TryGetValue(target, out var label) ? label.Label : null; }
            case "getBranch": return new JsonArray([.. Tree().GetBranch(Arg(0) ?? state.Context.LeafId, attached.LifetimeToken).Select(item => Node(item.WireBody))]);
            case "getEntries": return new JsonArray([.. Tree().Entries.Select(item => Node(item.WireBody))]);
            case "getHeader": return Node(state.Log.Header.WireBody);
            case "getSessionName": return SessionName(attached);
            case "getChildren": return Arg(0) is { } parent && Tree().ById.TryGetValue(parent, out var node)
                ? new JsonArray([.. node.ChildIds.Select(child => Node(Tree().ById[child].Entry.WireBody))]) : new JsonArray();
            case "getTree": return TreeJson(Tree());
            case "buildSessionContext":
                return new JsonObject
                {
                    ["messages"] = new JsonArray([.. state.Context.Messages.Select(message => Node(message.WireBody))]), ["thinkingLevel"] = state.Context.ThinkingLevel,
                    ["model"] = state.Context.Model is { } model ? new JsonObject { ["provider"] = model.Provider, ["modelId"] = model.ModelId } : null
                };
            case "buildContextEntries":
                return new JsonArray([.. (state.Context.ContextEntries.IsDefault ? [] : state.Context.ContextEntries).Select(entry => Node(entry.SourceEntry.WireBody))]);
            default: throw new NotSupportedException($"sessionManager.{op}() is not available in this PiSharp host");
        }
    }

    private static JsonArray TreeJson(SessionTreeSnapshot tree)
    {
        JsonObject Build(string id)
        {
            var node = tree.ById[id];
            var item = new JsonObject { ["entry"] = JsonNode.Parse(node.Entry.WireBody.ToString()) };
            if (node.ResolvedLabel is { } label) item["label"] = label.Label;
            item["children"] = new JsonArray([.. node.ChildIds.Select(child => (JsonNode)Build(child))]);
            return item;
        }
        return new JsonArray([.. tree.RootIds.Select(id => (JsonNode)Build(id))]);
    }

    private JsonNode? UiRead(string op) => op switch
    {
        "getEditorText" => EditorText?.Invoke() ?? "",
        "getToolsExpanded" => ToolsExpanded?.Invoke() ?? false,
        "getAllThemes" => new JsonArray([.. new[] { ("dark", (string?)null), ("light", null) }.Concat(_options.Themes.Select(theme => (theme.Name, (string?)theme.Path)))
            .Select(theme => (JsonNode)new JsonObject { ["name"] = theme.Item1, ["path"] = theme.Item2 })]),
        "terminalSize" => TerminalSize?.Invoke() is { } size ? new JsonObject { ["columns"] = size.Columns, ["rows"] = size.Rows }
            : new JsonObject { ["columns"] = SafeConsole(() => Console.WindowWidth, 80), ["rows"] = SafeConsole(() => Console.WindowHeight, 24) },
        "getGitBranch" => GitBranch?.Invoke(),
        "getExtensionStatuses" => new JsonObject([.. _statuses.Where(item => item.Value is not null).Select(item => KeyValuePair.Create(item.Key, (JsonNode?)item.Value))]),
        "getAvailableProviderCount" => AvailableProviderCount?.Invoke() ?? 0,
        _ => null
    };
    /// <summary>ctx.ui.getEditorText() (IMPL-I's editor supplies it).</summary>
    internal Func<string>? EditorText { get; set; }
    /// <summary>IMPL-I: the interactive terminal's size, tools expansion, git branch and provider count (footer data), read without the UI loop.</summary>
    internal Func<(int Columns, int Rows)>? TerminalSize { get; set; }
    internal Func<bool>? ToolsExpanded { get; set; }
    internal Func<string?>? GitBranch { get; set; }
    internal Func<int>? AvailableProviderCount { get; set; }
    /// <summary>IMPL-I: a widget, header or footer component asked to be redrawn (tui.requestRender in Node).</summary>
    internal Action<string>? ComponentInvalidated { get; set; }
    private static int SafeConsole(Func<int> read, int fallback) { try { var value = read(); return value > 0 ? value : fallback; } catch (IOException) { return fallback; } catch (InvalidOperationException) { return fallback; } }

    // ----------------------------------------------------------------------------------------------------------------- UI

    private IExtensionUi? UiOf(JsonElement p) => (ContextOf(p) as IExtensionUiContext)?.Ui;

    /// <summary>The UI of a ctx whose callback already returned: upstream's ctx.ui outlives the callback (an onComplete, a timer), so
    /// a fresh scope of the session's UI serves it, for the same extension owner but without the finished operation's cancellation.</summary>
    private IExtensionUiScope? DetachedUi(JsonElement p)
    {
        if (UiProvider is not { } provider || !p.TryGetProperty("ctx", out var id) || id.ValueKind != JsonValueKind.Number) return null;
        if (_contexts.TryGetValue(id.GetInt64(), out var live))
            return live is IExtensionUiContext ? null : provider.OpenScope(live);
        return ContextOf(p) is { } context ? provider.OpenScope(new DetachedContext(context, Attached?.LifetimeToken ?? CancellationToken.None)) : null;
    }

    private sealed class DetachedContext(IExtensionContext inner, CancellationToken session) : IExtensionContext
    {
        public string OwnerId => inner.OwnerId;
        public long OwnerGeneration => inner.OwnerGeneration;
        public CancellationToken OperationCancellationToken => CancellationToken.None;
        public CancellationToken SessionCancellationToken => session;
        public CancellationToken ExtensionLifetimeCancellationToken => inner.ExtensionLifetimeCancellationToken;
    }

    private async Task<JsonNode?> DialogAsync(JsonElement p, string op, JsonElement args, CancellationToken token)
    {
        await using var detached = DetachedUi(p);
        var ui = detached ?? UiOf(p);
        if (ui is null) return op == "confirm" ? false : null;
        string Text(int index) => args.GetArrayLength() > index && args[index].ValueKind == JsonValueKind.String ? args[index].GetString()! : "";
        ExtensionUiDialogOptions? Options(int index) => args.GetArrayLength() > index && args[index].ValueKind == JsonValueKind.Object &&
            args[index].TryGetProperty("timeout", out var timeout) && timeout.ValueKind == JsonValueKind.Number ? new(timeout.GetDouble()) : null;
        switch (op)
        {
            case "select":
            {
                var choices = args.GetArrayLength() > 1 && args[1].ValueKind == JsonValueKind.Array ? args[1].EnumerateArray().Select(item => item.GetString() ?? "").ToImmutableArray() : [];
                return ExtensionUiSourceDefaults.Text(await ui.SelectAsync(Text(0), choices, Options(2), token).ConfigureAwait(false));
            }
            case "confirm": return ExtensionUiSourceDefaults.Confirmation(await ui.ConfirmAsync(Text(0), Text(1), Options(2), token).ConfigureAwait(false));
            case "input": return ExtensionUiSourceDefaults.Text(await ui.InputAsync(Text(0), args.GetArrayLength() > 1 && args[1].ValueKind == JsonValueKind.String ? args[1].GetString() : null, Options(2), token).ConfigureAwait(false));
            case "editor": return ExtensionUiSourceDefaults.Text(await ui.EditorAsync(Text(0), args.GetArrayLength() > 1 && args[1].ValueKind == JsonValueKind.String ? args[1].GetString() : null, token).ConfigureAwait(false));
            default: throw new NotSupportedException($"ctx.ui.{op}() is not available in this PiSharp host");
        }
    }

    private async Task PublishAsync(JsonElement p)
    {
        var op = p.GetProperty("op").GetString();
        var args = p.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a : default;
        string? Text(int index) => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > index && args[index].ValueKind == JsonValueKind.String ? args[index].GetString() : null;
        if (op == "setStatus") _statuses[Text(0) ?? ""] = Text(1);
        // Interactive-only UI (setFooter, setHeader, setWorkingMessage/Visible/Indicator, setHiddenThinkingLabel, setEditorComponent,
        // addAutocompleteProvider, onTerminalInput, setToolsExpanded, component widgets): the interactive mode (IMPL-I) subscribes here;
        // component ids render through RenderComponentAsync.
        if (op is not null) InteractiveUi?.Invoke(op, args.ValueKind == JsonValueKind.Array ? args.Clone() : default);
        await using var detached = DetachedUi(p);
        IExtensionUi? ui;
        try { ui = detached ?? UiOf(p); } catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or OperationCanceledException) { ui = null; }
        ExtensionUiNotification? notification = op switch
        {
            "notify" => new ExtensionUiNotify(Text(0) ?? "", Text(1) switch { "warning" => ExtensionUiNotifyKind.Warning, "error" => ExtensionUiNotifyKind.Error, _ => ExtensionUiNotifyKind.Info }),
            "setStatus" => new ExtensionUiStatus(Text(0) ?? "", Text(1)),
            "setTitle" => new ExtensionUiTitle(Text(0) ?? ""),
            "setEditorText" or "pasteToEditor" => new ExtensionUiEditorText(Text(0) ?? ""),
            "setWidget" => Widget(),
            _ => null
        };
        if (notification is null) return;
        if (ui is null) return;
        try { await ui.PublishAsync(notification).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or OperationCanceledException or ObjectDisposedException) { }

        ExtensionUiNotification? Widget()
        {
            var key = Text(0) ?? "";
            var placement = args.GetArrayLength() > 2 && args[2].ValueKind == JsonValueKind.Object && args[2].TryGetProperty("placement", out var where) && where.GetString() == "belowEditor"
                ? ExtensionUiWidgetPlacement.BelowEditor : ExtensionUiWidgetPlacement.AboveEditor;
            if (args.GetArrayLength() < 2 || args[1].ValueKind == JsonValueKind.Null) return new ExtensionUiTextWidget(key, null, placement);
            if (args[1].ValueKind == JsonValueKind.Array) return new ExtensionUiTextWidget(key, [.. args[1].EnumerateArray().Select(line => line.GetString() ?? "")], placement);
            // A component widget: rendered once at the terminal width (component widgets that re-render need IMPL-I's widget host).
            if (args[1].TryGetProperty("component", out var component))
            {
                var width = SafeConsole(() => Console.WindowWidth, 80);
                var rows = Node.RequestAsync("component.render", new JsonObject { ["id"] = component.GetString(), ["width"] = width }).GetAwaiter().GetResult();
                return new ExtensionUiTextWidget(key, rows is { ValueKind: JsonValueKind.Array } lines ? [.. lines.EnumerateArray().Select(line => line.GetString() ?? "")] : [], placement);
            }
            return null;
        }
    }

    /// <summary>ctx.ui.custom(): the component lives in Node; the host renders it there and feeds it keys until it calls done().</summary>
    private async Task OpenComponentAsync(JsonElement p, CancellationToken token)
    {
        var context = ContextOf(p) ?? throw new NotSupportedException("ctx.ui.custom() needs an active extension context");
        var componentId = p.GetProperty("component").GetString()!;
        if ((context as IExtensionUiContext)?.Ui is not IExtensionCustomComponentUi ui)
        {
            await Node.RequestAsync("component.dispose", new JsonObject { ["id"] = componentId }, token).ConfigureAwait(false);
            throw new NotSupportedException("ctx.ui.custom() is not available in this mode");
        }
        var generation = (context as IExtensionSessionContext)?.SessionSnapshot?.Generation ?? 1;
        var identity = new ExtensionCustomComponentIdentity(context.OwnerId, context.OwnerGeneration, generation, componentId);
        _components[componentId] = (ui, identity);
        await ui.OpenCustomComponentAsync(new ExtensionCustomComponentCallbacks(identity,
            async (width, _, cancellation) => PiNodeOwner.Rows(await Node.RequestAsync("component.render", new JsonObject { ["id"] = componentId, ["width"] = width }, cancellation).ConfigureAwait(false)),
            async (data, _, cancellation) => await Node.RequestAsync("component.input", new JsonObject { ["id"] = componentId, ["data"] = data }, cancellation).ConfigureAwait(false),
            async _ => { _components.TryRemove(componentId, out var _); await Node.RequestAsync("component.dispose", new JsonObject { ["id"] = componentId }).ConfigureAwait(false); }), token).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------------------------------------------------- tools and commands

    private async Task<JsonNode?> ExecuteToolAsync(JsonElement p, PiNodeHostRequest request, CancellationToken token)
    {
        if (ContextOf(p) is not IExtensionToolContext context)
            return Outcome(p.GetProperty("name").GetString()!, "Nested tool calls are not available in this context", true, $"{p.GetProperty("toolCallId").GetString()}/0");
        var outcome = await context.ExecuteToolAsync(p.GetProperty("name").GetString()!, JsonData.Parse(p.GetProperty("args").GetRawText()),
            new ExtensionExecuteToolOptions(token, async (partial, cancellation) => await request.ReportProgress(JsonNode.Parse(partial.ToString())).ConfigureAwait(false))).ConfigureAwait(false);
        return new JsonObject
        {
            ["toolCall"] = new JsonObject { ["type"] = "toolCall", ["id"] = outcome.ToolCallId, ["name"] = outcome.ToolName, ["arguments"] = JsonNode.Parse(p.GetProperty("args").GetRawText()) },
            ["result"] = JsonNode.Parse(outcome.Result.ToString()), ["isError"] = outcome.IsError
        };
        static JsonObject Outcome(string name, string text, bool isError, string id) => new()
        {
            ["toolCall"] = new JsonObject { ["type"] = "toolCall", ["id"] = id, ["name"] = name, ["arguments"] = new JsonObject() },
            ["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["details"] = new JsonObject() },
            ["isError"] = isError
        };
    }

    /// <summary>The virtual modules' host hook. <c>builtinTool.execute</c> (createBashTool(...).execute and the other built-in tool
    /// factories) runs PiSharp's own built-in tool through the calling tool's context; model catalog reads use the run's registry.</summary>
    private async Task<JsonNode?> BridgeCallAsync(JsonElement p, PiNodeHostRequest request, CancellationToken token)
    {
        var name = p.GetProperty("name").GetString()!;
        var args = p.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a : default;
        switch (name)
        {
            case "builtinTool.execute":
            {
                var call = args[0];
                if (call.TryGetProperty("operations", out var operations) && operations.ValueKind != JsonValueKind.Null)
                    throw new NotSupportedException("Built-in tools with custom operations are not available in the PiSharp Node bridge");
                if (ContextOf(p) is not IExtensionToolContext context)
                    throw new NotSupportedException("Built-in tools run from extension code need a tool execution context in PiSharp");
                var outcome = await context.ExecuteToolAsync(call.GetProperty("name").GetString()!, JsonData.Parse(call.GetProperty("params").GetRawText()),
                    new ExtensionExecuteToolOptions(token, async (partial, _) => await request.ReportProgress(JsonNode.Parse(partial.ToString())).ConfigureAwait(false))).ConfigureAwait(false);
                var result = JsonNode.Parse(outcome.Result.ToString())!.AsObject();
                // Upstream execute throws for a failed call; the error text is the result's text.
                if (outcome.IsError) throw new InvalidOperationException(string.Concat((result["content"] as JsonArray ?? []).Select(item => item?["text"]?.GetValue<string>())));
                result.Remove("isError");
                return result;
            }
            case "stream": case "streamSimple": case "complete": case "completeSimple":
                if (Stream is null) throw new NotSupportedException($"{name} is not available in the PiSharp Node bridge");
                return await Stream(args[0], args.GetArrayLength() > 1 ? args[1] : default, token).ConfigureAwait(false);
            case "getModel": return Models is null ? null : await Models("find", JsonDocument.Parse(new JsonArray(args[0].GetString(), args[1].GetString()).ToJsonString()).RootElement, token).ConfigureAwait(false);
            case "getModels": return Models is null ? new JsonArray() : await Models("getAll", default, token).ConfigureAwait(false);
            default: throw new NotSupportedException($"{name} is not available in the PiSharp Node bridge");
        }
    }

    private async Task<JsonNode?> SessionCommandAsync(JsonElement p, CancellationToken token)
    {
        var context = ContextOf(p);
        var op = p.GetProperty("op").GetString();
        switch (op)
        {
            case "newSession" when context is IExtensionSessionCreationCommandContext creation:
            {
                var result = await creation.CreateSessionAsync(new ExtensionSessionCreationRequest(ExtensionSessionCreationKind.New,
                    ParentSession: p.TryGetProperty("parentSession", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null), token).ConfigureAwait(false);
                if (result is null) return new JsonObject { ["cancelled"] = true };
                await WithSessionAsync(p, result.Context, token).ConfigureAwait(false);
                return new JsonObject { ["cancelled"] = false };
            }
            case "fork" when context is IExtensionSessionCreationCommandContext creation:
            {
                var position = p.TryGetProperty("position", out var where) && where.GetString() == "at" ? ExtensionSessionCreationKind.ForkAt : ExtensionSessionCreationKind.ForkBefore;
                var result = await creation.CreateSessionAsync(new ExtensionSessionCreationRequest(position, p.GetProperty("entryId").GetString()), token).ConfigureAwait(false);
                if (result is null) return new JsonObject { ["cancelled"] = true };
                await WithSessionAsync(p, result.Context, token).ConfigureAwait(false);
                return new JsonObject { ["cancelled"] = false };
            }
            case "switchSession" when context is IExtensionSessionCommandContext switching:
            {
                var replaced = await switching.SwitchSessionAsync(Path.GetFullPath(p.GetProperty("sessionPath").GetString()!), cancellationToken: token).ConfigureAwait(false);
                if (replaced is null) return new JsonObject { ["cancelled"] = true };
                await WithSessionAsync(p, replaced, token).ConfigureAwait(false);
                return new JsonObject { ["cancelled"] = false };
            }
            case "navigateTree" when context is IExtensionSessionTreeCommandContext tree:
            {
                var options = p.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
                bool Flag(string name) => options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
                string? Str(string name) => options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var result = await tree.NavigateTreeAsync(new(p.GetProperty("targetId").GetString()!, Flag("summarize"), Str("customInstructions"), Flag("replaceInstructions"), Str("label")), token).ConfigureAwait(false);
                return new JsonObject { ["cancelled"] = result.Disposition != "Selected" };
            }
            default: throw new NotSupportedException($"ctx.{op}() is not available in this context");
        }
    }

    /// <summary>withSession(ctx): the callback runs in Node with a context bound to the replacement session.</summary>
    private async Task WithSessionAsync(JsonElement p, IExtensionContext replacement, CancellationToken token)
    {
        if (!p.TryGetProperty("withSession", out var callback) || callback.ValueKind != JsonValueKind.String) return;
        using var lease = Enter(replacement);
        await Node.RequestAsync("callback.invoke", new JsonObject { ["id"] = callback.GetString(), ["args"] = lease.Id }, token).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------------------------------------------------- UI seams (IMPL-I)

    /// <summary>Every <c>ctx.ui</c> publication (operation name and its JSON arguments) for the interactive mode's own widgets.</summary>
    internal Action<string, JsonElement>? InteractiveUi { get; set; }

    /// <summary>Renders a Node-side component (a widget, footer, header or editor factory's component) at a width.</summary>
    internal async Task<ImmutableArray<string>> RenderComponentAsync(string componentId, int width, CancellationToken token) =>
        PiNodeOwner.Rows(await Node.RequestAsync("component.render", new JsonObject { ["id"] = componentId, ["width"] = width }, token).ConfigureAwait(false)).Rows;

    /// <summary>Feeds terminal input to a Node-side component (handleInput).</summary>
    internal Task InputComponentAsync(string componentId, string data, CancellationToken token) =>
        Node.RequestAsync("component.input", new JsonObject { ["id"] = componentId, ["data"] = data }, token);

    /// <summary>ctx.ui.onTerminalInput handlers (published as <c>onTerminalInput</c> with a callback id): the handler's
    /// <c>{ consume?, data? }</c> result for one input chunk, or null.</summary>
    internal async Task<JsonElement?> InvokeTerminalInputAsync(string callbackId, string data, CancellationToken token) =>
        await Node.RequestAsync("callback.invoke", new JsonObject { ["id"] = callbackId, ["args"] = data }, token).ConfigureAwait(false);

    /// <summary>Source registerShortcut: the extensions' shortcuts (key id, description, extension path), in load order. The
    /// interactive mode resolves conflicts with its keybindings (runner.ts getShortcuts) and calls <see cref="RunShortcutAsync"/>.</summary>
    internal ImmutableArray<(string Shortcut, string? Description, string ExtensionPath, int Extension)> Shortcuts =>
        [.. Extensions.SelectMany(extension => (extension.Descriptor["shortcuts"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(item => (item["shortcut"]!.GetValue<string>(), item["description"]?.GetValue<string>(), extension.Path, extension.Index)))];

    /// <summary>Source runner.ts getShortcuts: the extension shortcuts for the effective keybindings. A key a reserved built-in action
    /// uses is skipped; a key another built-in or an earlier extension uses goes to the later extension. Diagnostics are warnings.</summary>
    internal (ImmutableArray<(string Key, string? Description, string ExtensionPath, int Extension, string Shortcut)> Shortcuts, ImmutableArray<(string Message, string Path)> Diagnostics)
        ResolveShortcuts(IReadOnlyDictionary<string, IReadOnlyList<string>> keybindings)
    {
        var builtin = new Dictionary<string, (string Keybinding, bool Restrict)>(StringComparer.Ordinal);
        foreach (var (keybinding, keys) in keybindings)
        {
            var restrict = ReservedKeybindings.Contains(keybinding);
            foreach (var key in keys)
            {
                var normalized = key.ToLowerInvariant();
                if (builtin.TryGetValue(normalized, out var existing) && existing.Restrict && !restrict) continue;
                builtin[normalized] = (keybinding, restrict);
            }
        }
        var chosen = new Dictionary<string, (string Key, string? Description, string ExtensionPath, int Extension, string Shortcut)>(StringComparer.Ordinal);
        var order = new List<string>();
        var diagnostics = ImmutableArray.CreateBuilder<(string, string)>();
        foreach (var (shortcut, description, path, index) in Shortcuts)
        {
            var normalized = shortcut.ToLowerInvariant();
            if (builtin.TryGetValue(normalized, out var bound) && bound.Restrict)
            { diagnostics.Add(($"Extension shortcut '{shortcut}' from {path} conflicts with built-in shortcut. Skipping.", path)); continue; }
            if (builtin.TryGetValue(normalized, out bound))
                diagnostics.Add(($"Extension shortcut conflict: '{shortcut}' is built-in shortcut for {bound.Keybinding} and {path}. Using {path}.", path));
            if (chosen.TryGetValue(normalized, out var earlier))
                diagnostics.Add(($"Extension shortcut conflict: '{shortcut}' registered by both {earlier.ExtensionPath} and {path}. Using {path}.", path));
            else order.Add(normalized);
            chosen[normalized] = (normalized, description, path, index, shortcut);
        }
        return ([.. order.Select(key => chosen[key])], diagnostics.ToImmutable());
    }

    private static readonly ImmutableHashSet<string> ReservedKeybindings =
    [
        "app.interrupt", "app.clear", "app.exit", "app.suspend", "app.thinking.cycle", "app.model.cycleForward", "app.model.cycleBackward",
        "app.model.select", "app.tools.expand", "app.thinking.toggle", "app.editor.external", "app.message.copy", "app.message.followUp",
        "tui.input.submit", "tui.select.confirm", "tui.select.cancel", "tui.input.copy", "tui.editor.deleteToLineEnd"
    ];

    /// <summary>Source createContext() for a handler the mode runs outside any event (a shortcut): a fresh context of the extension's
    /// owner for the current session; its UI is the session's.</summary>
    internal IExtensionContext CreateContext(int extension)
    {
        var loaded = Extensions.FirstOrDefault(item => item.Index == extension) ?? throw new InvalidOperationException($"Unknown extension {extension}");
        return new FreshContext(loaded.OwnerId, Math.Max(1, loaded.OwnerGeneration), Attached?.LifetimeToken ?? CancellationToken.None);
    }

    private sealed class FreshContext(string ownerId, long generation, CancellationToken session) : IExtensionContext
    {
        public string OwnerId => ownerId;
        public long OwnerGeneration => generation;
        public CancellationToken OperationCancellationToken => CancellationToken.None;
        public CancellationToken SessionCancellationToken => session;
        public CancellationToken ExtensionLifetimeCancellationToken => CancellationToken.None;
    }

    /// <summary>Runs a shortcut's handler with a fresh context (runner.ts: the shortcut handler gets createContext()).</summary>
    internal Task RunShortcutAsync(int extension, string shortcut, CancellationToken token) =>
        RunShortcutAsync(extension, shortcut, CreateContext(extension), token);

    /// <summary>Runs a shortcut's handler with a context bound to <paramref name="context"/>.</summary>
    internal async Task RunShortcutAsync(int extension, string shortcut, IExtensionContext context, CancellationToken token)
    {
        using var lease = Enter(context);
        await Node.RequestAsync("shortcut.run", new JsonObject { ["ext"] = extension, ["shortcut"] = shortcut, ["ctx"] = lease.Id }, token,
            afterPrecedingFrames: true).ConfigureAwait(false);
    }

    /// <summary>Source getMessageRenderer: the rows a registerMessageRenderer renderer draws for a custom message, or null when no
    /// extension renders that customType.</summary>
    internal Task<ImmutableArray<string>?> RenderMessageAsync(string customType, JsonNode message, int width, bool expanded, CancellationToken token) =>
        RenderAsync("render.message", new JsonObject { ["customType"] = customType, ["message"] = message.DeepClone(), ["width"] = width, ["expanded"] = expanded }, token);

    /// <summary>Source getEntryRenderer: the rows a registerEntryRenderer renderer draws for a custom entry, or null.</summary>
    internal Task<ImmutableArray<string>?> RenderEntryAsync(string customType, JsonNode entry, int width, bool expanded, CancellationToken token) =>
        RenderAsync("render.entry", new JsonObject { ["customType"] = customType, ["entry"] = entry.DeepClone(), ["width"] = width, ["expanded"] = expanded }, token);

    private async Task<ImmutableArray<string>?> RenderAsync(string method, JsonObject parameters, CancellationToken token)
    {
        var result = await Node.RequestAsync(method, parameters, token).ConfigureAwait(false);
        if (result is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("handled", out var handled) || handled.ValueKind != JsonValueKind.True) return null;
        return [.. value.GetProperty("lines").EnumerateArray().Select(line => line.GetString() ?? "")];
    }

    /// <summary>Source getMarkdownTransformers: the extensions' markdown transformers applied in load order.</summary>
    internal async Task<string> TransformMarkdownAsync(string markdown, JsonObject? context, CancellationToken token)
    {
        if (!Extensions.Any(extension => extension.Descriptor["markdownTransformer"]?.GetValue<bool>() == true)) return markdown;
        var result = await Node.RequestAsync("markdown.transform", new JsonObject { ["markdown"] = markdown, ["context"] = context?.DeepClone() ?? new JsonObject() }, token).ConfigureAwait(false);
        return result is { ValueKind: JsonValueKind.String } text ? text.GetString()! : markdown;
    }

    // ----------------------------------------------------------------------------------------------------------------- virtual models

    /// <summary>runner.ts bindCore: the virtual models the extensions registered (pi.registerVirtualModel) join a model registry;
    /// routing calls the extension's <c>route(request, ctx)</c> in Node with upstream's ModelRouteRequest.</summary>
    /// <summary>model-runtime.ts registerProvider: each extension provider's chat models, baseUrl, apiKey and headers join the registry
    /// (model selection and the live routes read them); a provider with streamSimple serves its API through the Node host.</summary>
    internal void RegisterProviders(PiSharp.Cli.Models.ModelRegistry registry)
    {
        foreach (var registration in ProviderRegistrations)
        {
            if (registration["config"] is not JsonObject described || registration["name"]?.GetValue<string>() is not { } name) continue;
            var config = (JsonObject)described.DeepClone();
            foreach (var field in new[] { "classifiers", "images", "hasRefreshModels", "oauth" }) config.Remove(field);
            if (config["models"] is JsonArray models)
            {
                var chat = models.OfType<JsonObject>().Where(model => model["type"]?.GetValue<string>() is null or "chat").Select(model => model.DeepClone()).ToArray();
                if (chat.Length == 0) config.Remove("models"); else config["models"] = new JsonArray(chat);
            }
            var hasStream = config["hasStreamSimple"]?.GetValue<bool>() == true;
            if (config["models"] is null && config["baseUrl"] is null && config["apiKey"] is null && config["headers"] is null && !hasStream) continue;
            try
            {
                registry.RegisterExtensionProvider(name, config);
                if (hasStream && config["api"]?.GetValue<string>() is { } api)
                    registry.RegisterCustomStream(api, entry => new PiProviderTransport(this, entry, registry));
            }
            catch (InvalidOperationException error)
            { _ = ReportAsync(registration["extensionPath"]?.GetValue<string>() ?? name, "register_provider", error.Message); }
        }
    }

    internal void RegisterVirtualModels(PiSharp.Cli.Models.ModelRegistry registry)
    {
        foreach (var registration in VirtualModelRegistrations)
        {
            if (registration["definition"] is not JsonObject definition) continue;
            var provider = definition["provider"]?.GetValue<string>(); var id = definition["id"]?.GetValue<string>();
            if (provider is null || id is null) continue;
            ImmutableArray<string>? Strings(string name) => definition[name] is JsonArray items ? [.. items.Select(item => item!.GetValue<string>())] : null;
            double? Number(string name) => definition[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;
            var thinkingLevels = Strings("thinkingLevels") ?? (definition["thinkingLevelMap"] is JsonObject map
                ? [.. map.Where(level => level.Value is not null).Select(level => level.Key)] : null);
            try
            {
                registry.RegisterVirtualModel(new(provider, id, definition["name"]?.GetValue<string>() ?? id,
                    request => RouteAsync(registry, provider, id, request), thinkingLevels, Number("contextWindow"), Number("maxTokens"), Strings("input")));
            }
            catch (InvalidOperationException error)
            { _ = ReportAsync(registration["extensionPath"]?.GetValue<string>() ?? provider, "register_virtual_model", error.Message); }
        }
    }

    private async ValueTask<PiSharp.Cli.Models.ModelRoute> RouteAsync(PiSharp.Cli.Models.ModelRegistry registry, string provider, string id,
        PiSharp.Cli.Models.ModelRouteRequest request)
    {
        static JsonNode Model(PiSharp.Cli.Models.RegistryModel model) => JsonNode.Parse(model.ToJsonString())!;
        var payload = new JsonObject
        {
            ["model"] = Model(request.Model), ["thinkingLevel"] = request.ThinkingLevel,
            ["reason"] = request.Reason switch { PiSharp.Cli.Models.ModelRouteReason.Continuation => "continuation", PiSharp.Cli.Models.ModelRouteReason.Retry => "retry",
                PiSharp.Cli.Models.ModelRouteReason.Direct => "direct", _ => "user" },
            ["messages"] = new JsonArray([.. request.Messages.Select(message => (JsonNode)message.DeepClone())])
        };
        if (request.Previous is { } previous)
            payload["previous"] = new JsonObject { ["model"] = Model(previous.Model), ["thinkingLevel"] = previous.ThinkingLevel };
        if (request.Failed is { } failed)
            payload["failed"] = new JsonObject { ["model"] = Model(failed.Model), ["thinkingLevel"] = failed.ThinkingLevel, ["message"] = failed.Message.DeepClone() };
        if (request.State is not null) payload["state"] = request.State.DeepClone();
        var route = await Node.RequestAsync("virtualModel.route", new JsonObject { ["provider"] = provider, ["id"] = id, ["request"] = payload },
            request.CancellationToken).ConfigureAwait(false);
        if (route is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("model", out var target) || target.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Virtual model {provider}/{id} returned no route.");
        var routedProvider = target.GetProperty("provider").GetString()!; var routedId = target.GetProperty("id").GetString()!;
        var physical = registry.Find(routedProvider, routedId) ?? PiSharp.Cli.Models.RegistryModel.FromJson(JsonNode.Parse(target.GetRawText())!.AsObject());
        return new(physical, value.TryGetProperty("thinkingLevel", out var level) && level.ValueKind == JsonValueKind.String ? level.GetString()! : request.ThinkingLevel,
            value.TryGetProperty("state", out var state) ? JsonNode.Parse(state.GetRawText()) : null);
    }

    // ----------------------------------------------------------------------------------------------------------------- user bash output

    internal IDisposable RegisterShellOutput(string callId, ExtensionShellOutputCallback callback)
    {
        _shellOutputs[callId] = (callback, Task.CompletedTask);
        return new Release(() => _shellOutputs.TryRemove(callId, out _));
    }
    internal async Task DrainShellOutputAsync(string callId)
    {
        if (_shellOutputs.TryGetValue(callId, out var output)) await output.Chain.ConfigureAwait(false);
    }
    private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }
}
