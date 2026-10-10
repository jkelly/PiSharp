using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Tree;
using PiSharp.Sessions.Context;

namespace PiSharp.Cli.Extensions;

/// <summary>Explicit CLI host binding over the existing attachment, forest queries and idle original.
/// Profile composition supplies live model/prompt reads and an optional already admitted compaction engine.</summary>
public sealed partial class NativeExtensionContextFacadeHost : IExtensionSessionGraphReadHost, IExtensionContextActionHost
{
    private ReplaceableAgentSession? owner;
    private readonly IExtensionContextReadHost live;
    private readonly Func<AgentSessionAttachment, string?, CancellationToken, Task>? compact;
    public NativeExtensionContextFacadeHost()
    { live = new NativeSessionLiveFacadeReads(() => Volatile.Read(ref owner)); }
    public NativeExtensionContextFacadeHost(ReplaceableAgentSession owner, IExtensionContextReadHost live,
        Func<AgentSessionAttachment, string?, CancellationToken, Task>? compact = null)
        : this(live, compact) { Attach(owner); }
    public NativeExtensionContextFacadeHost(IExtensionContextReadHost live,
        Func<AgentSessionAttachment, string?, CancellationToken, Task>? compact = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (compact?.GetInvocationList().Length > 1) throw new ArgumentException("One admitted compaction engine is required.", nameof(compact));
        this.live = live; this.compact = compact;
    }
    public void Attach(ReplaceableAgentSession value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Interlocked.CompareExchange(ref owner, value, null) is not null)
            throw new InvalidOperationException("This facade host already has its explicit session owner.");
        BindAttachedSessionBehaviors(value);
    }
    internal void ValidateRegistrationActionAttachment(CancellationToken token)
        => CaptureRegistrationActionAttachment(token);
    internal AgentSessionAttachment CaptureRegistrationActionAttachment(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var attachedOwner = Volatile.Read(ref owner) ?? throw new NotSupportedException("The native facade host has not been attached.");
        var attached = attachedOwner.Current;
        attachedOwner.ValidateAttachment(attached);
        attached.LifetimeToken.ThrowIfCancellationRequested();
        var state = attached.Session.Snapshot;
        if (state.IsDisposed || state.IsRetired || state.Fault is not null)
            throw new InvalidOperationException("The registration action has no usable host attachment.");
        return attached;
    }
    private AgentSessionAttachment Capture(IExtensionContext context)
    {
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        var captured = (context as IExtensionSessionContext)?.SessionSnapshot ??
            throw new NotSupportedException("The admitted facade context has no captured session attachment.");
        var host = Volatile.Read(ref owner) ?? throw new NotSupportedException("The native facade host has not been attached.");
        var attached = host.Current;
        host.ValidateAttachment(attached);
        var state = attached.Session.Snapshot;
        if (captured.Generation != attached.Generation || captured.SessionId != state.Log.Header.Id ||
            state.IsDisposed || state.IsRetired || state.Fault is not null)
            throw new InvalidOperationException("The facade context no longer owns the current host attachment.");
        return attached;
    }
    internal (Action Validate, CancellationToken Lifetime) CaptureExecutionAttachment(IExtensionCommandContext context)
    {
        var exact = Capture(context);
        return (() =>
        {
            exact.LifetimeToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(exact, Capture(context)))
                throw new InvalidOperationException("Exec requires the exact originating attachment.");
        }, exact.LifetimeToken);
    }
    private T Read<T>(IExtensionContext context, Func<AgentSessionAttachment, T> read)
    {
        var attached = Capture(context);
        var result = read(attached);
        if (!ReferenceEquals(attached, Capture(context)))
            throw new InvalidOperationException("The facade read crossed a host attachment replacement.");
        return result;
    }
    public string GetCwd(IExtensionContext context) => Read(context, _ => live.GetCwd(context));
    public JsonData? GetModel(IExtensionContext context) => Read(context, _ => live.GetModel(context));
    public bool IsIdle(IExtensionContext context) => Read(context, _ => live.IsIdle(context));
    public bool HasPendingMessages(IExtensionContext context) => Read(context, _ => live.HasPendingMessages(context));
    public string GetSystemPrompt(IExtensionContext context) => Read(context, _ => live.GetSystemPrompt(context));
    public string? GetLeafId(IExtensionSessionContext context) => Read(context, attached => attached.Session.Snapshot.Context.LeafId);
    private static SessionTreeSnapshot Tree(AgentSessionAttachment attached)
        => attached.Session.CreateTreeQueries().Build(attached.Session.Snapshot.Log.Entries, attached.LifetimeToken);
    public JsonData? GetEntry(IExtensionSessionContext context, string entryId)
        => Read(context, attached => Tree(attached).GetEntry(entryId)?.WireBody);
    public JsonData? GetLeafEntry(IExtensionSessionContext context) => Read(context, attached =>
    {
        var state = attached.Session.Snapshot;
        return state.Context.LeafId is { } id ? attached.Session.CreateTreeQueries().Build(state.Log.Entries, attached.LifetimeToken).GetEntry(id)?.WireBody : null;
    });
    public ImmutableArray<JsonData> GetEntries(IExtensionSessionContext context)
        => Read(context, attached => Tree(attached).Entries.Select(entry => entry.WireBody).ToImmutableArray());
    public ImmutableArray<JsonData> GetBranch(IExtensionSessionContext context, string? entryId) => Read(context, attached =>
    {
        var state = attached.Session.Snapshot;
        return attached.Session.CreateTreeQueries().Build(state.Log.Entries, attached.LifetimeToken)
            .GetBranch(entryId ?? state.Context.LeafId, attached.LifetimeToken).Select(entry => entry.WireBody).ToImmutableArray();
    });
    public JsonData GetHeader(IExtensionSessionContext context) => Read(context, attached => attached.Session.Snapshot.Log.Header.WireBody);
    public string? GetSessionFile(IExtensionSessionContext context) => Read(context, attached => attached.Session.SessionFile);
    public string? GetSessionName(IExtensionSessionContext context) => Read(context, attached =>
    {
        var tree = Tree(attached);
        if (!tree.SessionNameAvailable) throw new SessionFacadeMetadataUnavailableException();
        return tree.SessionName;
    });
    public JsonData GetTree(IExtensionSessionContext context) => Read(context, attached => ExportTree(Tree(attached)));
    public string? GetLabel(IExtensionSessionContext context, string entryId) => Read(context, attached =>
    {
        var tree = Tree(attached);
        if (!tree.LabelsAvailable) throw new SessionFacadeMetadataUnavailableException();
        return tree.Labels.TryGetValue(entryId, out var label) ? label.Label : null;
    });
    public string GetSessionCwd(IExtensionSessionContext context) => Read(context, attached =>
        attached.Session.Snapshot.Log.Header.WireBody.Value.GetProperty("cwd").GetString() ??
            throw new SessionFacadeMetadataUnavailableException());
    public string? GetSessionDirectory(IExtensionSessionContext context) => Read(context, attached =>
        attached.Session.SessionFile is { } file ? Path.GetDirectoryName(file) : null);
    public ExtensionFacadeSessionProjection BuildSessionProjection(IExtensionSessionContext context) => Read(context, attached =>
    {
        var state = attached.Session.Snapshot;
        var projected = new SessionContextProjector().Project(state.Log.Entries, state.Context.LeafId, attached.LifetimeToken);
        return new ExtensionFacadeSessionProjection(projected.ContextEntries.Select(entry => entry.SourceEntry.WireBody).ToImmutableArray(),
            projected.ContextEntries.Select(entry => new ExtensionFacadeSessionContribution(entry.SourceEntry.WireBody,
                entry.Messages.Select(message => message.WireBody).ToImmutableArray())).ToImmutableArray(),
            projected.Messages.Select(message => message.WireBody).ToImmutableArray(),
            projected.LlmMessages.Select(message => message.WireBody).ToImmutableArray(), projected.ThinkingLevel,
            projected.Model?.Provider, projected.Model?.ModelId);
    });
    private static JsonData ExportTree(SessionTreeSnapshot tree)
    {
        if (!tree.LabelsAvailable) throw new SessionFacadeMetadataUnavailableException();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        // Iterative traversal uses the engine's validated topology; no recursion depth or new parent inference.
        var pending = new Stack<(string? Id, bool Close)>();
        writer.WriteStartArray();
        for (var index = tree.RootIds.Length - 1; index >= 0; index--) pending.Push((tree.RootIds[index], false));
        while (pending.TryPop(out var step))
        {
            if (step.Close) { writer.WriteEndArray(); writer.WriteEndObject(); continue; }
            var node = tree.ById[step.Id!];
            writer.WriteStartObject(); writer.WritePropertyName("entry");
            writer.WriteRawValue(node.Entry.WireBody.ToString(), skipInputValidation: false);
            if (node.ResolvedLabel is { } label) writer.WriteString("label", label.Label);
            writer.WritePropertyName("children"); writer.WriteStartArray();
            pending.Push((null, true));
            for (var index = node.ChildIds.Length - 1; index >= 0; index--) pending.Push((node.ChildIds[index], false));
        }
        writer.WriteEndArray(); writer.Flush();
        return JsonData.Parse(Encoding.UTF8.GetString(stream.ToArray()));
    }
    public Task WaitForIdleAsync(IExtensionCommandContext context, CancellationToken cancellationToken)
        => Capture(context).Session.WaitForIdleAsync(cancellationToken);
    public Task CompactAsync(IExtensionCommandContext context, string? customInstructions, CancellationToken cancellationToken)
    {
        var attached = Capture(context);
        return (compact ?? throw new NotSupportedException("No admitted summary generator/policy has been bound."))(
            attached, customInstructions, cancellationToken);
    }
    public Task ReloadAsync(IExtensionCommandContext context, CancellationToken cancellationToken)
    {
        _ = Capture(context); cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Reload from this command would retire its originating runtime; an owned handoff bridge is required.");
    }
}

public sealed class SessionFacadeMetadataUnavailableException : Exception
{
    public SessionFacadeMetadataUnavailableException() : base("The native forest cannot supply supported session-name metadata.") { }
}
