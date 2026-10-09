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
    { _activation = activation; _actions = actions; _ = registry; _ = facadeHost; }

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
            {
                if (ContextOf(p) is IExtensionCommandContext command && command is not null && _activation is not null)
                    throw new NotSupportedException("ctx.compact() is not available in this PiSharp host yet");
                throw new NotSupportedException("ctx.compact() is not available in this PiSharp host yet");
            }
            case "command.waitForIdle": await RequireAttached().Session.WaitForIdleAsync(token).ConfigureAwait(false); return null;
            case "command.session": return await SessionCommandAsync(p, token).ConfigureAwait(false);
            case "command.reload": throw new NotSupportedException("ctx.reload() is not available in this PiSharp host yet");
            case "models.read": return await ModelsAsync(Op(), Args(), token).ConfigureAwait(false);
            case "models.call": return await ModelsAsync(Op(), Args(), token).ConfigureAwait(false);
            case "bridge.call": throw new NotSupportedException($"{p.GetProperty("name").GetString()} is not available in the PiSharp Node bridge");
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
            case "provider.register": lock (_providers) _providers.Add(JsonNode.Parse(parameters.GetRawText())!.AsObject()); RegistrationsChanged?.Invoke(); return;
            case "provider.unregister":
                lock (_providers) _providers.RemoveAll(item => item["name"]?.GetValue<string>() == parameters.GetProperty("name").GetString());
                RegistrationsChanged?.Invoke(); return;
            case "virtualModel.register": lock (_virtualModels) _virtualModels.Add(JsonNode.Parse(parameters.GetRawText())!.AsObject()); RegistrationsChanged?.Invoke(); return;
            case "virtualModel.unregister":
                lock (_virtualModels) _virtualModels.RemoveAll(item => item["definition"]?["provider"]?.GetValue<string>() == parameters.GetProperty("provider").GetString() &&
                    item["definition"]?["id"]?.GetValue<string>() == parameters.GetProperty("id").GetString());
                RegistrationsChanged?.Invoke(); return;
            case "mcp.register": lock (_mcpServers) _mcpServers.Add(JsonNode.Parse(parameters.GetRawText())!.AsObject()); return;
            case "mcp.unregister": lock (_mcpServers) _mcpServers.RemoveAll(item => item["name"]?.GetValue<string>() == parameters.GetProperty("name").GetString()); return;
            case "registrations.changed":
            {
                var index = parameters.GetProperty("ext").GetInt32();
                lock (_extensions) foreach (var extension in _extensions.Where(item => item.Index == index))
                        extension.Descriptor = JsonNode.Parse(parameters.GetProperty("extension").GetRawText())!.AsObject();
                RegistrationsChanged?.Invoke();
                return;
            }
            default: return;
        }
    }

    /// <summary>ctx.shutdown(): the mode's graceful shutdown.</summary>
    internal Action? ShutdownRequested { get; set; }

    private static async Task Guard(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("Extension action failed: {0}", error.Message); }
    }

    // ----------------------------------------------------------------------------------------------------------------- actions

    private async Task SendMessageAsync(JsonElement p, CancellationToken token)
    {
        var actions = _actions ?? throw new InvalidOperationException("Extension runtime not initialized.");
        var message = p.GetProperty("message");
        var options = p.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        var deliverAs = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("deliverAs", out var d) ? d.GetString() : null;
        await actions.SendMessageAsync(new ExtensionCustomMessage(message.GetProperty("customType").GetString() ?? "",
                message.TryGetProperty("content", out var content) ? JsonData.Parse(content.GetRawText()) : JsonData.Parse("\"\""),
                !message.TryGetProperty("display", out var display) || display.ValueKind != JsonValueKind.False,
                message.TryGetProperty("details", out var details) ? JsonData.Parse(details.GetRawText()) : null),
            new ExtensionMessageOptions(options.ValueKind == JsonValueKind.Object && options.TryGetProperty("triggerTurn", out var trigger) && trigger.ValueKind is JsonValueKind.True or JsonValueKind.False ? trigger.GetBoolean() : null,
                deliverAs switch { "followUp" => ExtensionMessageDelivery.FollowUp, "nextTurn" => ExtensionMessageDelivery.NextTurn, "steer" => ExtensionMessageDelivery.Steer, _ => null }),
            token).ConfigureAwait(false);
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
        var descriptor = new ModelDescriptor(model.GetProperty("provider").GetString()!, model.GetProperty("id").GetString()!,
            model.TryGetProperty("api", out var api) ? api.GetString() ?? "" : "");
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
            if (annotations.TryGetValue(name, out var source) && registered.IsExtension)
            {
                if (source.Tool["annotations"] is JsonObject hints) info["annotations"] = hints.DeepClone();
                info["sourceInfo"] = new JsonObject { ["path"] = source.Extension.ResolvedPath, ["source"] = "local", ["scope"] = "user", ["origin"] = "top-level" };
            }
            else info["sourceInfo"] = new JsonObject { ["path"] = "<builtin:" + name + ">", ["source"] = "builtin", ["scope"] = "temporary", ["origin"] = "top-level" };
            tools.Add(info);
        }
        return tools;
    }

    /// <summary>Source getCommands: extension commands (with invocation names), then prompt templates and skills (IMPL-I adds those).</summary>
    private JsonArray Commands()
    {
        var commands = new JsonArray();
        foreach (var extension in Extensions)
            foreach (var command in (extension.Descriptor["commands"] as JsonArray ?? []).OfType<JsonObject>())
                commands.Add(new JsonObject
                {
                    ["name"] = command["name"]!.GetValue<string>(), ["description"] = command["description"]?.DeepClone(), ["source"] = "extension",
                    ["sourceInfo"] = new JsonObject { ["path"] = extension.ResolvedPath, ["source"] = "local", ["scope"] = "user", ["origin"] = "top-level" }
                });
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
            case "isProjectTrusted": return _options.ProjectTrusted(Cwd);
            case "hasPendingMessages":
                if (attached is null) return false;
                var queues = attached.Session.GetPendingInputQueueSnapshot();
                return !queues.SteeringMessages.IsEmpty || !queues.FollowUpMessages.IsEmpty || !state!.Agent.PendingInputs.IsEmpty;
            case "systemPrompt":
                return state is null ? "" : new PiSharp.Sessions.Context.SessionSystemReplay().Replay(state.Agent.Messages, CancellationToken.None).Prompt;
            case "contextUsage": return ContextUsage?.Invoke();
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
        "getToolsExpanded" => false,
        "getAllThemes" => new JsonArray([.. new[] { ("dark", (string?)null), ("light", null) }.Concat(_options.Themes.Select(theme => (theme.Name, (string?)theme.Path)))
            .Select(theme => (JsonNode)new JsonObject { ["name"] = theme.Item1, ["path"] = theme.Item2 })]),
        "terminalSize" => new JsonObject { ["columns"] = SafeConsole(() => Console.WindowWidth, 80), ["rows"] = SafeConsole(() => Console.WindowHeight, 24) },
        "getGitBranch" => null,
        "getExtensionStatuses" => new JsonObject([.. _statuses.Where(item => item.Value is not null).Select(item => KeyValuePair.Create(item.Key, (JsonNode?)item.Value))]),
        "getAvailableProviderCount" => 0,
        _ => null
    };
    /// <summary>ctx.ui.getEditorText() (IMPL-I's editor supplies it).</summary>
    internal Func<string>? EditorText { get; set; }
    private static int SafeConsole(Func<int> read, int fallback) { try { var value = read(); return value > 0 ? value : fallback; } catch (IOException) { return fallback; } catch (InvalidOperationException) { return fallback; } }

    // ----------------------------------------------------------------------------------------------------------------- UI

    private IExtensionUi? UiOf(JsonElement p) => (ContextOf(p) as IExtensionUiContext)?.Ui;

    private async Task<JsonNode?> DialogAsync(JsonElement p, string op, JsonElement args, CancellationToken token)
    {
        var ui = UiOf(p);
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
        var ui = UiOf(p);
        if (ui is null) return;
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
        try { await ui.PublishAsync(notification).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or OperationCanceledException) { }

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
