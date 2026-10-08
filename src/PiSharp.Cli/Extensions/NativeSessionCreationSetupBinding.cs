using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Extensions;

namespace PiSharp.Cli.Extensions;

/// <summary>Host-owned optional setup binding on the actual attached read host and owner.</summary>
internal sealed class NativeSessionCreationSetupBinding(ReplaceableAgentSession owner, NativeExtensionContextFacadeHost reads)
{
    internal Task<AgentSessionReplacement?> NewSessionAsync(IExtensionCommandContext context, string? parentSession,
        SessionCreationSetupCallback setup, CancellationToken token)
    {
        _ = reads.GetCwd(context);
        var attachment = reads.CaptureRegistrationActionAttachment(context.OperationCancellationToken);
        if (!ReferenceEquals(attachment, owner.Current)) throw new InvalidOperationException("Foreign setup owner.");
        owner.ValidateAttachment(attachment);
        return owner.CreateWithSetupAsync(attachment, new(AgentSessionCreationKind.New, ParentSession: parentSession), setup, token);
    }
}

/// <summary>The portable callback reaches the same native staged manager; it owns no fabricated writer.</summary>
internal sealed class NativeSessionSetupManagerAdapter(SessionCreationSetupWriter actual) : IExtensionSessionSetupManager
{
    public string GetSessionId()=>actual.GetSessionId();
    public string GetCwd()=>actual.GetCwd();
    public string? GetLeafId()=>actual.GetLeafId();
    public PiSharp.Contracts.JsonData GetHeader()=>actual.CaptureLog().Header.WireBody;
    public System.Collections.Immutable.ImmutableArray<PiSharp.Contracts.JsonData> GetEntries()=>actual.GetEntries().Select(entry=>entry.WireBody).ToImmutableArray();
    public System.Collections.Immutable.ImmutableArray<PiSharp.Contracts.JsonData> GetBranch()=>actual.GetBranch().Select(entry=>entry.WireBody).ToImmutableArray();
    public PiSharp.Contracts.JsonData? GetEntry(string id)=>actual.GetEntry(id)?.WireBody;
    public void Branch(string id)=>actual.Branch(id);
    public void ResetLeaf()=>actual.ResetLeaf();
    public async ValueTask<PiSharp.Contracts.JsonData> AppendEntryAsync(string kind,PiSharp.Contracts.JsonData fields,CancellationToken token=default)
        =>(await actual.AppendAsync(new(kind,fields),token).ConfigureAwait(false)).Entry.WireBody;
    public async ValueTask<PiSharp.Contracts.JsonData> AppendMessageAsync(PiSharp.Contracts.JsonData message,CancellationToken token=default)
        =>(await actual.AppendMessageAsync(message,token).ConfigureAwait(false)).Entry.WireBody;
}
