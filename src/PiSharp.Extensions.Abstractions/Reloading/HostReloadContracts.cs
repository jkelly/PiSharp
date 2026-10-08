using System.Collections.Immutable;

namespace PiSharp.Extensions.Abstractions.Reloading;

/// <summary>The original extension flag domain: boolean or string, including false and empty string.</summary>
public sealed record HostReloadFlagValue
{
    public bool? Boolean { get; }
    public string? Text { get; }
    public HostReloadFlagValue(bool value) => Boolean = value;
    public HostReloadFlagValue(string value) => Text = value ?? throw new ArgumentNullException(nameof(value));
}

public enum HostReloadToolExposure { Direct, ModelOnly, Hidden, Codemode, Deferred }
public sealed record HostReloadTool(string Name, bool IsExtension, HostReloadToolExposure Exposure,
    bool DefaultActive = true);

/// <summary>Captured host-approved metadata; no discovery, settings mutation or registry publication occurs here.</summary>
public sealed class HostReloadRuntime
{
    public ImmutableDictionary<string, HostReloadFlagValue> FlagDefaults { get; }
    public ImmutableArray<HostReloadTool> Tools { get; }
    public HostReloadRuntime(IEnumerable<KeyValuePair<string, HostReloadFlagValue>> flagDefaults,
        IEnumerable<HostReloadTool> tools)
    {
        FlagDefaults = HostReloadCapture.Flags(flagDefaults);
        ArgumentNullException.ThrowIfNull(tools);
        var result = ImmutableArray.CreateBuilder<HostReloadTool>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (result.Count == 4096 || tool is null || !HostReloadCapture.Valid(tool.Name) ||
                !Enum.IsDefined(tool.Exposure) || !names.Add(tool.Name))
                throw new ArgumentException("Invalid, duplicate or excessive runtime tools.", nameof(tools));
            result.Add(tool);
        }
        Tools = result.ToImmutable();
    }
}

/// <summary>One payload for settings, resources, runtime, registry and session. The host owns its concrete type
/// and must keep it unexposed until its shared publication owner commits the complete generation.</summary>
public sealed class HostReloadGeneration<TPayload> where TPayload : class
{
    public long Id { get; }
    public TPayload Payload { get; }
    public ImmutableDictionary<string, HostReloadFlagValue> Flags { get; }
    public ImmutableArray<string> ActiveTools { get; }
    public bool HasBindings { get; }
    public HostReloadGeneration(long id, TPayload payload,
        IEnumerable<KeyValuePair<string, HostReloadFlagValue>> flags, IEnumerable<string> activeTools, bool hasBindings)
    {
        if (id < 0) throw new ArgumentOutOfRangeException(nameof(id));
        ArgumentNullException.ThrowIfNull(payload);
        Id = id; Payload = payload; Flags = HostReloadCapture.Flags(flags);
        ActiveTools = HostReloadCapture.Names(activeTools); HasBindings = hasBindings;
    }
}

public enum HostReloadPublicationAuthority { New, None, Unknown }
/// <summary>Compare-and-publish acknowledgement from the shared host owner. Unknown retains candidate ownership
/// for recovery. None must include the original rejection. Generation IDs must match this exact attempt.</summary>
public sealed record HostReloadPublicationReceipt(long ExpectedGeneration, long CandidateGeneration,
    HostReloadPublicationAuthority Authority, Exception? Failure = null);

/// <summary>Failure of an admitted original callback task. OriginalTaskException is the exact completed task's
/// aggregate, retaining all original fault objects. A genuinely cancelled task has no aggregate. An OCE in a
/// faulted task does not imply cancellation. Synchronous invocation faults are reported directly instead.</summary>
public sealed class HostReloadCallbackFailure : Exception
{
    public bool OriginalTaskIsCanceled { get; }
    public AggregateException? OriginalTaskException { get; }
    public Exception AwaitedCause { get; }
    public HostReloadCallbackFailure(bool originalTaskIsCanceled, AggregateException? originalTaskException, Exception awaitedCause)
        : base("The original host reload callback task failed.", originalTaskException ?? awaitedCause)
    {
        OriginalTaskIsCanceled = originalTaskIsCanceled; OriginalTaskException = originalTaskException;
        AwaitedCause = awaitedCause;
    }
}

/// <summary>All callbacks borrow an already admitted host reservation. Preparation mutates only the returned
/// candidate payload. Each delegate must have exactly one target. StageSettings must clean any acquisition it cannot return.
/// InvalidateAndDrain joins original callbacks and rejects old contexts. Publish performs one
/// host-owned atomic registry/session compare-and-publish; it must not implement a parallel registry pipeline.</summary>
public sealed class HostReloadOperations<TPayload> where TPayload : class
{
    public required Func<CancellationToken, ValueTask<TPayload>> StageSettingsAsync { get; init; }
    public required Func<TPayload, CancellationToken, ValueTask> SyncQueueModesAsync { get; init; }
    public required Func<TPayload, CancellationToken, ValueTask> ResetApiProvidersAsync { get; init; }
    public required Func<TPayload, CancellationToken, ValueTask> ReloadResourcesAsync { get; init; }
    public required Func<TPayload, CancellationToken, ValueTask<HostReloadRuntime>> DescribeRuntimeAsync { get; init; }
    public required Func<HostReloadGeneration<TPayload>, CancellationToken, ValueTask> BuildRuntimeAsync { get; init; }
    public required Func<HostReloadGeneration<TPayload>, string, CancellationToken, ValueTask> SessionShutdownAsync { get; init; }
    public required Func<HostReloadGeneration<TPayload>, ValueTask> InvalidateAndDrainAsync { get; init; }
    public required Func<HostReloadGeneration<TPayload>, HostReloadGeneration<TPayload>, ValueTask<HostReloadPublicationReceipt>> PublishAsync { get; init; }
    public required Func<TPayload, ValueTask> CleanupAsync { get; init; }
    public required Func<HostReloadGeneration<TPayload>, CancellationToken, ValueTask> BeforeSessionStartAsync { get; init; }
    public required Func<HostReloadGeneration<TPayload>, string, CancellationToken, ValueTask> SessionStartAsync { get; init; }
    public required Func<HostReloadGeneration<TPayload>, CancellationToken, ValueTask> ReportUnhandledMcpServersAsync { get; init; }
    public required Func<HostReloadGeneration<TPayload>, string, CancellationToken, ValueTask> ExtendResourcesAsync { get; init; }
}

internal static class HostReloadCapture
{
    internal static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048;
    internal static ImmutableArray<string> Names(IEnumerable<string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = ImmutableArray.CreateBuilder<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); var count = 0;
        foreach (var name in source)
        {
            if (!Valid(name) || ++count > 4096) throw new ArgumentException("Invalid or excessive names.", nameof(source));
            if (seen.Add(name)) result.Add(name);
        }
        return result.ToImmutable();
    }
    internal static ImmutableDictionary<string, HostReloadFlagValue> Flags(IEnumerable<KeyValuePair<string, HostReloadFlagValue>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = ImmutableDictionary.CreateBuilder<string, HostReloadFlagValue>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (result.Count == 4096 || !Valid(item.Key) || item.Value is null || result.ContainsKey(item.Key))
                throw new ArgumentException("Invalid, duplicate or excessive flags.", nameof(source));
            result.Add(item.Key, item.Value);
        }
        return result.ToImmutable();
    }
}
