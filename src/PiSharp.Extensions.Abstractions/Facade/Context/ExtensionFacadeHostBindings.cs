using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Facade.Context;

/// <summary>Optional capability supplied by the actual admitted host context, never inferred from a mode.</summary>
public interface IExtensionFacadeHostContext : IExtensionCommandContext
{
    IExtensionContextReadHost FacadeHost { get; }
}

/// <summary>Full graph queries must use the host's existing immutable session engine and validate
/// the supplied captured attachment before and after each query. No graph is synthesized by the facade.</summary>
public interface IExtensionSessionGraphReadHost : IExtensionContextReadHost
{
    string? GetLeafId(IExtensionSessionContext context);
    JsonData? GetEntry(IExtensionSessionContext context, string entryId);
    JsonData? GetLeafEntry(IExtensionSessionContext context);
    ImmutableArray<JsonData> GetEntries(IExtensionSessionContext context);
    ImmutableArray<JsonData> GetBranch(IExtensionSessionContext context, string? entryId);
    JsonData GetTree(IExtensionSessionContext context);
    JsonData GetHeader(IExtensionSessionContext context);
    string? GetSessionFile(IExtensionSessionContext context);
    string? GetSessionName(IExtensionSessionContext context);
    string? GetLabel(IExtensionSessionContext context, string entryId);
    string GetSessionCwd(IExtensionSessionContext context);
    string? GetSessionDirectory(IExtensionSessionContext context);
    ExtensionFacadeSessionProjection BuildSessionProjection(IExtensionSessionContext context);
}

public sealed record ExtensionFacadeSessionContribution(JsonData SourceEntry, ImmutableArray<JsonData> Messages);
public sealed record ExtensionFacadeSessionProjection(ImmutableArray<JsonData> ContextEntries,
    ImmutableArray<ExtensionFacadeSessionContribution> ProjectedEntries, ImmutableArray<JsonData> Messages,
    ImmutableArray<JsonData> LlmMessages, string ThinkingLevel, string? ModelProvider, string? ModelId);

/// <summary>Each method returns the existing engine's actual Task, including typed Task results.
/// Implementations must reject stale attachment/callback reentry before effects. No wrapper or timeout Task.</summary>
public interface IExtensionContextActionHost : IExtensionContextReadHost
{
    Task WaitForIdleAsync(IExtensionCommandContext context, CancellationToken cancellationToken);
    Task ReloadAsync(IExtensionCommandContext context, CancellationToken cancellationToken);
    Task CompactAsync(IExtensionCommandContext context, string? customInstructions, CancellationToken cancellationToken);
}

public interface IExtensionSessionGraphFacade : IExtensionCommandFacade
{
    JsonData? GetEntry(string entryId);
    JsonData? GetLeafEntry();
    ImmutableArray<JsonData> GetEntries();
    ImmutableArray<JsonData> GetBranch(string? entryId);
    JsonData GetTree();
    JsonData GetHeader();
    string? SessionFile { get; }
    string? SessionName { get; }
    string? GetLabel(string entryId);
    string SessionCwd { get; }
    string? SessionDirectory { get; }
    ImmutableArray<JsonData> BuildContextEntries();
    ExtensionFacadeSessionProjection BuildSessionProjection();
}

public interface IExtensionHostActionFacade : IExtensionCommandFacade
{
    ValueTask WaitForIdleAsync(CancellationToken cancellationToken = default);
    ValueTask ReloadAsync(CancellationToken cancellationToken = default);
    /// <summary>Native awaitable adaptation of the original void compact trigger. Actual work is
    /// owned by the command even if its result is ignored. Optional JS completion callbacks remain separate.</summary>
    ValueTask CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default);
}
