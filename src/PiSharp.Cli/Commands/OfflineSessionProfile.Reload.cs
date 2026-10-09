using PiSharp.Agent;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Runtime.Reloading;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private readonly object reloadGate = new();
    private NativeHostReloadAdmission? reloadAdmission;
    private readonly Dictionary<AgentSessionAttachment, ReloadAttempt> reloadAttempts = new(ReferenceEqualityComparer.Instance);
    private sealed class ReloadAttempt(NativeHostReloadCoordinator coordinator)
    {
        internal NativeHostReloadCoordinator Coordinator { get; } = coordinator;
        internal Task<NativeHostReloadReceipt>? Original;
    }

    internal void ConfigureReload(NativeHostReloadAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var owner = Sessions ?? throw new InvalidOperationException("Reload admission requires the attached profile owner.");
        AttachRuntimeView(owner);
        var attachment = owner.Current;
        if (CaptureRuntimeView(attachment).Extension is not null && !startupViewTransferred)
            throw new InvalidOperationException("Native reload requires initial activation ownership in the attached runtime lease.");
        var coordinator = new NativeHostReloadCoordinator(owner, attachment, admission.Current,
            admission.Flags, admission.HasBindings, admission.Plan, ViewReloadOperations(attachment, admission));
        lock (reloadGate)
        {
            if (reloadAdmission is not null) throw new InvalidOperationException("Reload is already admitted.");
            reloadAdmission = admission;
            reloadAttempts.Add(attachment, new(coordinator));
        }
    }

    // Returning the coordinator's task directly preserves the exact owner original for concurrent
    // callers and close. Only acknowledged publication may supply a later generation's payload.
    internal Task<NativeHostReloadReceipt> ReloadAsync(AgentSessionAttachment attachment, CancellationToken token = default)
    {
        ReloadAttempt attempt;
        lock (reloadGate)
        {
            var admission = reloadAdmission ?? throw new InvalidOperationException("This profile has no native reload admission.");
            if (!reloadAttempts.TryGetValue(attachment, out attempt!))
            {
                if (!ReferenceEquals(Sessions!.Current, attachment))
                    throw new InvalidOperationException("This is not the profile's current attachment.");
                // Publication can expose the new attachment while start/cleanup still runs.
                // Re-enter the coordinator so its owner rejects callback self-joins, rather
                // than returning a pending task directly from inside a lifecycle callback.
                var pending = reloadAttempts.Values.LastOrDefault(value => value.Original is null || !value.Original.IsCompleted);
                if (pending is not null) attempt = pending;
                else
                {
                    var receipt = reloadAttempts.Values.Select(value => value.Original)
                        .Where(task => task?.IsCompletedSuccessfully == true).Select(task => task!.Result)
                        .SingleOrDefault(value => ReferenceEquals(value.Current, attachment));
                    if (receipt?.Workflow is not { Authority: ResourceReloadAuthority.New, Candidate: { } candidate })
                        throw new InvalidOperationException("This attachment has no admitted reload provenance.");
                    attempt = new(new(Sessions!, attachment, candidate.Payload, candidate.Flags,
                        candidate.HasBindings, admission.Plan, ViewReloadOperations(attachment, admission)));
                    reloadAttempts.Add(attachment, attempt);
                }
            }
        }
        var original = attempt.Coordinator.ReloadAsync(token);
        lock (reloadGate) attempt.Original ??= original;
        return original;
    }

    internal IPromptInputAdmission InputAdmission => new ReloadInput(this, "prompt");
    // The one-shot command's plain-text path historically supplies a scalar transcript.
    // Installing reload routing alone must not change it. An actual loaded admission
    // still owns all text, not just slash commands, and reload always gets explicit refusal.
    internal IPromptInputAdmission? SelectOneShotInputAdmission(string text)
    {
        var view = CaptureRuntimeView();
        using var use = view.Lifetime.Enter();
        return IsReloadCommand(text) || view.Admission("prompt") is not null ? InputAdmission : null;
    }
    private static bool IsReloadCommand(string text) => text.StartsWith("/reload", StringComparison.Ordinal) &&
        (text.Length == 7 || char.IsWhiteSpace(text[7]));
    internal Func<string, IPromptInputAdmission> PromptInputSelector => type =>
        new ReloadInput(this, type);

    private sealed class ReloadInput(OfflineSessionProfile profile, string type) : IPromptInputAdmission
    {
        public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
        {
            input = PromptInputValue.Own(input);
            token.ThrowIfCancellationRequested();
            if (profile.CaptureRuntimeView().Extension?.Pi is { } pi) await pi.WaitForRegistrationsAsync(token).ConfigureAwait(false);
            var text = input.Text;
            var command = IsReloadCommand(text);
            if (!command)
            {
                var view = profile.CaptureRuntimeView();
                // Actual native callback/lease settlement precedes the host's post-submission hook.
                using (view.Lifetime.Enter())
                {
                    var next = view.Admission(type);
                    return next is null ? new(PromptInputAction.Continue) :
                        await next.ReduceAsync(input, token).ConfigureAwait(false);
                }
            }
            if (type != "prompt" || text.TrimEnd() != "/reload" || input.Images is not null || input.StreamingBehavior is not null)
                throw new PromptInputAdmissionException(PromptInputAdmissionFailure.InvalidInput);
            var owner = profile.Sessions ?? throw new InvalidOperationException("Reload requires an attached profile owner.");
            // The Pi entry reloads its extensions and resources in place (agent-session.ts reload()).
            bool admitted; lock (profile.reloadGate) admitted = profile.reloadAdmission is not null;
            if (!admitted && profile.SupportsPiReload)
            {
                await profile.PiReloadAsync(token).ConfigureAwait(false);
                return new(PromptInputAction.Handled);
            }
            var original = profile.ReloadAsync(owner.Current, token);
            var receipt = await original.ConfigureAwait(false);
            if (!receipt.Workflow.Failures.IsEmpty)
                throw new AggregateException("Native reload did not complete successfully.", receipt.Workflow.Failures.Select(failure => failure.Cause));
            return new(PromptInputAction.Handled);
        }
    }
}
