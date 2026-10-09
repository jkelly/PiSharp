// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts (ExtensionAPI.on, registerTool,
// registerCommand, registerToolRenderer, ToolDefinition, the event and result types), packages/coding-agent/src/core/extensions/wrapper.ts
// (wrapRegisteredTool), packages/coding-agent/src/core/tools/tool-definition-wrapper.ts and packages/agent/src/agent-loop.ts
// (a thrown execute error becomes an error tool result with the error message).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Compatibility.Node.Pi;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>A Node-hosted extension as a registry owner: each registration it made in Node becomes the native registration whose
/// callbacks call back into Node. The extension's functions never leave Node; only JSON crosses.</summary>
internal sealed class PiNodeOwner(PiExtensionHost host, PiLoadedExtension extension) : IPiSharpExtension
{
    /// <summary>Events delivered as observations (handlers' results are ignored upstream).</summary>
    internal static readonly ImmutableHashSet<string> ObservationEvents =
    [
        "session_start", "session_info_changed", "session_compact", "session_compact_failed", "session_shutdown", "mcp_servers_change",
        "session_tree", "after_provider_response", "provider_stream_event", "agent_start", "agent_end", "agent_settled", "ui_prompt_start",
        "ui_prompt_end", "turn_start", "message_start", "message_update", "tool_execution_start", "tool_execution_update", "tool_execution_end",
        "model_select", "thinking_level_select"
    ];
    /// <summary>Events whose handler results the host folds (result-returning event handlers).</summary>
    internal static readonly ImmutableHashSet<string> ResultEvents =
    [
        "project_trust", "resources_discover", "session_before_compact", "cache_warming_decision", "before_provider_request",
        "before_provider_headers", "turn_end", "agent_before_settle", "message_end"
    ];

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        var descriptor = extension.Descriptor;
        foreach (var eventName in extension.Events) RegisterEvent(registry, eventName);
        foreach (var tool in (descriptor["tools"] as JsonArray ?? []).OfType<JsonObject>()) registry.RegisterTool(Tool(tool));
        foreach (var command in (descriptor["commands"] as JsonArray ?? []).OfType<JsonObject>()) registry.RegisterCommand(Command(command));
        if (descriptor["toolRenderers"] is JsonValue renderers && renderers.GetValue<int>() > 0 && registry is IExtensionToolRendererRegistry rendererRegistry)
            rendererRegistry.RegisterToolRenderer(new("tool-renderer", ResolveRenderers));
        // pi.registerMcpServer(): servers registered while the extension loaded are read when the session starts.
        if (registry is PiSharp.Extensions.Mcp.Registration.IExtensionMcpServerRegistry mcp)
            foreach (var server in host.McpServerRegistrations.Where(server => server["extensionPath"]?.GetValue<string>() == extension.Path))
                try { mcp.RegisterMcpServer(server["name"]!.GetValue<string>(), JsonData.Parse(server["config"]?.ToJsonString() ?? "{}")); }
                catch (NotSupportedException) { }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------------------------------------------------------- dispatch

    /// <summary>Runs the extension's handlers for one event in Node and returns the reduced result (null for undefined).</summary>
    private async Task<JsonElement?> EmitAsync(string eventName, JsonNode payload, IExtensionContext context, CancellationToken token, int? handler = null)
    {
        using var lease = host.Enter(context);
        var parameters = new JsonObject { ["ext"] = extension.Index, ["event"] = eventName, ["payload"] = payload, ["ctx"] = lease.Id };
        if (handler is { } index) parameters["handler"] = index;
        var response = await host.CallAsync("emit", parameters, token).ConfigureAwait(false);
        await host.ReportErrorsAsync(extension, response).ConfigureAwait(false);
        return response is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("result", out var result) ? result : null;
    }

    private static JsonNode Parse(JsonData value) => JsonNode.Parse(value.ToString())!;
    private static JsonNode Parse(JsonElement value) => JsonNode.Parse(value.GetRawText())!;
    private static JsonData Data(JsonElement value) => JsonData.Parse(value.GetRawText());
    private static JsonObject Event(string type, params (string Name, JsonNode? Value)[] fields)
    {
        var value = new JsonObject { ["type"] = type };
        foreach (var (name, field) in fields) if (field is not null) value[name] = field;
        return value;
    }

