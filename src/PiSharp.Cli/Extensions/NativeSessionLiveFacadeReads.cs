using System.Text;
using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Sessions.Context;

namespace PiSharp.Cli.Extensions;

/// <summary>Default immutable native engine reads. No credentials, filesystem or provider acquisition.</summary>
internal sealed class NativeSessionLiveFacadeReads(Func<ReplaceableAgentSession?> owner) : IExtensionContextReadHost
{
    private (AgentSessionAttachment Attached, PersistentAgentSessionSnapshot State) Capture(IExtensionContext context)
    {
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        var source = (context as IExtensionSessionContext)?.SessionSnapshot ??
            throw new NotSupportedException("No admitted session capture is available.");
        var host = owner() ?? throw new NotSupportedException("Native facade reads have not been attached.");
        var attached = host.Current;
        host.ValidateAttachment(attached);
        var state = attached.Session.Snapshot;
        if (source.SessionId != state.Log.Header.Id || source.Generation != attached.Generation ||
            state.IsDisposed || state.IsRetired || state.Fault is not null)
            throw new InvalidOperationException("Native facade reads require their captured current attachment.");
        return (attached, state);
    }
    public string GetCwd(IExtensionContext context) => Capture(context).State.Log.Header.WireBody.Value.GetProperty("cwd").GetString()
        ?? throw new SessionFacadeMetadataUnavailableException();
    public JsonData? GetModel(IExtensionContext context)
    {
        var model = Capture(context).State.Agent.Model;
        using var bytes = new MemoryStream();
        using var writer = new Utf8JsonWriter(bytes);
        writer.WriteStartObject(); writer.WriteString("id", model.Id); writer.WriteString("api", model.Api);
        writer.WriteString("provider", model.Provider); writer.WriteEndObject(); writer.Flush();
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }
    public bool IsIdle(IExtensionContext context)
    {
        var state = Capture(context).State;
        return !state.Agent.IsRunning && !state.IsProcessingOperation && !state.IsConfiguring &&
            !state.IsAdmittingInput && !state.IsAppendingExtensionEntry && !state.IsEditingContext && !state.IsCompacting;
    }
    public bool HasPendingMessages(IExtensionContext context)
    {
        var captured = Capture(context);
        var queues = captured.Attached.Session.GetPendingInputQueueSnapshot(context.OperationCancellationToken);
        return !queues.SteeringMessages.IsEmpty || !queues.FollowUpMessages.IsEmpty || !captured.State.Agent.PendingInputs.IsEmpty;
    }
    public string GetSystemPrompt(IExtensionContext context)
        // agent-session.ts systemPrompt: the prompt of the in-memory loadout (a selection or catalog change since the last request).
        => Capture(context).Attached.Session.GetSystemPrompt(context.OperationCancellationToken);
}
