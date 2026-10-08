using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PublishedConsumer;

/// <summary>A net10.0 consumer of the experimental abstractions, with no reference to the runtime or Agent.</summary>
public sealed class PublishedConsumerExtension : IPiSharpExtension
{
    public bool HoldInitialization { get; init; }
    public TaskCompletionSource InitializationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ContinueInitialization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IExtensionRegistry? Registry { get; private set; }
    public IExtensionToolContext? LastToolContext { get; private set; }
    public IExtensionCommandContext? LastCommandContext { get; private set; }
    public IExtensionContext? LastObservationContext { get; private set; }
    public int CommandCalls { get; private set; }
    public int ObservationCalls { get; private set; }
    public int DisposalCalls { get; private set; }

    public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        Registry = registry;
        registry.RegisterTool(new("tool", "sample-echo", "Echo an owned JSON value.",
            JsonData.Parse("{\"type\":\"object\"}"), (arguments, context, token) =>
            {
                token.ThrowIfCancellationRequested();
                LastToolContext = context;
                return ValueTask.FromResult(JsonData.Parse("{\"echo\":" + arguments.ToString() + "}"));
            }));
        registry.RegisterCommand(new("command", "sample-count", "Count command invocations.", (_, context, token) =>
        {
            token.ThrowIfCancellationRequested();
            LastCommandContext = context;
            CommandCalls++;
            return ValueTask.CompletedTask;
        }));
        registry.Observe(new("observer", "sample-notice", (_, context, token) =>
        {
            token.ThrowIfCancellationRequested();
            LastObservationContext = context;
            ObservationCalls++;
            return ValueTask.CompletedTask;
        }));
        InitializationEntered.TrySetResult();
        if (HoldInitialization) await ContinueInitialization.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        else await Task.Yield();
    }

    public ValueTask DisposeAsync()
    {
        DisposalCalls++;
        return ValueTask.CompletedTask;
    }
}
