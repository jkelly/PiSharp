using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;

namespace PiSharp.PublicSdkConsumer.R511;

/// <summary>Compiled separately against public abstractions only. No host internals or loader privileges.</summary>
public sealed class PublicConsumer : IPiSharpExtension
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HoldTool { get; init; }
    public int ToolCalls { get; private set; }
    public int InputCalls { get; private set; }
    public int CommandCalls { get; private set; }
    public int DisposalCalls { get; private set; }
    public IExtensionContext? RetainedContext { get; private set; }
    public ExtensionSessionSnapshot? RetainedView { get; private set; }
    public IExtensionUi? RetainedUi { get; private set; }
    public ExtensionUiOutcome<string>? InitialUiOutcome { get; private set; }

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        registry.RegisterTool(new("echo", "public-echo", "Public consumer echo", JsonData.EmptyObject, Echo));
        registry.RegisterCommand(new("command", "public-command", "Public consumer command", Command));
        registry.RegisterInputHandler(new("input", Input));
        return ValueTask.CompletedTask;
    }

    private async ValueTask<JsonData> Echo(JsonData value, IExtensionToolContext context, CancellationToken token)
    {
        ToolCalls++;
        RetainedContext = context;
        RetainedView = ((IExtensionSessionContext)context).SessionSnapshot;
        RetainedUi = ((IExtensionUiContext)context).Ui;
        InitialUiOutcome = await RetainedUi.InputAsync("inert consumer", cancellationToken: token);
        Entered.TrySetResult();
        if (HoldTool) await Release.Task;
        return value;
    }

    private ValueTask Command(JsonData value, IExtensionCommandContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CommandCalls++;
        RetainedContext = context;
        RetainedView = ((IExtensionSessionContext)context).SessionSnapshot;
        return ValueTask.CompletedTask;
    }

    private ValueTask<ExtensionInputPatch?> Input(ExtensionInputEvent value, IExtensionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InputCalls++;
        RetainedContext = context;
        RetainedView = ((IExtensionSessionContext)context).SessionSnapshot;
        return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, value.Text + ":public"));
    }

    public ValueTask DisposeAsync() { DisposalCalls++; return ValueTask.CompletedTask; }
}
