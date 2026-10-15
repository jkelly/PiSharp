using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;

namespace PiSharp.Extensions.Agent;

internal sealed class RegisteredExtensionContextHooks
{
    private readonly RegisteredExtensionEventDispatcher dispatcher;
    private readonly ExtensionRegistry registry;
    private readonly ExtensionAgentBindingOptions options;
    private readonly CancellationToken session;

    internal RegisteredExtensionContextHooks(ExtensionRegistry registry, ExtensionAgentBindingOptions options,
        CancellationToken session)
    {
        this.options = options; this.session = session; this.registry = registry;
        dispatcher = new(registry, value => { _ = ToolResultValueCodec.Read(value, options.ResultValues); },
            options.ToolEventDispatch, options.MaximumToolEventHandlers,
            // The context messages a handler returns are admitted with the same count bound the dispatch admits them with.
            messages => AgentLoopRunner.ValidateRequestMessages(messages, options.ResultValues,
                (options.ToolEventDispatch ?? new()).MaximumContextMessages), options.RestoreSystemMessage);
    }

    internal async ValueTask<AgentPromptPreparation> BeforePromptAsync(AgentPromptStart start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        if (registry.CaptureSnapshot().BeforeAgentStartHandlers.IsEmpty) return new([]);
        if (options.ReadSystemPrompt is null || options.RestoreSystemMessage is null)
            throw new InvalidOperationException("Before-start handlers require host system-state replay.");
        // Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/agent-session.ts (_startRun, _prepareRun)
        // and extensions/runner.ts (emitBeforeAgentStart with a BeforeAgentStartTrigger): a run starts from a user message
        // (messageType "user": prompt and images) or, for sendMessage(..., { triggerTurn: true }), from a custom message (messageType
        // "custom": the message, no prompt). The CLI has one admitted user input. Low-level batch prompts use the last user as their
        // prompt event; without a user input the first custom input started the run (pending nextTurn messages follow it).
        ExtensionBeforeAgentStartEvent input;
        var systemPrompt = options.ReadSystemPrompt(start.History.AddRange(start.Inputs), token);
        if (start.Inputs.LastOrDefault(message => message.Role == "user") is { } user)
        {
            var content = user.WireBody.Value.GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()! :
                string.Join("\n", content.EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));
            var images = content.ValueKind == JsonValueKind.Array
                ? content.EnumerateArray().Where(part => part.GetProperty("type").GetString() == "image").ToArray() : Array.Empty<JsonElement>();
            input = new(text, systemPrompt, images.Length == 0 ? null : JsonData.Parse(JsonSerializer.Serialize(images)));
        }
        else if (start.Inputs.FirstOrDefault(message => message.Role == "custom") is { } custom)
            input = ExtensionBeforeAgentStartEvent.ForCustomMessage(custom.WireBody, systemPrompt);
        else return new([]);
        var result = await dispatcher.DispatchBeforeAgentStartAsync(input, token, session).ConfigureAwait(false);
        if (options.ReportEventDiagnostic is { } report)
            foreach (var diagnostic in result.Diagnostics)
            { await report(diagnostic, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested(); }
        token.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        var additions = result.Messages.Select(message => new TranscriptEntry("custom", JsonData.Parse(JsonSerializer.Serialize(new
        {
            role = "custom", customType = message.CustomType, display = message.Display,
            content = message.Content is null || message.Content.Value.ValueKind == JsonValueKind.Null ? JsonData.Parse("[]").Value : message.Content.Value, details = message.Details?.Value, timestamp = start.Timestamp
        })))).ToImmutableArray();
        return new(additions)
        {
            AfterContext = result.ForcedSystemPrompt is not { } forced ? null : (messages, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
                var current = options.RestoreSystemMessage(messages, cancellation);
                var properties = new Dictionary<string, object?> { ["role"] = "system", ["content"] = forced,
                    ["timestamp"] = start.Timestamp };
                if (current is not null)
                {
                    if (current.WireBody.Value.TryGetProperty("toolsAdded", out var tools)) properties["toolsAdded"] = tools;
                    if (current.WireBody.Value.TryGetProperty("timestamp", out var timestamp)) properties["timestamp"] = timestamp;
                }
                var head = new TranscriptEntry("system", JsonData.Parse(JsonSerializer.Serialize(properties)));
                return ValueTask.FromResult(messages.Where(message => message.Role != "system").Prepend(head).ToImmutableArray());
            }
        };
    }

    internal async ValueTask<ImmutableArray<TranscriptEntry>> TransformAsync(ImmutableArray<TranscriptEntry> messages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        ExtensionContextReduction conversation, result;
        try
        {
            // Context handlers snapshot once per model request. Removal during dispatch does not revoke admitted leases.
            conversation = await dispatcher.DispatchContextAsync(new ExtensionContextEvent(messages), cancellationToken, session).ConfigureAwait(false);
            // The source captures the second event's handlers after the first phase has completed.
            result = await dispatcher.DispatchContextWithSystemAsync(new ExtensionContextWithSystemEvent(conversation.Messages), cancellationToken, session).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken != cancellationToken)
        {
            // agent.abort() while a context handler runs aborts the request (Pi ends the run aborted). The dispatcher observes the
            // abort through a token linked to each handler's lifetime; it is still this request's cancellation.
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
        if (options.ReportEventDiagnostic is { } report)
            foreach (var diagnostic in conversation.Diagnostics.AddRange(result.Diagnostics))
            {
                await report(diagnostic, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
            }
        cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        return result.Messages;
    }
}