    private void RegisterEvent(IExtensionRegistry registry, string eventName)
    {
        var id = "on-" + eventName;
        if (ObservationEvents.Contains(eventName))
        {
            registry.Observe(new(id, eventName, async (observation, context, token) =>
                await EmitAsync(eventName, Parse(observation), context, token).ConfigureAwait(false)));
            return;
        }
        if (ResultEvents.Contains(eventName))
        {
            if (registry is not IExtensionEventHandlerRegistry handlers) return;
            handlers.RegisterEventHandler(new(id, eventName, async (current, context, token) =>
                await EmitAsync(eventName, Parse(current), context, token).ConfigureAwait(false) is { } result ? Data(result) : null));
            return;
        }
        switch (eventName)
        {
            case "context":
                registry.RegisterContextHandler(new(id, async (snapshot, context, token) =>
                    Messages(await EmitAsync(eventName, Event("context", ("messages", MessagesNode(snapshot.Messages))), context, token).ConfigureAwait(false)) is { } messages
                        ? new ExtensionContextMessagesPatch(messages) : null));
                return;
            case "context_with_system":
                registry.RegisterContextWithSystemHandler(new(id, async (snapshot, context, token) =>
                    Messages(await EmitAsync(eventName, Event("context_with_system", ("messages", MessagesNode(snapshot.Messages))), context, token).ConfigureAwait(false)) is { } messages
                        ? new ExtensionContextMessagesPatch(messages) : null));
                return;
            case "before_agent_start":
                // One native handler per Node handler: each returns at most one message, and the host chains the system prompt.
                for (var index = 0; index < extension.HandlerCount(eventName); index++)
                {
                    var handler = index;
                    registry.RegisterBeforeAgentStartHandler(new(id + "-" + handler, async (snapshot, context, token) =>
                    {
                        var result = await EmitAsync(eventName, Event("before_agent_start", ("prompt", snapshot.Prompt),
                            ("images", snapshot.Images is null ? null : Parse(snapshot.Images)), ("systemPrompt", snapshot.SystemPrompt)), context, token, handler).ConfigureAwait(false);
                        if (result is not { ValueKind: JsonValueKind.Object } value) return null;
                        PiSharp.Extensions.Events.ExtensionCustomMessage? message = null; string? systemPrompt = null;
                        if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0)
                        {
                            var first = messages[0];
                            message = new(first.TryGetProperty("customType", out var type) ? type.GetString() ?? "" : "",
                                !first.TryGetProperty("display", out var display) || display.ValueKind == JsonValueKind.True,
                                first.TryGetProperty("content", out var content) ? Data(content) : null,
                                first.TryGetProperty("details", out var details) ? Data(details) : null);
                        }
                        if (value.TryGetProperty("systemPrompt", out var prompt) && prompt.ValueKind == JsonValueKind.String) systemPrompt = prompt.GetString();
                        return message is null && systemPrompt is null ? null : new ExtensionBeforeAgentStartPatch(message, systemPrompt);
                    }));
                }
                return;
            case "input":
                registry.RegisterInputHandler(new(id, async (snapshot, context, token) =>
                {
                    var result = await EmitAsync(eventName, Event("input", ("text", snapshot.Text), ("images", snapshot.Images is null ? null : Parse(snapshot.Images)),
                        ("source", snapshot.Source switch { ExtensionInputSource.Rpc => "rpc", ExtensionInputSource.Extension => "extension", _ => "interactive" }),
                        ("streamingBehavior", snapshot.StreamingBehavior)), context, token).ConfigureAwait(false);
                    if (result is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("action", out var action)) return null;
                    return action.GetString() switch
                    {
                        "handled" => new ExtensionInputPatch(ExtensionInputAction.Handled),
                        "transform" => new ExtensionInputPatch(ExtensionInputAction.Transform, value.TryGetProperty("text", out var text) ? text.GetString() : snapshot.Text,
                            value.TryGetProperty("images", out var images) && images.ValueKind != JsonValueKind.Null ? Data(images) : null),
                        _ => null
                    };
                }));
                return;
            case "tool_call":
                registry.RegisterToolCallHandler(new(id, async (snapshot, context, token) =>
                {
                    var input = Parse(snapshot.Arguments);
                    var before = input.ToJsonString();
                    var result = await EmitAsync(eventName, Event("tool_call", ("toolName", snapshot.ToolName), ("toolCallId", snapshot.ToolCallId),
                        ("input", input), ("parentToolCallId", snapshot.ParentToolCallId)), context, token).ConfigureAwait(false);
                    if (result is not { ValueKind: JsonValueKind.Object } value) return null;
                    // Upstream handlers edit event.input in place; the edited input replaces the arguments.
                    JsonData? arguments = value.TryGetProperty("input", out var edited) && edited.ValueKind == JsonValueKind.Object && edited.GetRawText() != before
                        ? Data(edited) : null;
                    JsonData? decision = value.TryGetProperty("result", out var raw) && raw.ValueKind == JsonValueKind.Object ? Data(raw) : null;
                    return arguments is null && decision is null ? null : new ExtensionToolCallPatch(arguments, decision);
                }));
                return;
            case "tool_result":
                registry.RegisterToolResultHandler(new(id, async (snapshot, context, token) =>
                {
                    var payload = Event("tool_result", ("toolName", snapshot.ToolName), ("toolCallId", snapshot.ToolCallId),
                        ("parentToolCallId", snapshot.ParentToolCallId), ("input", Parse(snapshot.Arguments)));
                    var result = snapshot.Result.Value;
                    payload["content"] = result.TryGetProperty("content", out var content) ? Parse(content) : new JsonArray();
                    payload["details"] = result.TryGetProperty("details", out var details) ? Parse(details) : null;
                    if (result.TryGetProperty("structuredContent", out var structured)) payload["structuredContent"] = Parse(structured);
                    if (result.TryGetProperty("usage", out var usage)) payload["usage"] = Parse(usage);
                    payload["isError"] = snapshot.OutcomeIsError || result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
                    var reduced = await EmitAsync(eventName, payload, context, token).ConfigureAwait(false);
                    return reduced is { ValueKind: JsonValueKind.Object } value ? new ExtensionToolResultPatch(Data(value)) : null;
                }));
                return;
            case "user_bash":
                if (registry is not IExtensionUserBashRegistry bash) return;
                bash.RegisterUserBashHandler(new(id, async (snapshot, context, token) =>
                {
                    var result = await EmitAsync(eventName, Event("user_bash", ("command", snapshot.Command), ("excludeFromContext", snapshot.ExcludeFromContext),
                        ("cwd", snapshot.WorkingDirectory)), context, token).ConfigureAwait(false);
                    if (result is not { ValueKind: JsonValueKind.Object } value) return null;
                    if (value.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.String)
                        return new ExtensionUserBashPatch { Operations = new PiShellOperations(host, operations.GetString()!) };
                    if (value.TryGetProperty("result", out var ran) && ran.ValueKind == JsonValueKind.Object)
                        return new ExtensionUserBashPatch
                        {
                            Result = new(ran.GetProperty("output").GetString() ?? "", ran.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : null,
                                ran.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True,
                                ran.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
                                ran.TryGetProperty("fullOutputPath", out var full) && full.ValueKind == JsonValueKind.String ? full.GetString() : null)
                        };
                    return null;
                }));
                return;
            case "session_before_switch":
            case "session_before_fork":
                RegisterSessionVetoes(registry);
                return;
            case "session_before_tree":
                if (registry is not IExtensionSessionTreeLifecycleRegistry tree) return;
                tree.RegisterSessionBeforeTreeHandler(new(id, async (proposal, context, token) =>
                {
                    var preparation = new JsonObject
                    {
                        ["targetId"] = proposal.TargetId, ["oldLeafId"] = proposal.OldLeafId, ["commonAncestorId"] = proposal.CommonAncestorId,
                        ["entriesToSummarize"] = new JsonArray([.. proposal.EntriesToSummarize.Select(entry => Parse(entry))]),
                        ["userWantsSummary"] = proposal.Options.Summarize
                    };
                    if (proposal.Options.CustomInstructions is { } instructions) preparation["customInstructions"] = instructions;
                    if (proposal.Options.ReplaceInstructions) preparation["replaceInstructions"] = true;
                    if (proposal.Options.Label is { } label) preparation["label"] = label;
                    var result = await EmitAsync(eventName, Event("session_before_tree", ("preparation", preparation)), context, token).ConfigureAwait(false);
                    if (result is not { ValueKind: JsonValueKind.Object } value) return null;
                    ExtensionTreeSummary? summary = null;
                    if (value.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object)
                        summary = new(s.GetProperty("summary").GetString() ?? "", s.TryGetProperty("usage", out var usage) ? TokenUsageOf(usage) : TokenUsage.Zero,
                            s.TryGetProperty("details", out var details) ? Data(details) : null);
                    return new ExtensionBeforeTreeResult(value.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True, summary,
                        value.TryGetProperty("customInstructions", out var custom) ? new(custom.ValueKind == JsonValueKind.String ? custom.GetString() : null) : null,
                        value.TryGetProperty("replaceInstructions", out var replace) && replace.ValueKind is JsonValueKind.True or JsonValueKind.False ? new(replace.GetBoolean()) : null,
                        value.TryGetProperty("label", out var newLabel) ? new(newLabel.ValueKind == JsonValueKind.String ? newLabel.GetString() : null) : null);
                }));
                return;
            default:
                // An event this host does not raise (or a custom one): registered as an observation, as upstream keeps any handler.
                registry.Observe(new(id, eventName, async (observation, context, token) =>
                    await EmitAsync(eventName, Parse(observation), context, token).ConfigureAwait(false)));
                return;
        }
    }

    private bool _vetoesRegistered;
    /// <summary>session_before_switch (reason "new" or "resume") and session_before_fork: a <c>cancel</c> result stops the change.</summary>
    private void RegisterSessionVetoes(IExtensionRegistry registry)
    {
        if (_vetoesRegistered) return; _vetoesRegistered = true;
        var events = extension.Events.ToHashSet(StringComparer.Ordinal);
        static bool Cancelled(JsonElement? result) => result is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True;
        if (events.Contains("session_before_switch") && registry is IExtensionSessionLifecycleRegistry lifecycle)
            lifecycle.RegisterSessionSwitchHandler(new("before-switch", async (proposal, context, token) =>
                Cancelled(await EmitAsync("session_before_switch", Event("session_before_switch", ("reason", "resume"), ("targetSessionFile", proposal.TargetPath)), context, token).ConfigureAwait(false))
                    ? ExtensionSessionSwitchDecision.Cancel : ExtensionSessionSwitchDecision.Continue));
        if (registry is IExtensionSessionCreationRegistry creation)
            creation.RegisterSessionCreationHandler(new("before-creation", async (proposal, context, token) =>
            {
                JsonElement? result = null;
                if (proposal.Kind == ExtensionSessionCreationKind.New)
                { if (events.Contains("session_before_switch")) result = await EmitAsync("session_before_switch", Event("session_before_switch", ("reason", "new")), context, token).ConfigureAwait(false); }
                else if (events.Contains("session_before_fork"))
                    result = await EmitAsync("session_before_fork", Event("session_before_fork", ("entryId", proposal.EntryId),
                        ("position", proposal.Kind == ExtensionSessionCreationKind.ForkBefore ? "before" : "at")), context, token).ConfigureAwait(false);
                return Cancelled(result) ? ExtensionSessionSwitchDecision.Cancel : ExtensionSessionSwitchDecision.Continue;
            }));
    }

    private static TokenUsage TokenUsageOf(JsonElement usage) => PiSharp.CodingAgent.SessionWireUsage.Read(usage);

    private static JsonArray MessagesNode(ImmutableArray<TranscriptEntry> messages) => new([.. messages.Select(message => Parse(message.WireBody))]);
    private static ImmutableArray<TranscriptEntry>? Messages(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return null;
        return [.. messages.EnumerateArray().Select(message => new TranscriptEntry(message.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "", Data(message)))];
    }

    // ------------------------------------------------------------------------------------------------------------- tools

    private ExtensionToolDescriptor Tool(JsonObject tool)
    {
        var name = tool["name"]!.GetValue<string>();
        var exposure = tool["exposure"]?.GetValue<string>() switch
        {
            "model-only" => ToolExposure.ModelOnly, "codemode" => ToolExposure.Codemode, "deferred" => ToolExposure.Deferred, "hidden" => ToolExposure.Hidden,
            _ => ToolExposure.Direct
        };
        ToolNamespace? grouping = tool["namespace"] is JsonObject space
            ? new(space["name"]!.GetValue<string>(), space["description"]?.GetValue<string>()) { Instructions = space["instructions"]?.GetValue<string>() } : null;
        var parameters = JsonData.Parse(tool["parameters"]?.ToJsonString() ?? "{\"type\":\"object\",\"properties\":{}}");
        var hasCall = tool["hasRenderCall"]?.GetValue<bool>() == true; var hasResult = tool["hasRenderResult"]?.GetValue<bool>() == true;
        return new("tool-" + name, name, tool["description"]?.GetValue<string>() ?? "", parameters, (arguments, context, token) => ExecuteAsync(name, arguments, context, token))
        {
            Exposure = exposure, Namespace = grouping,
            DefaultActive = tool["defaultActive"] is JsonValue active ? active.GetValue<bool>() : exposure is ToolExposure.Direct or ToolExposure.ModelOnly,
            PromptGuidelines = [.. (tool["promptGuidelines"] as JsonArray ?? []).Select(item => item!.GetValue<string>())],
            ConstrainedSampling = tool["constrainedSampling"] is JsonObject sampling ? JsonData.Parse(sampling.ToJsonString()) : null,
            PrepareInitialArgumentsAsync = async (arguments, token) =>
            {
                var prepared = await host.CallAsync("tool.prepareArguments", new JsonObject { ["ext"] = extension.Index, ["name"] = name, ["args"] = Parse(arguments) }, token).ConfigureAwait(false);
                return prepared is { } value ? Data(value) : arguments;
            },
            PrepareLoadout = tool["hasPrepareLoadout"]?.GetValue<bool>() == true ? loadout => PrepareLoadout(name, loadout) : null,
            Renderers = hasCall || hasResult ? new ExtensionToolRenderers(
                tool["renderShell"]?.GetValue<string>() == "self" ? ExtensionToolRenderShell.Self : null,
                hasCall ? (context, width, token) => RenderToolAsync("call", context, null, width, token) : null,
                hasResult ? (result, context, width, token) => RenderToolAsync("result", context, result, width, token) : null) : null
        };
    }

    /// <summary>Source wrapToolDefinition + agent-loop: execute(toolCallId, params, signal, onUpdate, ctx); a thrown error becomes an
    /// error result whose text is the error message.</summary>
    private async ValueTask<JsonData> ExecuteAsync(string name, JsonData arguments, IExtensionToolContext context, CancellationToken token)
    {
        using var lease = host.Enter(context);
        var invocation = context as IExtensionToolInvocationContext;
        var updates = Task.CompletedTask; var gate = new object();
        void Progress(JsonElement partial)
        {
            if (invocation is null) return;
            var data = Data(partial);
            lock (gate) updates = updates.ContinueWith(_ => invocation.ReportUpdateAsync(data, token).AsTask(), token,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
        try
        {
            var result = await host.CallAsync("tool.execute", new JsonObject
            {
                ["ext"] = extension.Index, ["name"] = name, ["toolCallId"] = invocation?.ToolCallId ?? Guid.NewGuid().ToString("N"),
                ["params"] = Parse(arguments), ["ctx"] = lease.Id
            }, token, Progress).ConfigureAwait(false);
            Task pending; lock (gate) pending = updates;
            try { await pending.ConfigureAwait(false); } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return result is { ValueKind: JsonValueKind.Object } value ? Data(value) : JsonData.Parse("{\"content\":[],\"details\":{}}");
        }
        catch (PiNodeHostException error)
        {
            return JsonData.Parse(new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = error.Message }), ["details"] = new JsonObject(), ["isError"] = true
            }.ToJsonString());
        }
    }

    private ToolLoadoutChanges? PrepareLoadout(string name, ToolLoadout loadout)
    {
        JsonArray Tools(ImmutableArray<ToolLoadoutTool> tools) => new([.. tools.Select(tool => Parse(tool.Declaration))]);
        var parameters = new JsonObject
        {
            ["ext"] = extension.Index, ["name"] = name,
            ["loadout"] = new JsonObject
            {
                ["declared"] = Tools(loadout.Declared), ["callable"] = Tools(loadout.Callable), ["registered"] = Tools(loadout.Registered),
                ["exposures"] = new JsonObject([.. loadout.Registered.DistinctBy(tool => tool.Name).Select(tool => KeyValuePair.Create(tool.Name, (JsonNode?)(tool.Exposure switch
                {
                    ToolExposure.ModelOnly => "model-only", ToolExposure.Codemode => "codemode", ToolExposure.Deferred => "deferred", ToolExposure.Hidden => "hidden", _ => "direct"
                })))]),
                ["guidelines"] = new JsonObject([.. loadout.Registered.DistinctBy(tool => tool.Name).Select(tool => KeyValuePair.Create(tool.Name,
                    (JsonNode?)new JsonArray([.. (tool.PromptGuidelines.IsDefault ? [] : tool.PromptGuidelines).Select(text => (JsonNode)text)])))])
            }
        };
        // prepareLoadout is synchronous upstream; the Node host answers it from its own event loop.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = host.CallAsync("tool.prepareLoadout", parameters, timeout.Token).GetAwaiter().GetResult();
        if (result is not { ValueKind: JsonValueKind.Object } value) return null;
        return new ToolLoadoutChanges
        {
            Descriptions = value.TryGetProperty("descriptions", out var descriptions) && descriptions.ValueKind == JsonValueKind.Object
                ? descriptions.EnumerateObject().ToImmutableDictionary(item => item.Name, item => item.Value.GetString() ?? "", StringComparer.Ordinal)
                : ImmutableDictionary<string, string>.Empty,
            HiddenDeclarations = value.TryGetProperty("hiddenDeclarations", out var hidden) && hidden.ValueKind == JsonValueKind.Array
                ? [.. hidden.EnumerateArray().Select(item => item.GetString()!)] : []
        };
    }

    // ------------------------------------------------------------------------------------------------------------- commands

    private ExtensionCommandDescriptor Command(JsonObject command)
    {
        var name = command["name"]!.GetValue<string>();
        return new("command-" + name, name, command["description"]?.GetValue<string>() ?? "", async (arguments, context, token) =>
        {
            using var lease = host.Enter(context);
            var args = arguments.Value.ValueKind == JsonValueKind.String ? arguments.Value.GetString() ?? "" : "";
            try
            {
                await host.CallAsync("command.execute", new JsonObject { ["ext"] = extension.Index, ["name"] = name, ["args"] = args, ["ctx"] = lease.Id }, token).ConfigureAwait(false);
            }
            catch (PiNodeHostException error)
            {
                // Source runner: a command handler error is reported as an extension error ("command:<name>").
                await host.ReportAsync(extension.Path, "command:" + name, error.Message).ConfigureAwait(false);
            }
        })
        {
            SourcePath = extension.Path,
            GetArgumentCompletionsAsync = command["hasCompletions"]?.GetValue<bool>() == true ? async (prefix, token) =>
            {
                var result = await host.CallAsync("command.complete", new JsonObject { ["ext"] = extension.Index, ["name"] = name, ["prefix"] = prefix }, token).ConfigureAwait(false);
                return result is { } value ? Data(value) : JsonData.Null;
            } : null
        };
    }

    // ------------------------------------------------------------------------------------------------------------- renderers

    private async Task<ExtensionCustomComponentRows> RenderToolAsync(string slot, ExtensionToolRenderContext context, JsonData? result, int width, CancellationToken token)
    {
        var parameters = new JsonObject
        {
            ["slot"] = slot, ["toolName"] = context.ToolName, ["toolCallId"] = context.ToolCallId, ["args"] = Parse(context.Arguments), ["width"] = width,
            ["executionStarted"] = context.ExecutionStarted, ["argsComplete"] = context.ArgumentsComplete, ["isPartial"] = context.IsPartial,
            ["expanded"] = context.Expanded, ["showImages"] = context.ShowImages, ["isError"] = context.IsError, ["durationMs"] = context.DurationMs,
            ["outputPad"] = context.OutputPad
        };
        if (result is not null) parameters["result"] = Parse(result);
        var rendered = await host.CallAsync("render.tool", parameters, token).ConfigureAwait(false);
        return Rows(rendered);
    }

    internal static ExtensionCustomComponentRows Rows(JsonElement? rendered)
    {
        ImmutableArray<string> lines = rendered is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("lines", out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(line => line.GetString() ?? "")]
            : rendered is { ValueKind: JsonValueKind.Array } plain ? [.. plain.EnumerateArray().Select(line => line.GetString() ?? "")] : [];
        if (rendered is { ValueKind: JsonValueKind.Object } source && source.TryGetProperty("cellWidths", out var widths) && widths.ValueKind == JsonValueKind.Array &&
            widths.GetArrayLength() == lines.Length)
            return new(lines, [.. widths.EnumerateArray().Select(width => width.GetInt32())]);
        return new(lines, [.. lines.Select(VisibleWidth)]);
    }

    /// <summary>pi-tui visibleWidth for rows that came without widths: ANSI escape sequences take no cells, wide characters two.</summary>
    private static readonly System.Text.RegularExpressions.Regex AnsiSequence = new(
        "\u001b(?:\\[[0-?]*[ -/]*[@-~]|\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)|[@-Z\\\\-_])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static int VisibleWidth(string line)
    {
        var plain = AnsiSequence.Replace(line, "");
        var width = 0;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(plain);
        while (elements.MoveNext())
        {
            var rune = System.Text.Rune.GetRuneAt((string)elements.Current, 0);
            if (System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control) continue;
            var value = rune.Value;
            width += value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F
                or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
        }
        return width;
    }

    /// <summary>Source registerToolRenderer resolvers run in Node: a tool whose renderers Node supplies is drawn there; otherwise the
    /// remaining resolvers and the registered tool apply.</summary>
    private ExtensionToolRenderers? ResolveRenderers(string toolName, Func<ExtensionToolRenderers?> next)
    {
        JsonElement? resolved;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            resolved = host.CallAsync("render.resolve", new JsonObject { ["toolName"] = toolName }, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is PiNodeHostException or OperationCanceledException or InvalidOperationException) { return next(); }
        if (resolved is not { ValueKind: JsonValueKind.Object } value || value.TryGetProperty("handled", out var handled) && handled.ValueKind == JsonValueKind.False) return next();
        var fallback = next();
        var hasCall = value.TryGetProperty("hasRenderCall", out var call) && call.ValueKind == JsonValueKind.True;
        var hasResult = value.TryGetProperty("hasRenderResult", out var result) && result.ValueKind == JsonValueKind.True;
        return new(value.TryGetProperty("renderShell", out var shell) && shell.GetString() == "self" ? ExtensionToolRenderShell.Self : fallback?.RenderShell,
            hasCall ? (context, width, token) => RenderToolAsync("call", context, null, width, token) : fallback?.RenderCall,
            hasResult ? (data, context, width, token) => RenderToolAsync("result", context, data, width, token) : fallback?.RenderResult);
    }
}

/// <summary>Source BashOperations returned by a user_bash handler: <c>exec</c> runs in Node and streams its output back.</summary>
internal sealed class PiShellOperations(PiExtensionHost host, string callbackId) : IExtensionShellOperations
{
    public async ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ExtensionShellOutputCallback onData, CancellationToken cancellationToken)
    {
        var callId = Guid.NewGuid().ToString("N");
        using var registration = host.RegisterShellOutput(callId, onData);
        using var abort = cancellationToken.Register(() => _ = host.CallAsync("userBash.abort", new JsonObject { ["callId"] = callId }, CancellationToken.None));
        var result = await host.CallAsync("callback.invoke", new JsonObject
        {
            ["id"] = callbackId, ["args"] = new JsonObject { ["command"] = command, ["cwd"] = workingDirectory, ["callId"] = callId }
        }, CancellationToken.None).ConfigureAwait(false);
        await host.DrainShellOutputAsync(callId).ConfigureAwait(false);
        return result is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : null;
    }
}
