using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions;

public sealed record ExtensionNewSessionOptions(string? ParentSession=null,ExtensionNewSessionSetupCallback? Setup=null);
public interface IExtensionSessionSetupCommandFacade : PiSharp.Extensions.Facade.Context.IExtensionCommandFacade
{
    ValueTask<PiSharp.Extensions.Facade.Context.IExtensionCommandFacade?> NewSessionAsync(ExtensionNewSessionOptions options,
        CancellationToken token=default);
}

/// <summary>Only the actual staged manager supplies this callback-affine capability.</summary>
public delegate ValueTask ExtensionNewSessionSetupCallback(IExtensionSessionSetupManager manager, CancellationToken token);
public interface IExtensionSessionSetupManager
{
    string GetSessionId();
    string GetCwd();
    string? GetLeafId();
    JsonData GetHeader();
    ImmutableArray<JsonData> GetEntries();
    ImmutableArray<JsonData> GetBranch();
    JsonData? GetEntry(string id);
    void Branch(string entryId);
    void ResetLeaf();
    ValueTask<JsonData> AppendEntryAsync(string kind, JsonData fields, CancellationToken token = default);
    ValueTask<JsonData> AppendMessageAsync(JsonData message, CancellationToken token = default);
}
