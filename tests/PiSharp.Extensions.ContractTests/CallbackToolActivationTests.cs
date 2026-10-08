using System.Collections.Immutable;
using System.Reflection;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Context;

internal static partial class ToolActivationTests
{
    private static IEnumerable<(string Name, Func<Task> Run)> CallbackActivationCases() =>
    [
        (Prefix + "callback selection publishes matching next-request scheduler through mandatory policy", CallbackNextRequest),
        (Prefix + "cancelled batch joins ignored child and retains selection for next prompt", CallbackCancelledBatch),
        (Prefix + "post-ack cancellation retains actual checkpoint and matching Agent selection", CallbackAfterAcknowledgment),
        (Prefix + "host setter supersedes pending proposal including no-op durable selection", HostSupersedesProposal),
        (Prefix + "navigation validates epoch restores pending state and returns usable post-epoch revision", NavigationActivationEpoch),
        (Prefix + "navigation restoration overflow fails before Agent or context publication", NavigationEpochOverflow),
        (Prefix + "parallel prepared proposals cannot overwrite newer accepted selection", ConcurrentActivationProposals),
        (Prefix + "preparation reentrancy and cancellation preserve prior logical selection", ActivationPreparationGuards),
        (Prefix + "retained native callback and retired attachment cannot schedule effects", ClosedActivationContext),
        (Prefix + "registration teardown joins original preparation lease before cancellation returns", ActivationLeaseCancellation),
        (Prefix + "request boundary retries changed selection before admitting any checkpoint", ActivationBoundaryRevisionRetry),
        (Prefix + "queued steering receives activation checkpoint before its user input", CallbackQueuedNextRequest),
        (Prefix + "pending system content survives while stale tool intent cannot undo activation", PendingSystemToolIntent)
    ];

    private static async Task CallbackNextRequest()
    {
        var provider = new ActivationProvider();
        await using var fixture = await Fixture.Create(views: provider);
        Check(fixture.Extensions.AvailableFeatures.Contains(ExtensionToolActivationFeatures.Feature), "Host capability was not advertised.");
        var session = await fixture.New("callback");
        await using (var owner = fixture.Owner(session))
        {
            fixture.Source.SelectTool = index => index switch { 0 => "outer", 1 => "inactive", 2 => "code", 3 => "denied", _ => null };
            fixture.Outer = async (context, _) =>
            {
                fixture.Retained = context;
                var activation = (IExtensionToolActivationContext)context;
                Names(["direct", "outer"], activation.GetActiveTools());
                var invalidRejected = false;
                try { activation.SetActiveTools(default); } catch (ExtensionRegistrationException) { invalidRejected = true; }
                Check(invalidRejected, "Malformed SDK selection bypassed descriptor input bounds.");
                var selected = activation.SetActiveTools(["missing", "hidden", "inactive", "code", "denied", "inactive"]);
                Names(["inactive", "code", "denied"], selected.Names);
                Names(selected.Names, activation.GetActiveTools());
                Names(["direct", "code", "deferred", "denied"], context.Tools);
                Check((await context.ExecuteToolAsync("inactive", JsonData.EmptyObject)).IsError, "Setter expanded the current invocation's broker.");
                return Result("selected");
            };
            await session.PromptAsync(Input());
            Names(["direct", "outer"], Declared(fixture.Source.Requests[0]));
            foreach (var request in fixture.Source.Requests.Skip(1)) Names(["inactive", "code", "denied"], Declared(request));
            Check(fixture.Effects.Contains("inactive") && fixture.Effects.Contains("code") && !fixture.Effects.Contains("denied"), "New scheduler skipped admission/policy.");
            Check(fixture.Policy.SawFinalCodeArguments, "New scheduler bypassed final argument transforms.");
            Names(["inactive", "code", "denied"], session.Snapshot.Agent.Tools.Select(tool => tool.Name));
            Check(ActivationDeltas(session, "inactive") == 1, "Activation checkpoint was omitted or committed twice.");
        }
        var reopened = await fixture.Open(fixture.PathFor("callback"), default);
        await using var reopenedOwner = fixture.Owner(reopened);
        Names(["inactive", "code", "denied"], reopened.GetActiveTools());
    }

