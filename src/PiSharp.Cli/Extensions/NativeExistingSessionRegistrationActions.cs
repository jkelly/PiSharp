using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Cli.Extensions;

/// <summary>Actual admitted session delivery and optional captured template policy. No provider acquisition,
/// extension-state substitution or implicit late-model installation is performed by this binding.</summary>
public sealed class NativeExistingSessionRegistrationActions : IExtensionRegistrationActionHost
{
    private readonly NativeExtensionContextFacadeHost reads;
    private readonly Func<IPromptInputAdmission> inputAdmission;
    private readonly Func<IPromptInputAdmission>? expandedInputAdmission;
    public NativeExistingSessionRegistrationActions(NativeExtensionContextFacadeHost reads,
        Func<IPromptInputAdmission> inputAdmission, Func<IPromptInputAdmission>? expandedInputAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(reads); ArgumentNullException.ThrowIfNull(inputAdmission);
        if (inputAdmission.GetInvocationList().Length != 1) throw new ArgumentException("One admitted input pipeline required.", nameof(inputAdmission));
        this.reads = reads; this.inputAdmission = inputAdmission;
        if (expandedInputAdmission is not null && expandedInputAdmission.GetInvocationList().Length != 1)
            throw new ArgumentException("One admitted resource policy required.", nameof(expandedInputAdmission));
        this.expandedInputAdmission = expandedInputAdmission;
    }
    /// <summary>The admission bounds of an extension's user message; null keeps the session defaults.</summary>
    public PromptInputAdmissionOptions? InputLimits { get; init; }
    public ValueTask<bool> SetModelAsync(ModelDescriptor model, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(model);
        var attachment = reads.CaptureRegistrationActionAttachment(token);
        try { _ = attachment.Session.GetSupportedThinkingLevels(model); }
        catch (SessionRuntimeRegistryException error) when (error.Failure == SessionRuntimeRegistryFailure.UnknownModel)
        { return ValueTask.FromResult(false); }
        // agent-session.ts setModel: the per-model or default thinking level from the settings (else the current one), clamped to the
        // new model by setThinkingLevel.
        var thinking = reads.ModelSwitchThinkingLevel(model);
        return new(Own(attachment, token, work => attachment.Session.ConfigureAsync(new(Model: model, ThinkingLevel: thinking), work), _ => true));
    }
    public ValueTask SendUserMessageAsync(JsonData content, ExtensionUserMessageOptions? options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(content);
        var attachment = reads.CaptureRegistrationActionAttachment(token);
        var behavior = options?.DeliverAs switch
        {
            null or ExtensionMessageDelivery.Steer => PromptInputStreamingBehavior.Steer,
            ExtensionMessageDelivery.FollowUp => PromptInputStreamingBehavior.FollowUp,
            _ => throw new ArgumentException("Pinned user delivery admits only steering or follow-up.", nameof(options))
        };
        var admission = options?.ExpandPromptTemplates == true
            ? expandedInputAdmission?.Invoke() ?? throw new NotSupportedException("Expanded input requires the actual admitted resource catalog.")
            : inputAdmission();
        if (admission is null) throw new NotSupportedException("The native input pipeline has not been bound.");
        var input = NormalizeUserContent(content, behavior, InputLimits);
        return new(Own(attachment, token, work => attachment.Session.SubmitInputAsync(input, admission, InputLimits, work), _ => true));
    }
    public ValueTask SendMessageAsync(ExtensionCustomMessage message, ExtensionMessageOptions? options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(message);
        var attachment = reads.CaptureRegistrationActionAttachment(token);
        var delivery = options?.DeliverAs switch
        {
            null or ExtensionMessageDelivery.Steer => SessionCustomMessageDelivery.Steer,
            ExtensionMessageDelivery.FollowUp => SessionCustomMessageDelivery.FollowUp,
            ExtensionMessageDelivery.NextTurn => SessionCustomMessageDelivery.NextTurn,
            _ => throw new ArgumentException("Invalid custom delivery.", nameof(options))
        };
        var sessionId = attachment.Session.Snapshot.Log.Header.Id;
        return new(Own(attachment, token, work => attachment.Session.SendCustomMessageAsync(sessionId,
            new(message.CustomType, message.Content, message.Display, message.Details), delivery, options?.TriggerTurn, work), _ => true));
    }
    private static PromptInput NormalizeUserContent(JsonData content, PromptInputStreamingBehavior behavior, PromptInputAdmissionOptions? limits)
    {
        if (content.Value.ValueKind == JsonValueKind.String)
            return PromptInputValue.Own(new(content.Value.GetString()!, PromptInputSource.Extension, StreamingBehavior: behavior), limits);
        if (content.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("User content must be text or text/image blocks.", nameof(content));
        var texts = new List<string>(); var images = new List<JsonElement>();
        foreach (var part in content.Value.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var kind)) throw new ArgumentException("Invalid user block.", nameof(content));
            if (kind.GetString() == "text") texts.Add(part.GetProperty("text").GetString() ?? throw new ArgumentException("Invalid text block."));
            else if (kind.GetString() == "image") images.Add(part.Clone());
            else throw new ArgumentException("Invalid user block type.", nameof(content));
        }
        return PromptInputValue.Own(new(string.Join("\n", texts), PromptInputSource.Extension,
            images.Count == 0 ? null : JsonData.Parse(JsonSerializer.Serialize(images)), behavior), limits);
    }
    private static async Task<TResult> Own<TOriginal, TResult>(AgentSessionAttachment attachment, CancellationToken token,
        Func<CancellationToken, Task<TOriginal>> invoke, Func<TOriginal, TResult> project)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, attachment.LifetimeToken);
        Task<TOriginal>? original = null; TResult result = default!; Exception? failure = null, disposalFailure = null;
        try
        {
            original = invoke(cancellation.Token) ?? throw new InvalidOperationException("The admitted session engine returned no original.");
            result = project(await original.ConfigureAwait(false));
        }
        catch (OperationCanceledException error) when (original is { IsCanceled: true })
        { failure = new ExtensionProviderCanceledOriginalException(original, error); }
        catch (Exception error)
        { failure = new ExtensionProviderOriginalFaultException("existing-session-action", original, original?.Exception ?? error); }
        finally
        {
            try { cancellation.Dispose(); }
            catch (Exception error) { disposalFailure = error; }
        }
        if (failure is not null && disposalFailure is not null)
            throw new AggregateException("Actual session original and linked lifetime disposal failed.", failure, disposalFailure);
        if (disposalFailure is not null) throw new ExtensionProviderOriginalFaultException("linked-lifetime-dispose", original, disposalFailure);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}
