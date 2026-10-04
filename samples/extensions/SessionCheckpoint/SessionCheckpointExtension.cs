using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace SessionCheckpoint;

/// <summary>One ordinary published native owner across session replacements, with no retained session store.</summary>
public sealed class SessionCheckpointExtension : IPiSharpExtension
{
    private IExtensionSessionActionsContext? retained;
    private readonly AsyncLocal<bool> skipConfirmation = new();
    private readonly string? markers = Environment.GetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS");
    private void Mark(string text)
    {
        if (!string.IsNullOrEmpty(markers)) File.AppendAllText(Path.Combine(markers, "SessionCheckpoint.markers"), text + "\n");
    }
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Mark("initialize");
        if (registry is not IExtensionSessionLifecycleRegistry lifecycle ||
            !registry.Features.Contains(ExtensionSessionActionFeatures.DurableEntries) ||
            !registry.Features.Contains(ExtensionSessionActionFeatures.Replacement))
            throw new InvalidOperationException("Checkpoint sample requires durable session entries and replacement.");
        registry.RegisterCommand(new("checkpoint", "checkpoint", "Save a checkpoint and optionally switch sessions", CheckpointAsync));
        registry.Observe(new("session-start", "session_start", StartAsync));
        lifecycle.RegisterSessionSwitchHandler(new("before-switch", BeforeSwitchAsync));
        if (registry is IExtensionSessionCreationRegistry creation && registry.Features.Contains(ExtensionSessionActionFeatures.Creation))
        {
            registry.RegisterCommand(new("checkpoint-create", "checkpoint-create", "Create, fork or clone a durable session and checkpoint its fresh context", CreateAsync));
            creation.RegisterSessionCreationHandler(new("before-create", BeforeCreationAsync));
        }
        return ValueTask.CompletedTask;
    }

    private async ValueTask StartAsync(JsonData observation, IExtensionContext context, CancellationToken token)
    {
        if (context is not IExtensionSessionContext { SessionSnapshot: { } snapshot } || context is not IExtensionUiContext ui) return;
        Mark("session-start:" + snapshot.Generation);
        if (observation.Value.GetProperty("reason").GetString() == "resume" && ui.Ui.Capabilities.Supports(ExtensionUiFeature.Confirm))
            await ui.Ui.ConfirmAsync("Resume checkpoint session", "Continue with session " + snapshot.SessionId + "?", cancellationToken: token);
        await ui.Ui.PublishAsync(new ExtensionUiNotify("Session " + snapshot.SessionId + " ready", ExtensionUiNotifyKind.Info), token);
    }

    private async ValueTask<ExtensionSessionSwitchDecision> BeforeSwitchAsync(ExtensionSessionSwitchEvent proposal,
        IExtensionContext context, CancellationToken token)
    {
        if (skipConfirmation.Value) return ExtensionSessionSwitchDecision.Continue;
        if (context is not IExtensionUiContext ui) return ExtensionSessionSwitchDecision.Cancel;
        var reply = await ui.Ui.ConfirmAsync("Switch checkpoint session", "Open session " + proposal.TargetSessionId + "?", cancellationToken: token);
        return reply.Kind == ExtensionUiOutcomeKind.Value && reply.Value
            ? ExtensionSessionSwitchDecision.Continue : ExtensionSessionSwitchDecision.Cancel;
    }

    private async ValueTask CheckpointAsync(JsonData arguments, IExtensionCommandContext context, CancellationToken token)
    {
        if (context is not IExtensionSessionCommandContext current || current.SessionSnapshot is not { } original)
            throw new InvalidOperationException("Checkpoint command requires a live session context.");
        var text = arguments.Value.GetString() ?? ""; string? target = string.IsNullOrWhiteSpace(text) ? null : text;
        string? leaf = null, returnPath = null; var roundTrip = false; var label = "checkpoint";
        var confirmSwitch = true; var confirmCheckpoint = false;
        if (text.TrimStart().StartsWith('{'))
        {
            var request = JsonData.Parse(text).Value;
            target = request.TryGetProperty("target", out var path) ? path.GetString() : null;
            leaf = request.TryGetProperty("leafId", out var selected) ? selected.GetString() : null;
            roundTrip = request.TryGetProperty("roundTrip", out var round) && round.GetBoolean();
            returnPath = request.TryGetProperty("returnPath", out var returning) ? returning.GetString() : null;
            label = request.TryGetProperty("label", out var value) ? value.GetString() ?? label : label;
            confirmSwitch = !request.TryGetProperty("confirmSwitch", out var confirmation) || confirmation.GetBoolean();
            confirmCheckpoint = request.TryGetProperty("confirmCheckpoint", out var checkpoint) && checkpoint.GetBoolean();
        }
        if (roundTrip && (returnPath is null || !Path.IsPathFullyQualified(returnPath)))
            throw new InvalidOperationException("Round trip requires an explicit absolute returnPath.");
        var priorRetained = retained; retained = current;
        skipConfirmation.Value = !confirmSwitch;
        await Save(current, "before-switch", label, token);
        if (target is null) return;
        var fresh = await current.SwitchSessionAsync(target, useLatestLeaf: leaf is null, selectedLeafId: leaf, cancellationToken: token);
        if (fresh is null)
        { await current.Ui.PublishAsync(new ExtensionUiNotify("Session switch cancelled", ExtensionUiNotifyKind.Info), token); return; }
        if (confirmCheckpoint)
        {
            var approval = await fresh.Ui.ConfirmAsync("Save replacement checkpoint", "Save in the newly attached session?", cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (approval.Kind != ExtensionUiOutcomeKind.Value || !approval.Value) return;
        }
        await Save(fresh, "after-switch", label, token);
        var staleRejected = await RejectStale(current, token);
        if (roundTrip)
        {
            // The original A callback remains active while its physical writer has retired. Returning to A
            // must produce a new attachment, even though the durable session ID is identical.
            var back = await fresh.SwitchSessionAsync(returnPath!, cancellationToken: token);
            if (back is null) throw new InvalidOperationException("Round-trip return was cancelled after the first committed switch.");
            await Save(back, "after-return", label, token);
            staleRejected &= await RejectStale(current, token);
            if (priorRetained is not null) staleRejected &= await RejectStale(priorRetained, token);
            await back.Ui.PublishAsync(new ExtensionUiNotify("Checkpoint round trip complete; stale context rejected=" + staleRejected,
                ExtensionUiNotifyKind.Info), token);
        }
        else await fresh.Ui.PublishAsync(new ExtensionUiNotify("Checkpoint saved; stale context rejected=" + staleRejected,
            ExtensionUiNotifyKind.Info), token);
        if (!staleRejected) throw new InvalidOperationException("Retained context unexpectedly admitted a write.");

    }

    private async ValueTask<ExtensionSessionSwitchDecision> BeforeCreationAsync(ExtensionSessionCreationEvent proposal,
        IExtensionContext context, CancellationToken token)
    {
        if (skipConfirmation.Value) return ExtensionSessionSwitchDecision.Continue;
        if (context is not IExtensionUiContext ui) return ExtensionSessionSwitchDecision.Cancel;
        var reply = await ui.Ui.ConfirmAsync("Create checkpoint session", "Create " + proposal.Kind + " from session " +
            proposal.PreviousSessionId + "?", cancellationToken: token);
        return reply.Kind == ExtensionUiOutcomeKind.Value && reply.Value
            ? ExtensionSessionSwitchDecision.Continue : ExtensionSessionSwitchDecision.Cancel;
    }
    private async ValueTask CreateAsync(JsonData arguments, IExtensionCommandContext context, CancellationToken token)
    {
        if (context is not IExtensionSessionCreationCommandContext current || current.SessionSnapshot is null)
            throw new InvalidOperationException("Creation command requires a live session lifecycle context.");
        var text = arguments.Value.GetString() ?? "";
        var request = text.Length == 0 ? JsonData.Parse("{}").Value : JsonData.Parse(text).Value;
        var kind = request.TryGetProperty("kind", out var value) ? value.GetString() : "new";
        var selectedKind = kind switch { "new" => ExtensionSessionCreationKind.New, "before" => ExtensionSessionCreationKind.ForkBefore,
            "at" => ExtensionSessionCreationKind.ForkAt, "clone" => ExtensionSessionCreationKind.Clone,
            _ => throw new InvalidOperationException("Unsupported creation kind.") };
        var entry = request.TryGetProperty("entryId", out var selected) ? selected.GetString() : null;
        var parent = request.TryGetProperty("parentSession", out var parentValue) ? parentValue.GetString() : null;
        skipConfirmation.Value = request.TryGetProperty("confirm", out var confirm) && !confirm.GetBoolean();
        var result = await current.CreateSessionAsync(new(selectedKind, entry, parent), token);
        if (result is null)
        { await current.Ui.PublishAsync(new ExtensionUiNotify("Session creation cancelled", ExtensionUiNotifyKind.Info), token); return; }
        var fresh = result.Context;
        if (request.TryGetProperty("confirmCheckpoint", out var checkpoint) && checkpoint.GetBoolean())
        {
            var reply = await fresh.Ui.ConfirmAsync("Save created checkpoint", "Save in the newly created session?", cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (reply.Kind != ExtensionUiOutcomeKind.Value || !reply.Value) return;
        }
        await Save(fresh, "after-create", kind ?? "new", token);
        var rejected = await RejectStale(current, token);
        if (result.SelectedText is { } selectedText && fresh.Ui.Capabilities.Supports(ExtensionUiFeature.EditorText))
            await fresh.Ui.PublishAsync(new ExtensionUiEditorText(selectedText), token);
        await fresh.Ui.PublishAsync(new ExtensionUiNotify("Session created; stale context rejected=" + rejected,
            ExtensionUiNotifyKind.Info), token);
        if (!rejected) throw new InvalidOperationException("Retained creation context admitted a write.");
    }

    private static async ValueTask Save(IExtensionSessionCommandContext context, string phase, string label, CancellationToken token)
    {
        var snapshot = context.SessionSnapshot ?? throw new InvalidOperationException("No selected branch.");
        var restored = snapshot.BranchEntries.Count(entry => entry.Value.TryGetProperty("customType", out var kind) &&
            kind.GetString() == "pisharp.extension-state" && entry.Value.GetProperty("data").GetProperty("extensionId").GetString() == context.OwnerId);
        await context.AppendSessionEntryAsync("checkpoint", 1, JsonData.Parse(JsonSerializer.Serialize(new
        { phase, label, restored, sessionId = snapshot.SessionId, generation = snapshot.Generation, selectedLeafId = snapshot.SelectedLeafId })), token);
    }
    private static async ValueTask<bool> RejectStale(IExtensionSessionActionsContext context, CancellationToken token)
    {
        try { await context.AppendSessionEntryAsync("checkpoint", 1, JsonData.Parse("{\"phase\":\"stale\"}"), token); return false; }
        catch (InvalidOperationException) { return true; }
        catch (OperationCanceledException) { return true; }
    }
    public ValueTask DisposeAsync() { retained = null; Mark("dispose"); return ValueTask.CompletedTask; }
}