    private static async Task CallbackCancelledBatch()
    {
        var provider = new ActivationProvider(); await using var fixture = await Fixture.Create(views: provider);
        var session = await fixture.New("cancelled-batch"); await using var owner = fixture.Owner(session);
        var entered = Gate(); var cleanup = Gate(); var release = Gate(); using var cancellation = new CancellationTokenSource();
        fixture.Source.FirstTool = "outer";
        fixture.Code = async (_, token) =>
        { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); }
            finally { cleanup.TrySetResult(); await release.Task; } return Result("joined"); };
        fixture.Outer = (context, token) =>
        {
            fixture.Retained = context;
            ((IExtensionToolActivationContext)context).SetActiveTools(["inactive"]);
            _ = context.ExecuteToolAsync("code", JsonData.EmptyObject).AsTask();
            return ValueTask.FromResult(Result("ignored child"));
        };
        var original = session.PromptAsync(Input(), cancellation.Token);
        try
        {
            await Reach(entered.Task, original); cancellation.Cancel(); await Reach(cleanup.Task, original);
            Check(!original.IsCompleted, "Ignored child cleanup was detached.");
            Names(["inactive"], session.GetActiveTools());
            Names(["direct", "outer"], session.Snapshot.Agent.Tools.Select(tool => tool.Name));
            Check(ActivationDeltas(session, "inactive") == 0, "Cancelled batch published before its boundary.");
            await Throws<InvalidOperationException>(() => session.SetActiveToolsAsync(["code"]));
        }
        finally { release.TrySetResult(); await original; }
        fixture.Source.FirstTool = "inactive"; fixture.Source.Requests.Clear();
        await session.PromptAsync(Input());
        Names(["inactive"], Declared(fixture.Source.Requests[0]));
        Check(fixture.Effects.Count(name => name == "inactive") == 1 && ActivationDeltas(session, "inactive") == 1,
            "Retained selection did not publish exactly once on next prompt.");
        var history = session.Snapshot.Context.LlmMessages;
        var lastUser = Enumerable.Range(0, history.Length).Last(index => history[index].Role == "user");
        var deltaIndex = Enumerable.Range(0, history.Length).Last(index => IsActivationDelta(history[index], "inactive"));
        Check(deltaIndex < lastUser, "Retained activation was declared after the newly admitted user.");
    }

    private static async Task CallbackAfterAcknowledgment()
    {
        var provider = new ActivationProvider(); await using var fixture = await Fixture.Create(views: provider);
        var session = await fixture.New("after-ack"); await using var owner = fixture.Owner(session);
        using var cancellation = new CancellationTokenSource(); var cancelledAfterAck = false;
        fixture.Source.FirstTool = "outer";
        fixture.Outer = (context, _) =>
        {
            ((IExtensionToolActivationContext)context).SetActiveTools(["inactive"]);
            fixture.Clock = () =>
            {
                if (session.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(["inactive"]) && ActivationDeltas(session, "inactive") == 1)
                { cancelledAfterAck = true; cancellation.Cancel(); }
                return 1;
            };
            return ValueTask.FromResult(Result("activate"));
        };
        await session.PromptAsync(Input(), cancellation.Token);
        Check(cancelledAfterAck && ActivationDeltas(session, "inactive") == 1 && session.Snapshot.Fault is null,
            "Post-ack cancellation lost its checkpoint or poisoned the owned session.");
        Names(["inactive"], session.GetActiveTools()); Names(["inactive"], session.Snapshot.Agent.Tools.Select(tool => tool.Name));
        Check(!fixture.Effects.Contains("inactive"), "Cancelled next request caused effects.");
    }

    private static async Task HostSupersedesProposal()
    {
        await using var fixture = await Fixture.Create(); var session = await fixture.New("host"); await using var owner = fixture.Owner(session);
        session.ScheduleToolActivation(["code"]);
        await session.SetActiveToolsAsync(["inactive"]); Names(["inactive"], session.GetActiveTools());
        session.ScheduleToolActivation(["code"]); var checkpoint = session.Snapshot.Log.Entries.Length;
        await session.SetActiveToolsAsync(["inactive"]);
        Check(checkpoint == session.Snapshot.Log.Entries.Length, "No-op host selection fabricated a checkpoint.");
        Names(["inactive"], session.GetActiveTools());
        fixture.Source.FirstTool = "code"; await session.PromptAsync(Input());
        Names(["inactive"], Declared(fixture.Source.Requests[0]));
        Check(!fixture.Effects.Contains("code"), "Old proposal undid the acknowledged host setter.");
    }

    private static async Task NavigationActivationEpoch()
    {
        await using var fixture = await Fixture.Create(); var session = await fixture.New("tree"); await using var owner = fixture.Owner(session);
        var old = owner.CaptureTree(owner.Current); session.ScheduleToolActivation(["inactive"]);
        await Throws<SessionTreeNavigationException>(() => owner.NavigateTreeAsync(owner.Current, new(null, old.Revision)));
        var current = owner.CaptureTree(owner.Current);
        var vetoed = await owner.NavigateTreeAsync(owner.Current, new(null, current.Revision), beforeTree: (_, _) => ValueTask.FromResult(false));
        Check(vetoed.Cancelled, "Navigation veto was lost."); Names(["inactive"], session.GetActiveTools());
        var noOp = await owner.NavigateTreeAsync(owner.Current, new(current.LeafId, current.Revision));
        Check(noOp.NoOp, "Current leaf did not short-circuit."); Names(["inactive"], session.GetActiveTools());
        var entered = Gate(); var release = Gate();
        var navigation = owner.NavigateTreeAsync(owner.Current, new(null, current.Revision), beforeTree: async (_, token) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); return true; });
        try { await Reach(entered.Task, navigation); Check(session.Abort(), "Owned navigation abort was not admitted."); }
        finally { release.TrySetResult(); }
        var aborted = await navigation; Check(aborted.Aborted, "Owned abort was not retained."); Names(["inactive"], session.GetActiveTools());
        var selected = await owner.NavigateTreeAsync(owner.Current, new(null, current.Revision));
        Names([], session.GetActiveTools()); Check(selected.Disposition == SessionTreeNavigationDisposition.Selected, "Root selection failed.");
        var reusable = await owner.NavigateTreeAsync(owner.Current, new(selected.LeafId, selected.View.Revision));
        Check(reusable.NoOp, "Selected receipt captured the pre-restoration epoch.");
    }

    private static async Task NavigationEpochOverflow()
    {
        await using var fixture = await Fixture.Create(); var session = await fixture.New("epoch-overflow"); await using var owner = fixture.Owner(session);
        typeof(PersistentAgentSession).GetField("_activationEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, long.MaxValue);
        var view = owner.CaptureTree(owner.Current); var context = session.Snapshot.Context;
        await Throws<OverflowException>(() => owner.NavigateTreeAsync(owner.Current, new(null, view.Revision)));
        Check(ReferenceEquals(context, session.Snapshot.Context) && session.Snapshot.Context.LeafId == view.LeafId, "Restoration failed after publishing context.");
        Names(["direct", "outer"], session.Snapshot.Agent.Tools.Select(tool => tool.Name));
    }

    private static async Task ConcurrentActivationProposals()
    {
        var entered = Gate(); var release = Gate(); var hold = false;
        await using var fixture = await Fixture.Create((name, _) =>
        { if (hold && name == "code") { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); } return null; });
        var session = await fixture.New("proposals"); await using var owner = fixture.Owner(session); hold = true;
        var original = Task.Run(() => session.ScheduleToolActivation(["code"]));
        try { await Reach(entered.Task, original); session.ScheduleToolActivation(["inactive"]); }
        finally { release.TrySetResult(); }
        await Throws<InvalidOperationException>(async () => { await original; });
        Names(["inactive"], session.GetActiveTools());
        await session.PromptAsync(Input()); Names(["inactive"], Declared(fixture.Source.Requests[0]));
    }

    private static async Task ActivationPreparationGuards()
    {
        PersistentAgentSession? session = null; var reentrancyRejected = false; CancellationTokenSource? cancelDuring = null;
        await using var fixture = await Fixture.Create((name, _) =>
        {
            if (name == "code" && session is { } active)
            {
                try { active.ScheduleToolActivation(["direct"]); }
                catch (InvalidOperationException) { reentrancyRejected = true; }
                cancelDuring?.Cancel();
            }
            return null;
        });
        var activeSession = await fixture.New("guards"); session = activeSession; await using var owner = fixture.Owner(activeSession);
        using var before = new CancellationTokenSource(); before.Cancel();
        await Throws<OperationCanceledException>(() => Task.FromResult(activeSession.ScheduleToolActivation(["code"], before.Token)));
        using var during = new CancellationTokenSource(); cancelDuring = during;
        await Throws<OperationCanceledException>(() => Task.FromResult(activeSession.ScheduleToolActivation(["code"], during.Token)));
        Check(reentrancyRejected, "Preparation reentered logical publication."); Names(["direct", "outer"], activeSession.GetActiveTools());
        cancelDuring = null; activeSession.ScheduleToolActivation(["code"]); Names(["code"], activeSession.GetActiveTools());
    }

    private static async Task ClosedActivationContext()
    {
        var provider = new ActivationProvider(); await using var fixture = await Fixture.Create(views: provider);
        var session = await fixture.New("closed"); await using var owner = fixture.Owner(session);
        fixture.Source.FirstTool = "outer";
        fixture.Outer = (context, _) => { fixture.Retained = context; return ValueTask.FromResult(Result("retain")); };
        await session.PromptAsync(Input()); var retained = (IExtensionToolActivationContext)fixture.Retained!;
        await Refuses(() => retained.SetActiveTools(["inactive"])); await Refuses(() => retained.GetActiveTools());
        var old = owner.Current;
        var other = await fixture.New("other"); await other.DisposeAsync();
        await owner.SwitchAsync(old, new(fixture.PathFor("other")));
        await Refuses(() => retained.SetActiveTools(["code"]));
        Check(old.LifetimeToken.IsCancellationRequested && !fixture.Effects.Contains("inactive") && !fixture.Effects.Contains("code"), "Retired capability caused effects.");
    }

    private static async Task ActivationLeaseCancellation()
    {
        var entered = Gate(); var release = Gate(); var hold = false;
        await using var fixture = await Fixture.Create((name, _) =>
        { if (hold && name == "code") { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); } return null; });
        var session = await fixture.New("lease"); await using var owner = fixture.Owner(session);
        using var cancellation = new CancellationTokenSource(); hold = true;
        var original = Task.Run(() => session.ScheduleToolActivation(["code"], cancellation.Token)); Task? closing = null;
        try
        {
            await Reach(entered.Task, original); cancellation.Cancel(); closing = fixture.Extensions.DisposeAsync().AsTask();
            Check(!closing.IsCompleted && !original.IsCompleted, "Registry lease or original preparation was detached.");
        }
        finally { release.TrySetResult(); try { await Throws<OperationCanceledException>(async () => { await original; }); }
            finally { if (closing is not null) await closing; } }
        Names(["direct", "outer"], session.GetActiveTools()); Check(ActivationDeltas(session, "code") == 0, "Cancelled lease published metadata.");
    }

    private static async Task ActivationBoundaryRevisionRetry()
    {
        await using var fixture = await Fixture.Create(); var session = await fixture.New("boundary-retry"); await using var owner = fixture.Owner(session);
        session.ScheduleToolActivation(["inactive"]); var change = true; var samples = 0;
        fixture.Clock = () =>
        { if (++samples == 2 && change) { change = false; session.ScheduleToolActivation(["code"]); } return 1; };
        fixture.Source.FirstTool = "code";
        await session.PromptAsync(Input());
        Names(["code"], Declared(fixture.Source.Requests[0]));
        Check(!change && ActivationDeltas(session, "inactive") == 0 && ActivationDeltas(session, "code") == 1,
            "Stale staged revision reached a durable checkpoint.");
        Check(!fixture.Effects.Contains("inactive") && fixture.Effects.Count(name => name == "code") == 1 && fixture.Policy.SawFinalCodeArguments,
            "Retried request used the abandoned scheduler or bypassed policy.");
    }

    private static async Task CallbackQueuedNextRequest()
    {
        var provider = new ActivationProvider(); await using var fixture = await Fixture.Create(views: provider);
        var session = await fixture.New("queued-boundary"); await using var owner = fixture.Owner(session);
        var entered = Gate(); var release = Gate();
        fixture.Source.SelectTool = index => index switch { 0 => "outer", 1 => "inactive", _ => null };
        fixture.Code = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result("settled"); };
        fixture.Outer = (context, token) =>
        { ((IExtensionToolActivationContext)context).SetActiveTools(["inactive"]);
            _ = context.ExecuteToolAsync("code", JsonData.EmptyObject).AsTask(); return ValueTask.FromResult(Result("queued activation")); };
        var original = session.PromptAsync(Input());
        try
        {
            await Reach(entered.Task, original);
            session.Steer(new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"queued next\",\"timestamp\":1}")));
            Check(session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1 && fixture.Source.Requests.Count == 1 && ActivationDeltas(session, "inactive") == 0,
                "Queue or loadout changed before original child settlement.");
        }
        finally { release.TrySetResult(); await original; }
        Names(["inactive"], Declared(fixture.Source.Requests[1]));
        Check(fixture.Effects.Count(name => name == "inactive") == 1, "Queued turn used the old scheduler.");
        var history = session.Snapshot.Context.LlmMessages;
        var queued = Enumerable.Range(0, history.Length).Single(index => history[index].Role == "user" &&
            history[index].WireBody.Value.GetProperty("content").GetString() == "queued next");
        var delta = Enumerable.Range(0, history.Length).Single(index => IsActivationDelta(history[index], "inactive"));
        var remaining = session.GetPendingInputQueueSnapshot();
        Check(delta < queued && remaining.SteeringMessages.IsEmpty && remaining.FollowUpMessages.IsEmpty, "Activation checkpoint placement or queue drain differs.");
    }

    private static async Task PendingSystemToolIntent()
    {
        await using var fixture = await Fixture.Create(); var session = await fixture.New("pending-system");
        await using var owner = fixture.Owner(session);
        session.ScheduleToolActivation(["inactive"]);
        var intent = fixture.Binding.CreateDeclarationMessage("keep system content", 7);
        var user = new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"keep user content\",\"timestamp\":8}"));
        fixture.Source.FirstTool = "inactive";
        await session.PromptAsync([intent, user]);
        Names(["inactive"], Declared(fixture.Source.Requests[0]));
        var history = session.Snapshot.Context.LlmMessages;
        var systemIndex = Enumerable.Range(0, history.Length).Single(index => history[index].Role == "system" &&
            history[index].WireBody.Value.GetProperty("content").GetString() == "keep system content");
        var preserved = history[systemIndex].WireBody.Value;
        Check(preserved.GetProperty("timestamp").GetInt64() == 7 && !preserved.TryGetProperty("toolsAdded", out _) &&
            !preserved.TryGetProperty("toolsRemoved", out _), "Pending system content changed or stale tool intent survived.");
        var deltaIndex = Enumerable.Range(0, history.Length).Single(index => IsActivationDelta(history[index], "inactive"));
        Check(deltaIndex < systemIndex && history[systemIndex + 1].WireBody.Value.GetRawText() == user.WireBody.Value.GetRawText() &&
            fixture.Effects.Count(name => name == "inactive") == 1, "Boundary reordered input content or used a stale executor.");
        var requestSystem = fixture.Source.Requests[0].Messages.Single(message => message.Role == "system" &&
            message.WireBody.Value.GetProperty("content").GetString() == "keep system content");
        Check(!requestSystem.WireBody.Value.TryGetProperty("toolsAdded", out _), "Request projection revived pending tool intent.");
    }

    private static int ActivationDeltas(PersistentAgentSession session, string name) => session.Snapshot.Context.LlmMessages.Count(message => IsActivationDelta(message, name));
    private static bool IsActivationDelta(TranscriptEntry message, string name) => message.Role == "system" &&
        message.WireBody.Value.TryGetProperty("toolsAdded", out var tools) && tools.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == name);
    private static Task Refuses(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { return Task.CompletedTask; }
        throw new InvalidOperationException("Stale callback activation was admitted."); }

    // Native registry context forwarding plus actual session/attachment ownership, with no provider/transport effects.
    private sealed class ActivationProvider : IExtensionSessionToolActivationProvider
    {
        public ReplaceableAgentSession? Owner;
        public ExtensionSessionSnapshot? Capture(IExtensionContext context)
        {
            var owner = Owner; if (owner is null) return null; var attached = owner.Current; var state = attached.Session.Snapshot;
            return new(state.Log.Header.Id, attached.Generation, state.Context.LeafId, state.Context.Ancestry.Select(entry => entry.WireBody).ToImmutableArray());
        }
        public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot)
        {
            var owner = Owner ?? throw new InvalidOperationException("Missing owner."); var attached = owner.Current;
            Check(attached.Generation == snapshot.Generation && attached.Session.Snapshot.Log.Header.Id == snapshot.SessionId, "Stale scope capture.");
            return new ActivationScope(owner, attached, context, snapshot);
        }
    }
    private sealed class ActivationScope : IExtensionSessionToolActivationScope
    {
        private readonly ReplaceableAgentSession owner; private readonly AgentSessionAttachment attached; private readonly IExtensionContext context;
        private readonly CancellationTokenSource lifetime; private readonly object gate = new(); private bool closed;
        private int active; private TaskCompletionSource? idle;
        public ExtensionSessionSnapshot Snapshot { get; }
        public CancellationToken SessionCancellationToken => lifetime.Token;
        public ActivationScope(ReplaceableAgentSession owner, AgentSessionAttachment attached, IExtensionContext context, ExtensionSessionSnapshot snapshot)
        { this.owner = owner; this.attached = attached; this.context = context; Snapshot = snapshot;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(attached.LifetimeToken, context.SessionCancellationToken); }
        private T Invoke<T>(Func<T> operation)
        {
            lock (gate) { if (closed) throw new InvalidOperationException("Closed callback."); owner.ValidateAttachment(attached);
                context.OperationCancellationToken.ThrowIfCancellationRequested(); context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
                lifetime.Token.ThrowIfCancellationRequested(); active++; }
            try { return operation(); } finally { lock (gate) if (--active == 0) idle?.TrySetResult(); }
        }
        public ImmutableArray<string> GetActiveTools() => Invoke(() => attached.Session.GetActiveTools());
        public ExtensionToolActivationSelection SetActiveTools(ImmutableArray<string> names) => Invoke(() =>
        { using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.OperationCancellationToken, context.ExtensionLifetimeCancellationToken, lifetime.Token);
            var selected = attached.Session.ScheduleToolActivation(names, linked.Token); return new ExtensionToolActivationSelection(selected.Revision, selected.Names); });
        public ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string kind, int version, JsonData data, CancellationToken token) => throw new InvalidOperationException("Unavailable test action.");
        public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string path, bool latest, string? leaf, CancellationToken token) => throw new InvalidOperationException("Unavailable test action.");
        public async ValueTask DisposeAsync()
        {
            Task work; lock (gate) { if (closed) return; closed = true; work = active == 0 ? Task.CompletedTask : (idle = Gate()).Task; }
            lifetime.Cancel(); await work; lifetime.Dispose();
        }
    }
}
