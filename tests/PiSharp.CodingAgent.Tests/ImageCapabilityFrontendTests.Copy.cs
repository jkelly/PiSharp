using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static partial class ImageCapabilityFrontendTests
{
    private static readonly TimeSpan ImageStageBound = TimeSpan.FromSeconds(10);
    private static (string Name, Func<Task> Run)[] CopyCases() =>
    [
        ("image-capability frontend RPC copied native image clone and selected fork resume opposite capability without replay", NativeImageCopy),
        ("image-capability frontend RPC staged native image rejection joins held cleanup and retains usable source", NativeImageStaging)
    ];

    // Authored native integration controls. Expected request bodies reuse the existing independent
    // literals; this conjunction is not an execution of genuine Source frontend constructors.
    private static async Task NativeImageCopy()
    {
        foreach (var api in new[] { Api, "anthropic-messages" })
        foreach (var vision in new[] { false, true })
        foreach (var mode in new[] { "plain", "patch" })
        foreach (var kind in new[] { "clone", "fork" })
        {
            using var files = new Files(); var package = await ImagePackage(files);
            var settled = await ExecuteImageTool(files, package, api, vision, mode);
            var leaf = settled.ValidatedPrefix[^1].Entry.Id!;
            var userId = settled.ValidatedPrefix.Single(row => row.Entry.Type == "message" &&
                row.Entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user").Entry.Id;
            var sibling = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            {
                type = "message", id = "image-copy-excluded-sibling", parentId = userId,
                timestamp = "2026-10-03T00:00:00.000Z",
                message = new { role = "user", content = "excluded physical image-copy sibling", timestamp = 123 }
            }));
            await using (var writer = await SessionLogStore.OpenAsync(files.Session)) await writer.AppendAsync([sibling]);
            var source = await Complete(files.Session); Prefix(settled, source);
            Equal(sibling.Id, source.ValidatedPrefix[^1].Entry.Id);
            var selected = new SessionContextProjector().Project(ImageEntries(source), leaf);
            ToolCanonical(JsonSerializer.SerializeToElement(selected.LlmMessages.Select(message => message.WireBody.Value)), mode);
            Check(!selected.Ancestry.Any(entry => entry.Id == sibling.Id), "Authored selection retained the physical sibling.");

            await files.Script(api == Api ? SessionCommandTests.CompletionsText("must not send while copying") :
                SessionCommandTests.AnthropicText("must not send while copying")); string branch;
            await using (var rpc = new Child(files, vision, [.. package.Arguments, "--leaf", leaf], api, package.Marker))
            {
                await rpc.Send(kind == "clone" ? new { id = "copy", type = "clone" } :
                    (object)new { id = "copy", type = "fork", entryId = leaf, position = "at" });
                var response = Good(await rpc.Response("copy"), kind).GetProperty("data");
                Check(!response.GetProperty("cancelled").GetBoolean(), "Actual native image branch creation was canceled.");
                Equal(2L, response.GetProperty("generation").GetInt64());
                branch = response.GetProperty("sessionFile").GetString()!;
                Check(Path.GetDirectoryName(branch) == files.Root && branch != files.Session, "Image branch escaped its source directory.");
                var copied = await LiveComplete(branch);
                Equal(files.Session, copied.Header!.WireBody.Value.GetProperty("parentSession").GetString());
                Check(copied.Header.Id != source.Header!.Id, "Clone/fork reused the source session identity.");
                Equal(response.GetProperty("sessionId").GetString(), copied.Header.Id);
                var entries = ImageEntries(copied); Equal(selected.Ancestry.Length, entries.Length);
                for (var index = 0; index < entries.Length; index++)
                    Equal(selected.Ancestry[index].WireBody.ToString(), entries[index].WireBody.ToString());
                Check(!entries.Any(entry => entry.Id == sibling.Id), "Native clone/fork copied the unselected sibling.");
                await rpc.Send(new { id = "messages", type = "get_messages" });
                Same(JsonSerializer.SerializeToElement(selected.LlmMessages.Select(message => message.WireBody.Value)),
                    Good(await rpc.Response("messages"), "get_messages").GetProperty("data").GetProperty("messages"));
                await rpc.Send(new { id = "entries", type = "get_entries" });
                await Acknowledged(branch, Good(await rpc.Response("entries"), "get_entries").GetProperty("data").GetProperty("entries"));
                Check(!rpc.Records.Any(record => Type(record).StartsWith("tool_execution_", StringComparison.Ordinal)),
                    "Creating a native image branch replayed its finalized tool.");
                await rpc.Finish();
            }
            var branchBefore = await Complete(branch); ToolCanonical(Project(branchBefore), mode);
            var imported = files.In("native-image-import.jsonl");
            var copy = await new SessionCopyService().CopyAsync(new(branch, imported));
            Equal(SessionCopyStatus.Published, copy.Status); Equal(SessionCopyFormat.NativeExact, copy.Format);
            Equal(0, copy.OmittedRecords); Equal(0, copy.OmittedFields);
            var importedBefore = await Complete(imported);
            ImageBytesEqual(branchBefore, importedBefore, "Native-exact image export/import changed committed bytes.");
            var next = "after copied native image history";
            var expected = ToolContinuation(api, !vision, mode, next);
            await files.Script(api == Api ? SessionCommandTests.CompletionsText("copied-image-final", expectedRequest: expected) :
                SessionCommandTests.AnthropicText("copied-image-final", expectedRequest: expected));
            await using (var rpc = new Child(files, !vision, package.Arguments, api, package.Marker))
            {
                await rpc.Send(new { id = "switch", type = "switch_session", sessionPath = imported });
                Equal(2L, Good(await rpc.Response("switch"), "switch_session").GetProperty("data").GetProperty("generation").GetInt64());
                await rpc.Send(new { id = "state", type = "get_state" });
                var model = Good(await rpc.Response("state"), "get_state").GetProperty("data").GetProperty("model");
                ImageBinding(model, api, !vision);
                await rpc.Send(new { id = "p", type = "prompt", message = next });
                Good(await rpc.Response("p"), "prompt"); await rpc.Settled(); rpc.Final("copied-image-final");
                Check(!rpc.Records.Any(record => Type(record).StartsWith("tool_execution_", StringComparison.Ordinal)),
                    "Copied image history replayed a finalized native tool on its new binding.");
                await rpc.Send(new { id = "messages", type = "get_messages" });
                ToolCanonical(Good(await rpc.Response("messages"), "get_messages").GetProperty("data").GetProperty("messages"), mode);
                await rpc.Send(new { id = "entries", type = "get_entries" });
                await Acknowledged(imported, Good(await rpc.Response("entries"), "get_entries").GetProperty("data").GetProperty("entries"));
                await rpc.Finish();
            }
            var after = await Complete(imported); Prefix(importedBefore, after); ToolCanonical(Project(after), mode);
            ImageBytesEqual(source, await Complete(files.Session), "Image branch/continuation changed the source byte prefix.");
            ImageBytesEqual(branchBefore, await Complete(branch), "Native-exact export/resume changed its fork/clone source.");
            foreach (var path in new[] { files.Session, branch, imported })
            { await using var writer = await SessionLogStore.OpenAsync(path); Equal((long)(await LiveComplete(path)).OriginalBytes.Length, writer.Snapshot.CommittedByteLength); }
            ImageNoTemps(files); Markers(package, mode, runs: 3);
            Console.WriteLine("IMAGE_NATIVE_COPY_PASS " + JsonSerializer.Serialize(new
            { api, vision, mode, kind, resumedVision = !vision, fullExpectedHttpRequests = 3,
                selectedBranchOnly = true, parentSessionPreserved = true, nativeExactCopy = true,
                sourceBytesUnchanged = true, completedToolReexecuted = false, pluginDisposalJoined = true, writersReacquired = true }));
        }
    }

    private static async Task NativeImageStaging()
    {
        foreach (var api in new[] { Api, "anthropic-messages" })
        foreach (var vision in new[] { false, true })
        foreach (var creation in new[] { false, true })
        {
            const string mode = "patch";
            using var files = new Files(); var package = await ImagePackage(files);
            var source = await ExecuteImageTool(files, package, api, vision, mode);
            var candidate = creation ? files.In("1970-01-01T00-00-00-123Z_image-stage-candidate.jsonl") : files.In("image-stage-candidate.jsonl");
            if (!creation) Equal(SessionCopyStatus.Published, (await new SessionCopyService().CopyAsync(new(files.Session, candidate))).Status);
            var candidateBefore = creation ? null : await Complete(candidate);
            var markerBefore = Environment.GetEnvironmentVariable("PISHARP_IMAGE_FIXTURE_MARKER");
            Environment.SetEnvironmentVariable("PISHARP_IMAGE_FIXTURE_MARKER", package.Marker);
            try
            {
                using var deadline = new CancellationTokenSource(ImageStageBound);
                var next = "source after rejected native image stage";
                var turn = api == Api ? SessionCommandTests.CompletionsText("source-image-usable", expectedRequest: ToolContinuation(api, vision, mode, next)) :
                    SessionCommandTests.AnthropicText("source-image-usable", expectedRequest: ToolContinuation(api, vision, mode, next));
                await using var currentProfile = await ImageStageProfile(files, package, files.Session, api, vision, turn, deadline.Token);
                await using var candidateProfile = await ImageStageProfile(files, package, candidate, api, !vision,
                    api == Api ? SessionCommandTests.CompletionsText("must not send") : SessionCommandTests.AnthropicText("must not send"), deadline.Token);
                Equal(vision, currentProfile.SelectedModelDefinition.DeclaresImageInput);
                Equal(!vision, candidateProfile.SelectedModelDefinition.DeclaresImageInput);
                var currentRelease = new Release(); var stagedRelease = new HeldImageRelease(candidateProfile);
                var ids = 0; var acquisitions = 0;
                ValueTask<SessionRuntimeLease> Stage(string cwd, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested(); Equal(files.Root, cwd); acquisitions++;
                    return ValueTask.FromResult(new SessionRuntimeLease(candidateProfile.Registry, stagedRelease));
                }
                var lifecycle = new PersistentSessionLifecycle(currentProfile.Registry, () => 123, () => "image-stage-entry-" + ++ids,
                    nextSessionId: () => "image-stage-candidate", runtimeForWorkingDirectory: Stage);
                await using var initial = await PersistentAgentSession.OpenWithRuntimeFactoryAsync(files.Session,
                    (_, _) => ValueTask.FromResult(new SessionRuntimeLease(currentProfile.Registry, currentRelease)), () => 123,
                    () => "image-stage-entry-" + ++ids, new(UseLatestLeaf: true), currentProfile.SelectedModel, deadline.Token);
                currentProfile.AttachOwner(initial, lifecycle: lifecycle); await using var owner = currentProfile.Sessions!;
                var old = owner.Current; var oldContext = initial.Snapshot.Context; var oldMessages = initial.Snapshot.Agent.Messages;
                PersistentAgentSession? staged = null; Task<AgentSessionReplacement?>? transition = null;
                async ValueTask Reject(PersistentAgentSession target, CancellationToken token)
                {
                    staged = target; Equal(candidate, target.Path); ToolCanonical(JsonSerializer.SerializeToElement(target.Snapshot.Agent.Messages.Select(message => message.WireBody.Value)), mode);
                    ImageBytesEqual(source, await LiveComplete(files.Session), "Staging changed old durable image bytes before rejection.");
                    var before = await LiveComplete(target.Path);
                    await Throws<InvalidOperationException>(() => target.PromptAsync(new TranscriptEntry("user", JsonData.Parse(JsonSerializer.Serialize(new
                    { role = "user", content = new[] { new { type = "text", text = "reserved candidate must not execute" } }, timestamp = 123 })))));
                    await Throws<InvalidOperationException>(() => target.AppendExtensionEntryAsync(target.Snapshot.Log.Header.Id, Draft("reserved image candidate write"), token));
                    await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(old, Draft("reserved old image write"), token));
                    ImageBytesEqual(before, await LiveComplete(target.Path), "Reserved candidate acquired a durable effect.");
                    Equal(0, currentProfile.UsedTurns); Equal(0, candidateProfile.UsedTurns);
                    Equal(0, currentProfile.Actions.Length); Equal(0, candidateProfile.Actions.Length);
                    throw new IOException("authored image staging preflight rejection");
                }
                try
                {
                    transition = creation ? owner.CreateAsync(old, new(AgentSessionCreationKind.Clone),
                        (target, text, token) => { Check(text is null, "Clone unexpectedly selected user text."); return Reject(target, token); }, deadline.Token) :
                        owner.SwitchAsync(old, new(candidate), async (_, target, token) => { await Reject(target, token); return true; }, cancellationToken: deadline.Token);
                    await stagedRelease.Entered.Task.WaitAsync(deadline.Token);
                    Check(!transition.IsCompleted && !stagedRelease.Completed, "Failed image staging reported settled while its owned cleanup was held.");
                    Check(ReferenceEquals(owner.Current, old) && old.Generation == 1 && !old.LifetimeToken.IsCancellationRequested && !initial.Snapshot.IsRetired,
                        "Rejected staging changed the old attachment or capability lifetime.");
                    Check(ReferenceEquals(oldContext, initial.Snapshot.Context) && oldMessages == initial.Snapshot.Agent.Messages,
                        "Rejected image staging changed the old context or canonical agent history.");
                    ImageBytesEqual(source, await LiveComplete(files.Session), "Held staging cleanup changed source bytes.");
                    Equal(1, acquisitions); Equal(1, stagedRelease.Calls);
                    await using (var writer = await SessionLogStore.OpenAsync(candidate))
                        Equal((long)(await LiveComplete(candidate)).OriginalBytes.Length, writer.Snapshot.CommittedByteLength);
                    Check(File.Exists(candidate), "Creation rollback deleted the image branch before held resource cleanup joined.");
                    stagedRelease.Continue.TrySetResult();
                    await Throws<IOException>(() => transition);
                    Check(stagedRelease.Completed && staged!.Snapshot.IsDisposed, "Failed image staging did not join its actual candidate coordinator and runtime.");
                    Check(ReferenceEquals(owner.Current, old) && !initial.Snapshot.IsRetired, "Joined rejection retired the old image writer.");
                    if (creation) Check(!File.Exists(candidate), "Rejected image clone retained its known unattached branch.");
                    else ImageBytesEqual(candidateBefore!, await Complete(candidate), "Rejected switch changed candidate image history.");
                    await initial.PromptAsync(new TranscriptEntry("user", JsonData.Parse(JsonSerializer.Serialize(new
                    { role = "user", content = new[] { new { type = "text", text = next } }, timestamp = 123 }))), deadline.Token);
                    var final = initial.Snapshot.Agent.Messages.Last(message => message.Role == "assistant").WireBody.Value;
                    Equal("stop", final.GetProperty("stopReason").GetString());
                    Equal("source-image-usable", final.GetProperty("content")[0].GetProperty("text").GetString());
                    Equal(1, currentProfile.UsedTurns); Equal(0, candidateProfile.UsedTurns);
                    Equal(0, currentProfile.Actions.Length); Equal(0, candidateProfile.Actions.Length);
                    await owner.AppendExtensionEntryAsync(old, Draft("source image usable after rejection"), deadline.Token);
                    await owner.DisposeAsync(); Equal(1, currentRelease.Calls);
                }
                finally
                {
                    stagedRelease.Continue.TrySetResult();
                    if (transition is not null) try { await transition; } catch (Exception) when (transition.IsCompleted) { }
                }
                await currentProfile.DisposeAsync(); await candidateProfile.DisposeAsync();
                var after = await Complete(files.Session); Prefix(source, after); ToolCanonical(Project(after), mode);
                await using (var writer = await SessionLogStore.OpenAsync(files.Session)) Equal((long)after.OriginalBytes.Length, writer.Snapshot.CommittedByteLength);
                if (!creation) { await using var writer = await SessionLogStore.OpenAsync(candidate); Equal((long)candidateBefore!.OriginalBytes.Length, writer.Snapshot.CommittedByteLength); }
                ImageNoTemps(files); Markers(package, mode, runs: 3);
                Console.WriteLine("IMAGE_NATIVE_STAGING_PASS " + JsonSerializer.Serialize(new
                { api, vision, candidateVision = !vision, kind = creation ? "clone-preflight" : "switch-preflight", mode,
                    fullExpectedHttpRequests = 3, candidateRequests = 0, candidateEffects = 0, cleanupHeldUntilRelease = true,
                    originalGenerationRetained = true, oldContextUnchangedBeforeResume = true, oldSourceUsable = true,
                    completedToolReexecuted = false, pluginDisposalJoined = true, writersReacquired = true }));
            }
            finally { Environment.SetEnvironmentVariable("PISHARP_IMAGE_FIXTURE_MARKER", markerBefore); }
        }
    }

    private static ImmutableArray<SessionEntry> ImageEntries(SessionLogReadResult read) => read.ValidatedPrefix.Skip(1).Select(row => row.Entry).ToImmutableArray();
    private static void ImageBytesEqual(SessionLogReadResult expected, SessionLogReadResult actual, string reason) =>
        Check(expected.OriginalBytes.AsSpan().SequenceEqual(actual.OriginalBytes.AsSpan()), reason);
    private static void ImageNoTemps(Files files) => Check(!Directory.EnumerateFiles(files.Root, ".pisharp-*.tmp").Any(), "Image copy/staging retained an owned publication temporary.");
    private static void ImageBinding(JsonElement model, string api, bool vision) => Same(JsonSerializer.SerializeToElement(new
    { id = api == Api ? ModelId : "pisharp-offline-session", api, provider = api == Api ? "openai" : "anthropic",
        name = "PiSharp authored offline session model", baseUrl = api == Api ? "https://offline-session.invalid/v1" : "https://offline-session.invalid",
        reasoning = false, input = vision ? new[] { "text", "image" } : ["text"], contextWindow = 131_072, maxTokens = 8192,
        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 }, provenance = "authored-offline-profile", liveModelCapabilityClaimed = false }), model);
    private static async Task<OfflineSessionProfile> ImageStageProfile(Files files, ImagePackageInfo package, string path,
        string api, bool vision, object turn, CancellationToken token)
    {
        var flags = package.Arguments.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        var approval = JsonNode.Parse(await File.ReadAllTextAsync(flags["--extension-approval"], token))!.AsObject();
        approval["sessionPath"] = path;
        var approvalPath = files.In(path == files.Session ? "image-current-approval.json" : "image-candidate-approval.json");
        await File.WriteAllTextAsync(approvalPath, approval.ToJsonString(), Utf8, token);
        var extension = NativeExtensionConfiguration.Optional(flags["--extension-package"], flags["--extension-manifest"], approvalPath,
            flags["--extension-snapshot-root"], [ImageTool]);
        return await OfflineSessionProfile.CreateAsync(files.Root, path, null, [JsonData.Parse(JsonSerializer.Serialize(turn))], [], [], token,
            offlineApi: api, extension: extension, modelSupportsImages: vision);
    }
    private sealed class HeldImageRelease(IAsyncDisposable resource) : IAsyncDisposable
    {
        internal TaskCompletionSource Entered { get; } = NewGate();
        internal TaskCompletionSource Continue { get; } = NewGate();
        internal int Calls; internal bool Completed;
        public async ValueTask DisposeAsync()
        {
            Calls++; await resource.DisposeAsync(); Entered.TrySetResult();
            await Continue.Task.WaitAsync(ImageStageBound); Completed = true;
        }
    }
}
