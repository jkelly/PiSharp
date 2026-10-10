using PiSharp.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;

namespace PiSharp.Extensions.Agent;

/// <summary>Borrowed actual binding revision; one long-lived dispatcher, no mirrored callbacks or queue.</summary>
public sealed class RegisteredExtensionInputAdmission : IPromptInputAdmission
{
    private readonly ExtensionRegistrySnapshot snapshot;
    private readonly ExtensionRegistry registry;
    private readonly RegisteredExtensionEventDispatcher dispatcher;
    private readonly CancellationToken session;
    private readonly Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportDiagnostic;

    public RegisteredExtensionInputAdmission(ExtensionRegistry registry, ExtensionRegistrySnapshot snapshot,
        ExtensionEventDispatchOptions? options = null, int maximumHandlers = 256,
        CancellationToken sessionCancellationToken = default,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(snapshot);
        this.snapshot = snapshot; this.registry = registry; session = sessionCancellationToken; this.reportDiagnostic = reportDiagnostic;
        dispatcher = new(registry, value => { _ = ToolResultValueCodec.Read(value); }, options, maximumHandlers);
    }

    public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        if (registry.Current(snapshot).InputHandlers.IsEmpty) return new(PromptInputAction.Continue);
        try
        {
            var result = await dispatcher.DispatchInputAsync(snapshot,
                new(input.Text, input.Source switch
                {
                    PromptInputSource.Interactive => ExtensionInputSource.Interactive,
                    PromptInputSource.Rpc => ExtensionInputSource.Rpc,
                    PromptInputSource.Extension => ExtensionInputSource.Extension,
                    _ => throw new PromptInputAdmissionException(PromptInputAdmissionFailure.InvalidInput)
                }, input.Images, input.StreamingBehavior switch
                {
                    null => null, PromptInputStreamingBehavior.Steer => "steer", PromptInputStreamingBehavior.FollowUp => "followUp",
                    _ => throw new PromptInputAdmissionException(PromptInputAdmissionFailure.InvalidInput)
                }), cancellationToken, session).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
            if (reportDiagnostic is not null)
                foreach (var diagnostic in result.Diagnostics)
                {
                    await reportDiagnostic(diagnostic, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
                }
            return result.Action switch
            {
                ExtensionInputAction.Handled => new(PromptInputAction.Handled),
                ExtensionInputAction.Transform => new(PromptInputAction.Transform, result.Event.Text, result.Event.Images),
                _ => new(PromptInputAction.Continue)
            };
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested || session.IsCancellationRequested || error.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new PromptInputAdmissionException(PromptInputAdmissionFailure.HandlerFailed); }
    }
}
