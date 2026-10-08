using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Cli.Prompts;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private readonly object originalPromptHookGate = new();
    private readonly ConditionalWeakTable<SessionModelBinding, PromptBinding> originalPromptBindings = new();
    private sealed class PromptBinding(SessionModelBinding decorated) { internal SessionModelBinding Decorated = decorated; }
    internal SessionModelBinding DecorateOriginalPromptBinding(SessionModelBinding binding)
    {
        lock (originalPromptHookGate)
        {
            if (originalPromptBindings.TryGetValue(binding, out var known)) return known.Decorated;
            var old = binding.Hooks ?? new();
            var decorated = binding with { Hooks = old with { TransformRequestMessages = async (messages, token) =>
            {
                token.ThrowIfCancellationRequested();
                var snapshot = Sessions is { } owner ? CaptureOriginalSystemPrompt(owner.Current) : startupOriginalPrompt;
                var forced = !snapshot.NativeLiteralBaseline && snapshot.Input.ForceSystemPrompt is not null;
                if (!snapshot.NativeLiteralBaseline && !forced) messages = Project(messages);
                if (old.TransformRequestMessages is not null)
                {
                    Task<ImmutableArray<TranscriptEntry>>? original = null;
                    try
                    {
                        original = old.TransformRequestMessages(messages, token).AsTask();
                        messages = await original.ConfigureAwait(false);
                    }
                    catch (Exception direct)
                    {
                        var aggregate = original?.Exception;
                        // Only a genuinely canceled acquired original with the exact
                        // active requested token keeps canceled wrapper semantics.
                        // Faulted OCE and synchronous factory OCE remain faults.
                        if (original is { IsCanceled: true } && direct is OperationCanceledException canceled &&
                            canceled.CancellationToken == token && token.IsCancellationRequested)
                            throw new OriginalPromptHookCancellation(original, direct, token);
                        throw new OriginalPromptHookFailure(original, aggregate, direct);
                    }
                }
                // The original forced projection runs AFTER the previous context
                // hook, and takes inert declarations from its transformed result.
                return forced ? Project(messages) : messages;

                ImmutableArray<TranscriptEntry> Project(ImmutableArray<TranscriptEntry> currentMessages)
                {
                    var message = new TranscriptEntry("system", OriginalSystemPromptBuilder.Message(snapshot, 0));
                    var current = new SessionSystemReplay().Replay(currentMessages, token).CurrentMessage;
                    if (current is not null)
                    {
                        var body = JsonNode.Parse(message.WireBody.ToString())!.AsObject();
                        foreach (var property in current.WireBody.Value.EnumerateObject())
                            if (property.Name is not ("role" or "content" or "sections" or "timestamp"))
                                body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                        message = new("system", JsonData.Parse(body.ToJsonString()));
                    }
                    return currentMessages.Where(row => row.Role != "system").Prepend(message).ToImmutableArray();
                }
            } } };
            var marker = new PromptBinding(decorated); originalPromptBindings.Add(binding, marker); originalPromptBindings.Add(decorated, marker);
            return decorated;
        }
    }
    private void RequireOriginalPromptRegistry(SessionRuntimeRegistry registry)
    {
        if (!registry.UsesPromptSectionPreparation(PrepareDurablePromptSections))
            throw new InvalidOperationException("Prepared registry must carry the exact profile prompt-section admission.");
        // WithToolCatalog may share a model holder with the live predecessor. Staging
        // never CAS-publishes into that holder merely to install a hook.
        var captured = registry.CaptureModelCatalog();
        lock (originalPromptHookGate)
            foreach (var binding in captured.Bindings)
                if (!originalPromptBindings.TryGetValue(binding, out var marker) || !ReferenceEquals(marker.Decorated, binding))
                    throw new InvalidOperationException("Prepared registry must already carry this profile's admitted prompt decorator.");
    }
    internal sealed class OriginalPromptHookCancellation(Task original, Exception direct, CancellationToken token)
        : OperationCanceledException("Admitted original prompt hook was genuinely canceled.", direct, token)
    {
        internal Task Original { get; } = original;
        internal AggregateException? Aggregate => null;
        internal Exception Direct { get; } = direct;
    }
    internal sealed class OriginalPromptHookFailure(Task? original, AggregateException? aggregate, Exception direct)
        : Exception("Admitted original prompt hook failed.", aggregate ?? direct)
    {
        internal Task? Original { get; } = original;
        internal AggregateException? Aggregate { get; } = aggregate;
        internal Exception Direct { get; } = direct;
    }
}
