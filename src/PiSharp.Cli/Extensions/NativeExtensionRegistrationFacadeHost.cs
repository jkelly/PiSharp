using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Facade.Execution;

namespace PiSharp.Cli.Extensions;

/// <summary>Explicit application capability composition. The read side remains the actual attached
/// session host; mutation is supplied by an admitted application host, never inferred from mode.
/// ExtensionRegistrationCommands owns each returned original, including ignored action returns.</summary>
public sealed partial class NativeExtensionRegistrationFacadeHost : IExtensionSessionGraphReadHost,
    IExtensionContextActionHost, IExtensionRegistrationActionHost, IExtensionSettingsThinkingReadHost, IExtensionContextExecHost, IExtensionLifecycleHandoffHost, IExtensionSystemPromptOptionsReadHost
{
    private readonly NativeExtensionContextFacadeHost reads;
    private readonly IExtensionRegistrationActionHost actions;
    private readonly IExtensionContextExecHost? execution;
    public NativeExtensionRegistrationFacadeHost(NativeExtensionContextFacadeHost reads,
        IExtensionRegistrationActionHost actions, IExtensionContextExecHost? execution = null)
    {
        ArgumentNullException.ThrowIfNull(reads); ArgumentNullException.ThrowIfNull(actions);
        this.reads = reads; this.actions = actions; this.execution = execution;
    }
    public Task<ExtensionExecResult> ExecAsync(IExtensionCommandContext context, string command,
        ImmutableArray<string> arguments, ExtensionExecOptions? options = null) =>
        (execution ?? throw new NotSupportedException("No admitted execution context host.")).ExecAsync(context, command, arguments, options);
    public ValueTask<bool> SetModelAsync(ModelDescriptor model, CancellationToken token)
    { ArgumentNullException.ThrowIfNull(model); reads.ValidateRegistrationActionAttachment(token); return actions.SetModelAsync(model, token); }
    public ValueTask SendMessageAsync(ExtensionCustomMessage message, ExtensionMessageOptions? options, CancellationToken token)
    { ArgumentNullException.ThrowIfNull(message); reads.ValidateRegistrationActionAttachment(token); return actions.SendMessageAsync(message, options, token); }
    public ValueTask SendUserMessageAsync(JsonData content, ExtensionUserMessageOptions? options, CancellationToken token)
    { ArgumentNullException.ThrowIfNull(content); reads.ValidateRegistrationActionAttachment(token); return actions.SendUserMessageAsync(content, options, token); }
    public string GetCwd(IExtensionContext context) => reads.GetCwd(context);
    public JsonData? GetModel(IExtensionContext context) => reads.GetModel(context);
    public bool IsIdle(IExtensionContext context) => reads.IsIdle(context);
    public bool HasPendingMessages(IExtensionContext context) => reads.HasPendingMessages(context);
    public string GetSystemPrompt(IExtensionContext context) => reads.GetSystemPrompt(context);
    public JsonData GetSystemPromptOptions(IExtensionContext context) => reads.GetSystemPromptOptions(context);
    public JsonData GetSettings(IExtensionContext context) => reads.GetSettings(context);
    public string GetThinkingLevel(IExtensionContext context) => reads.GetThinkingLevel(context);
    public string? GetLeafId(IExtensionSessionContext context) => reads.GetLeafId(context);
    public JsonData? GetEntry(IExtensionSessionContext context, string id) => reads.GetEntry(context, id);
    public JsonData? GetLeafEntry(IExtensionSessionContext context) => reads.GetLeafEntry(context);
    public ImmutableArray<JsonData> GetEntries(IExtensionSessionContext context) => reads.GetEntries(context);
    public ImmutableArray<JsonData> GetBranch(IExtensionSessionContext context, string? id) => reads.GetBranch(context, id);
    public JsonData GetTree(IExtensionSessionContext context) => reads.GetTree(context);
    public JsonData GetHeader(IExtensionSessionContext context) => reads.GetHeader(context);
    public string? GetSessionFile(IExtensionSessionContext context) => reads.GetSessionFile(context);
    public string? GetSessionName(IExtensionSessionContext context) => reads.GetSessionName(context);
    public string? GetLabel(IExtensionSessionContext context, string id) => reads.GetLabel(context, id);
    public string GetSessionCwd(IExtensionSessionContext context) => reads.GetSessionCwd(context);
    public string? GetSessionDirectory(IExtensionSessionContext context) => reads.GetSessionDirectory(context);
    public ExtensionFacadeSessionProjection BuildSessionProjection(IExtensionSessionContext context) => reads.BuildSessionProjection(context);
    public Task WaitForIdleAsync(IExtensionCommandContext context, CancellationToken token) => reads.WaitForIdleAsync(context, token);
    public void RequestLifecycleHandoff(IExtensionCommandContext context, ExtensionLifecycleHandoffKind kind, CancellationToken token)
        => reads.RequestLifecycleHandoff(context, kind, token);
    public Task ReloadAsync(IExtensionCommandContext context, CancellationToken token) => reads.ReloadAsync(context, token);
    public Task CompactAsync(IExtensionCommandContext context, string? instructions, CancellationToken token) => reads.CompactAsync(context, instructions, token);
}
