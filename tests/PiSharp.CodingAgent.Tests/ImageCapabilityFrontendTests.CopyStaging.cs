using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Storage;

internal static partial class ImageCapabilityFrontendTests
{
    // Authored native composition checks. Existing pinned Source fixtures remain unchanged.
    private static (string Name, Func<Task> Run)[] CopyStagingCases() =>
    [
        ("image-capability frontend RPC closure fork and clone retain opaque tool images through capability selection and reopen", ImageCopies),
        ("image-capability frontend RPC closure rejected image staging joins cleanup and preserves the usable source binding", ImageStagingRollback)
    ];

    private static async Task ImageCopies()
    {
        foreach (var api in new[] { Api, "anthropic-messages" })
        foreach (var initialVision in new[] { false, true })
        foreach (var kind in new[] { AgentSessionCreationKind.ForkAt, AgentSessionCreationKind.Clone })
        {
            using var files = new Files(); var package = await ImagePackage(files);
            var seed = await ExecuteImageTool(files, package, api, initialVision, "patch");
            using var marker = new ClosureMarker(package.Marker);
            var expected = ClosureRequest(api, !initialVision, kind == AgentSessionCreationKind.Clone,
                ("user", "continue copied native image history"));
            var leases = new List<ClosureLease>(); var entry = 0; var identity = 0;
            var source = await ClosureProfile(files, package, api, initialVision);
            async ValueTask<SessionRuntimeLease> Bind(string cwd, CancellationToken token)
            {
                Equal(files.Root, cwd); token.ThrowIfCancellationRequested();
                var profile = leases.Count == 0 ? source : await ClosureProfile(files, package, api, !initialVision,
                    ClosureTurn(api, "copied-image-final", expected));
                var lease = new ClosureLease(profile); leases.Add(lease);
                return new(profile.Registry, lease);
            }
            var lifecycle = new PersistentSessionLifecycle(source.Registry, () => 123, () => "copy-entry-" + ++entry,
                nextSessionId: () => "copy-session-" + ++identity, runtimeForWorkingDirectory: Bind);
            var initial = await lifecycle.OpenAsync(new(files.Session), source.SelectedModel);
            await using var owner = lifecycle.Attach(initial); var original = owner.Current;
            var toolEntry = seed.ValidatedPrefix.Single(row => row.Entry.WireBody.Value.TryGetProperty("message", out var message) &&
                message.GetProperty("role").GetString() == "toolResult").Entry.Id;
            var selected = kind == AgentSessionCreationKind.ForkAt ? toolEntry : null;
            var created = (await owner.CreateAsync(original, new(kind, selected)))!.Current;
            Check(original.LifetimeToken.IsCancellationRequested && initial.Snapshot.IsRetired, "Copy retained the old attachment authority.");
            await leases[0].Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(initialVision, source.SelectedModelDefinition.DeclaresImageInput);
            Equal(!initialVision, leases[1].Profile.SelectedModelDefinition.DeclaresImageInput);
            var copied = await LiveComplete(created.Session.Path);
            Check(copied.ValidatedPrefix[0].Entry.Id != seed.ValidatedPrefix[0].Entry.Id, "Copy reused the source identity.");
            Equal(files.Session, copied.ValidatedPrefix[0].Entry.WireBody.Value.GetProperty("parentSession").GetString());
            var selectedMessages = Project(seed).EnumerateArray().ToArray();
            if (kind == AgentSessionCreationKind.ForkAt) selectedMessages = selectedMessages.TakeWhile(message =>
                message.GetProperty("role").GetString() != "toolResult").Append(selectedMessages.Single(message =>
                    message.GetProperty("role").GetString() == "toolResult")).ToArray();
            Same(JsonSerializer.SerializeToElement(selectedMessages), Project(copied));
            ClosureCanonical(seed, copied); await ClosureUnchanged(files.Session, seed);
            await ClosurePrompt(created.Session, "continue copied native image history", "copied-image-final");
            Equal(0, source.UsedTurns); Equal(1, leases[1].Profile.UsedTurns);
            Equal(0, leases[1].Profile.Actions.Length);
            var continued = await LiveComplete(created.Session.Path); Prefix(copied, continued); ClosureCanonical(seed, continued);
            var copiedPath = created.Session.Path;
            await owner.DisposeAsync();
            Check(leases.Count == 2 && leases.All(lease => lease.Calls == 1 && lease.Closed.Task.IsCompletedSuccessfully),
                "Copy retirement/disposal failed to join a fresh runtime lease exactly once.");

            // Reopen the durable copy with another immutable capability binding, rather than reusing its runtime.
            var reopenedExpected = ClosureRequest(api, initialVision, kind == AgentSessionCreationKind.Clone,
                ("user", "continue copied native image history"), ("assistant", "copied-image-final"), ("user", "after copied image reopen"));
            var reopenedProfile = await ClosureProfile(files, package, api, initialVision,
                ClosureTurn(api, "copied-reopened-final", reopenedExpected));
            var reopenedLease = new ClosureLease(reopenedProfile);
            var reopenLifecycle = new PersistentSessionLifecycle(reopenedProfile.Registry, () => 124, () => "reopened-copy-entry-" + ++entry,
                runtimeForWorkingDirectory: (_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new SessionRuntimeLease(reopenedProfile.Registry, reopenedLease)); });
            await using (var reopened = await reopenLifecycle.OpenAsync(new(copiedPath), reopenedProfile.SelectedModel))
                await ClosurePrompt(reopened, "after copied image reopen", "copied-reopened-final");
            Equal(1, reopenedProfile.UsedTurns); Equal(0, reopenedProfile.Actions.Length); Equal(1, reopenedLease.Calls);
            Check(reopenedLease.Closed.Task.IsCompletedSuccessfully, "Reopened copy runtime cleanup detached.");
            var final = await Complete(copiedPath); Prefix(continued, final); ClosureCanonical(seed, final);
            await ClosureUnchanged(files.Session, seed); Markers(package, "patch", runs: 4);
            await using var writer = await SessionLogStore.OpenAsync(copiedPath);
            Equal((long)final.OriginalBytes.Length, writer.Snapshot.CommittedByteLength);
            Console.WriteLine("IMAGE_COPY_STAGING_COPY_PASS " + JsonSerializer.Serialize(new
            {
                api, initialVision, copiedVision = !initialVision, reopenedVision = initialVision, kind = kind.ToString(), selectedEntryId = selected,
                sourceCanonicalSha256 = ClosureHash(seed), copiedCanonicalSha256 = ClosureHash(final),
                fullExpectedHttpRequests = new[] { expected, reopenedExpected }, requestReceipts = new[] { leases[1].Profile.Requests, reopenedProfile.Requests },
                sourceBytesUnchanged = true, opaqueImageBlocksAndDetailsUnchanged = true, historicalToolExecutions = 1,
                freshRuntimeLeasesJoinedExactlyOnce = 3, authoredNativeComposition = true, newUpstreamCaptureClaimed = false
            }));
        }
    }

    private static async Task ImageStagingRollback()
    {
        foreach (var api in new[] { Api, "anthropic-messages" })
        foreach (var initialVision in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        {
            using var files = new Files(); var package = await ImagePackage(files);
            var seed = await ExecuteImageTool(files, package, api, initialVision, "patch");
            using var marker = new ClosureMarker(package.Marker);
            var expected = ClosureRequest(api, initialVision, true, ("user", "continue source after rejected image staging"));
            var source = await ClosureProfile(files, package, api, initialVision, ClosureTurn(api, "rollback-source-final", expected));
            var leases = new List<ClosureLease>(); var entry = 0; var identity = 0; var targetReady = NewGate();
            async ValueTask<SessionRuntimeLease> Bind(string cwd, CancellationToken token)
            {
                Equal(files.Root, cwd); token.ThrowIfCancellationRequested();
                var profile = leases.Count == 0 ? source : await ClosureProfile(files, package, api, !initialVision);
                var lease = new ClosureLease(profile, hold: leases.Count != 0); leases.Add(lease);
                if (leases.Count == 2) targetReady.TrySetResult();
                return new(profile.Registry, lease);
            }
            var lifecycle = new PersistentSessionLifecycle(source.Registry, () => 123, () => "rollback-entry-" + ++entry,
                nextSessionId: () => "rollback-session-" + ++identity, runtimeForWorkingDirectory: Bind);
            var initial = await lifecycle.OpenAsync(new(files.Session), source.SelectedModel);
            await using var owner = lifecycle.Attach(initial); var original = owner.Current;
            using var cancellation = new CancellationTokenSource(); PersistentAgentSession? staged = null;
            ValueTask Reject(PersistentAgentSession target, string? selectedText, CancellationToken token)
            {
                staged = target;
                if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                throw new ClosurePreflightFailure();
            }
            var operation = owner.CreateAsync(original, new(AgentSessionCreationKind.Clone), Reject, cancellation.Token);
            try
            {
                // Wait for the actual staged runtime's disposal, not a timing-based guess that rollback began.
                await Task.WhenAny(operation, targetReady.Task).WaitAsync(TimeSpan.FromSeconds(5));
                Equal(2, leases.Count); await leases[1].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!operation.IsCompleted, "Rejected staging returned before its runtime cleanup joined.");
                Check(ReferenceEquals(original, owner.Current) && !original.LifetimeToken.IsCancellationRequested && !initial.Snapshot.IsRetired,
                    "Rejected staging retired or replaced the source attachment.");
                Check(staged is not null && staged.Snapshot.IsDisposed, "Staged agent/writer did not close before runtime disposal.");
                var stage = await Complete(staged!.Path); ClosureCanonical(seed, stage);
                await ClosureUnchanged(files.Session, seed);
                Equal(initialVision, source.SelectedModelDefinition.DeclaresImageInput);
                Equal(!initialVision, leases[1].Profile.SelectedModelDefinition.DeclaresImageInput);
                Equal(0, source.UsedTurns); Equal(0, leases[1].Profile.UsedTurns);
                Equal(0, source.Actions.Length); Equal(0, leases[1].Profile.Actions.Length);
                await using var writer = await SessionLogStore.OpenAsync(staged.Path);
                Equal((long)stage.OriginalBytes.Length, writer.Snapshot.CommittedByteLength);
            }
            finally
            {
                if (leases.Count == 2) leases[1].Release.TrySetResult();
                try { await operation; }
                catch (ClosurePreflightFailure) when (!cancel) { }
                catch (OperationCanceledException) when (cancel) { }
            }
            if (cancel) await Throws<OperationCanceledException>(() => operation);
            else await Throws<ClosurePreflightFailure>(() => operation);
            Check(staged is not null && !File.Exists(staged.Path), "Known unattached image branch remained after rollback.");
            Equal(1, leases[1].Calls); Check(leases[1].Closed.Task.IsCompletedSuccessfully, "Rejected runtime cleanup detached.");
            Check(ReferenceEquals(original, owner.Current), "Rollback changed the current attachment.");
            await ClosureUnchanged(files.Session, seed);
            await ClosurePrompt(initial, "continue source after rejected image staging", "rollback-source-final");
            Equal(1, source.UsedTurns); Equal(0, leases[1].Profile.UsedTurns); Equal(0, source.Actions.Length);
            var continued = await LiveComplete(files.Session); Prefix(seed, continued); ClosureCanonical(seed, continued);
            await owner.DisposeAsync();
            Check(leases.All(lease => lease.Calls == 1 && lease.Closed.Task.IsCompletedSuccessfully), "Rollback/source runtime release leaked or repeated.");
            Markers(package, "patch", runs: 3);
            await using var sourceWriter = await SessionLogStore.OpenAsync(files.Session);
            Equal((long)continued.OriginalBytes.Length, sourceWriter.Snapshot.CommittedByteLength);
            Console.WriteLine("IMAGE_COPY_STAGING_ROLLBACK_PASS " + JsonSerializer.Serialize(new
            {
                api, initialVision, stagedVision = !initialVision, failure = cancel ? "precommit-cancel" : "preflight-fault",
                sourceCanonicalSha256 = ClosureHash(seed), continuedCanonicalSha256 = ClosureHash(continued), fullExpectedHttpRequest = expected,
                sourceRequestReceipts = source.Requests, rejectedTargetRequests = leases[1].Profile.Requests,
                cleanupHeldAndJoined = true, closedStagedWriterReacquired = true, knownUnattachedBranchDeleted = true,
                sourceAttachmentAndBytesUnchangedBeforeContinuation = true, opaqueImagesUnchanged = true, historicalToolExecutions = 1,
                freshRuntimeLeasesJoinedExactlyOnce = 2, authoredNativeComposition = true, newUpstreamCaptureClaimed = false
            }));
        }
    }

    private static Task<OfflineSessionProfile> ClosureProfile(Files files, ImagePackageInfo package, string api, bool vision, params object[] turns)
    {
        string Argument(string flag) => package.Arguments[Array.IndexOf(package.Arguments, flag) + 1];
        var configuration = new NativeExtensionConfiguration(Argument("--extension-package"), Argument("--extension-manifest"),
            Argument("--extension-approval"), Argument("--extension-snapshot-root"), [ImageTool]);
        return OfflineSessionProfile.CreateAsync(files.Root, files.Session, null,
            turns.Select(turn => JsonData.Parse(JsonSerializer.Serialize(turn))).ToImmutableArray(), [], [], CancellationToken.None,
            offlineApi: api, extension: configuration, modelSupportsImages: vision);
    }
    private static object ClosureTurn(string api, string final, JsonObject expected) => api == Api ?
        SessionCommandTests.CompletionsText(final, expectedRequest: expected) : SessionCommandTests.AnthropicText(final, expectedRequest: expected);

    private static JsonObject ClosureRequest(string api, bool vision, bool includeSeedFinal, params (string Role, string Text)[] tail)
    {
        var request = ToolContinuation(api, vision, "patch"); var messages = request["messages"]!.AsArray();
        if (api != Api) messages[^1]!["content"]!.AsArray()[^1]!.AsObject().Remove("cache_control");
        void Add(string role, string text) => messages.Add(role == "user" ? TextUser(text) : new JsonObject
        {
            ["role"] = role, ["content"] = api == Api ? JsonValue.Create(text) :
                new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
        });
        if (includeSeedFinal) Add("assistant", "native-image-final");
        foreach (var item in tail) Add(item.Role, item.Text);
        if (api != Api) messages.Last(message => message!["role"]!.GetValue<string>() == "user")!["content"]!.AsArray()[^1]!["cache_control"] =
            new JsonObject { ["type"] = "ephemeral" };
        return request;
    }
    private static async Task ClosurePrompt(PersistentAgentSession session, string prompt, string final)
    {
        await session.PromptAsync(new TranscriptEntry("user", JsonData.Parse(JsonSerializer.Serialize(new
        { role = "user", content = new[] { new { type = "text", text = prompt } }, timestamp = 123 }))));
        var last = session.Snapshot.Agent.Messages.Last(entry => entry.Role == "assistant").WireBody.Value;
        Equal("stop", last.GetProperty("stopReason").GetString());
        Equal(final, last.GetProperty("content").EnumerateArray().Single(block => block.GetProperty("type").GetString() == "text").GetProperty("text").GetString());
    }
    private static void ClosureCanonical(SessionLogReadResult seed, SessionLogReadResult copied)
    {
        ToolCanonical(Project(copied), "patch");
        var original = Project(seed).EnumerateArray().Single(message => message.GetProperty("role").GetString() == "toolResult");
        var retained = Project(copied).EnumerateArray().Single(message => message.GetProperty("role").GetString() == "toolResult");
        Equal(original.GetRawText(), retained.GetRawText());
    }
    private static async Task ClosureUnchanged(string path, SessionLogReadResult seed)
    {
        var current = await LiveComplete(path);
        Check(seed.OriginalBytes.AsSpan().SequenceEqual(current.OriginalBytes.AsSpan()), "Creation/rollback rewrote source image history.");
        Same(Project(seed), Project(current));
    }
    private static string ClosureHash(SessionLogReadResult read) => Convert.ToHexStringLower(SHA256.HashData(read.OriginalBytes.AsSpan()));
    private sealed class ClosurePreflightFailure : Exception;
    private sealed class ClosureMarker(string path) : IDisposable
    {
        private readonly string? previous = Set(path);
        private static string? Set(string value) { var old = Environment.GetEnvironmentVariable("PISHARP_IMAGE_FIXTURE_MARKER"); Environment.SetEnvironmentVariable("PISHARP_IMAGE_FIXTURE_MARKER", value); return old; }
        public void Dispose() => Environment.SetEnvironmentVariable("PISHARP_IMAGE_FIXTURE_MARKER", previous);
    }
    private sealed class ClosureLease(OfflineSessionProfile profile, bool hold = false) : IAsyncDisposable
    {
        internal OfflineSessionProfile Profile { get; } = profile;
        internal int Calls;
        internal TaskCompletionSource Entered { get; } = NewGate();
        internal TaskCompletionSource Release { get; } = NewGate();
        internal TaskCompletionSource Closed { get; } = NewGate();
        public async ValueTask DisposeAsync()
        {
            Calls++; Entered.TrySetResult();
            if (hold) await Release.Task;
            await Profile.DisposeAsync(); Closed.TrySetResult();
        }
    }
}
