using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Facade.Context;

/// <summary>Explicit host read dependencies; this interface grants no mutation authority.</summary>
public interface IExtensionContextReadHost
{
    string GetCwd(IExtensionContext context);
    JsonData? GetModel(IExtensionContext context);
    bool IsIdle(IExtensionContext context);
    bool HasPendingMessages(IExtensionContext context);
    string GetSystemPrompt(IExtensionContext context);
}

/// <summary>Callback-bound live host reads and the already captured native branch.</summary>
public interface IExtensionContextFacade
{
    string OwnerId { get; }
    long OwnerGeneration { get; }
    string Cwd { get; }
    JsonData? Model { get; }
    bool IsIdle { get; }
    bool HasPendingMessages { get; }
    string SystemPrompt { get; }
    string? SessionId { get; }
    string? LeafId { get; }
    ImmutableArray<JsonData> GetBranch();
}

public interface IExtensionCommandFacade : IExtensionContextFacade
{
    ValueTask<IExtensionCommandFacade?> NewSessionAsync(string? parentSession = null,
        CancellationToken cancellationToken = default);
    ValueTask<IExtensionCommandFacade?> ForkAsync(string entryId, bool before = true,
        CancellationToken cancellationToken = default);
    ValueTask<IExtensionCommandFacade?> SwitchSessionAsync(string absolutePath,
        CancellationToken cancellationToken = default);
}

public delegate ValueTask ExtensionFacadeCommandCallback(JsonData arguments,
    IExtensionCommandFacade context, CancellationToken cancellationToken);

/// <summary>Native status-preserving envelope for a faulted original containing an OCE.
/// An async method must not throw that OCE alone and misclassify the original as canceled.</summary>
public sealed class ExtensionFacadeOriginalFaultException : Exception
{
    public Task OriginalTask { get; }
    public AggregateException OriginalException { get; }
    public ExtensionFacadeOriginalFaultException(Task originalTask, AggregateException originalException)
        : base("The facade original task faulted; inspect its unchanged original exception graph.", originalException)
    {
        OriginalTask = originalTask;
        OriginalException = originalException;
    }
}

/// <summary>Cancellation evidence retains the actual canceled Task and its await exception/token.</summary>
public sealed class ExtensionFacadeCanceledOriginalException : OperationCanceledException
{
    public Task OriginalTask { get; }
    public ExtensionFacadeCanceledOriginalException(Task originalTask, OperationCanceledException originalException)
        : base("The facade original task was canceled.", originalException, originalException.CancellationToken)
    { OriginalTask = originalTask; }
}
