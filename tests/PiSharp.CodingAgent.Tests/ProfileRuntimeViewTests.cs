using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Resources;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Runtime.Reloading;
using PiSharp.Sessions.Serialization;

internal static class ProfileRuntimeViewTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("profile view navigation retains lifetime and last close joins users plus all original faults", Lifetime),
        ("profile view default non-MCP lifecycle binds A B A and preserves admitted prompt images through shutdown", DefaultNavigation),
        ("profile view actual navigation and reload publish exact template and native registry together", () => Reload(false)),
        ("profile view rejected publication joins candidate cleanup without reviving old metadata", () => Reload(true))
    ];
    private static async Task DefaultNavigation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-profile-navigation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "A.jsonl"); var template = Path.Combine(root, "retained.md");
        var profile = await OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], default);
        try
        {
            Check(profile.SelectOneShotInputAdmission("plain text") is null);
            Check(profile.SelectOneShotInputAdmission("/reload") is not null);
            // A rejected acquisition transfers then retires its empty startup view
            // before RPC asks for shutdown. No native observer exists to dispatch.
            var rejectedCloses = 0;
            var rejected = profile.CaptureInitialRuntimeViewOwnership(new Resource(() =>
            { rejectedCloses++; return Task.CompletedTask; }), 1);
            await rejected.Resources.DisposeAsync();
            Check(profile.CaptureShutdownSessionSnapshot() is null);
            Check(!await profile.DispatchSessionShutdownAsync(null));
            try { _ = profile.CommandCatalog; throw new IOException("Retired view remained readable."); }
            catch (ObjectDisposedException) { }
            try { await profile.DispatchSessionShutdownAsync(new("unadmitted", 1, null, [])); throw new IOException("Foreign shutdown snapshot admitted."); }
            catch (InvalidOperationException) { }
            // Selection above freezes an empty startup capture, so use a fresh profile for loader ordering.
            await profile.DisposeAsync();
            Check(rejectedCloses == 1);
            profile = await OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], default);
            var sequence = 0;
            var lifecycle = profile.CreateLifecycle(() => 1, () => "entry-" + ++sequence);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "navigation-A", timestamp = "2026-10-05T00:00:00Z", cwd = root }));
            await using var session = await lifecycle.CreateAsync(path, header, profile.SelectedModel);
            await File.WriteAllTextAsync(template, "retained template");
            await profile.LoadPromptTemplatesAsync(Configuration(template), TextWriter.Null, default);
            await profile.AttachOwnerAsync(session, lifecycle: lifecycle);
            var owner = profile.Sessions!; var original = owner.Current; var registry = profile.Registry;
            var next = await owner.CreateAsync(original, new(AgentSessionCreationKind.New));
            Check(next is not null && owner.Current.Generation == 2);
            Check(profile.CommandCatalog.ToString().Contains("retained", StringComparison.Ordinal));
            var back = await owner.SwitchAsync(owner.Current, new(path));
            Check(back is not null && owner.Current.Generation == 3 && owner.Current.Session.Path == path);
            Check(!ReferenceEquals(owner.Current, original) && ReferenceEquals(profile.Registry, registry));
            var admission = profile.SelectOneShotInputAdmission("ordinary text");
            Check(admission is not null);
            var input = new PromptInput("/retained", Images: PiSharp.Contracts.JsonData.Parse(
                "[{\"type\":\"image\",\"data\":\"AQ==\",\"mimeType\":\"image/png\"}]"));
            var decision = await admission!.ReduceAsync(input, default);
            Check(decision.Action == PromptInputAction.Transform && decision.Text == "retained template");
            var effective = PromptInputValue.Apply(input, decision);
            var content = PromptInputValue.Message(effective, 1).WireBody.Value.GetProperty("content");
            Check(content.GetArrayLength() == 2 && content[0].GetProperty("text").GetString() == "retained template" &&
                content[1].GetProperty("data").GetString() == "AQ==");
            var retained = profile.CaptureShutdownSessionSnapshot();
            await owner.StopAdmissionAndJoinAsync();
            Check(!await profile.DispatchSessionShutdownAsync(retained));
            var close = profile.DisposeAsync().AsTask();
            Check(ReferenceEquals(close, profile.DisposeAsync().AsTask()));
            await close;
            Check(owner.Current.Session.Snapshot.IsDisposed && original.Session.Snapshot.IsDisposed);
        }
        finally
        {
            await profile.DisposeAsync();
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(root).StartsWith("pisharp-profile-navigation-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
    private static async Task Lifetime()
    {
        var entered = Gate(); var original = Gate();
        var a = new IOException("view A"); var b = new OperationCanceledException("faulted view B");
        var count = 0;
        var lifetime = new ProfileViewLifetime(new Resource(() => { count++; entered.TrySetResult(); return original.Task; }));
        var old = lifetime.Acquire(); var next = lifetime.Acquire(); var user = lifetime.Enter();
        await old.DisposeAsync(); Check(count == 0);
        var close = next.DisposeAsync().AsTask(); Check(ReferenceEquals(close, next.DisposeAsync().AsTask()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            original.SetException([a, b]); Check(!close.IsCompleted);
            user.Dispose();
            var error = await Failure(close);
            Check(Contains(error, a) && Contains(error, b) && count == 1 && original.Task.IsFaulted);
        }
        finally { user.Dispose(); original.TrySetResult(); await Settle(close); }
    }
    private static async Task Reload(bool rejectPublication)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-profile-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.jsonl");
        var before = Path.Combine(root, "before.md"); var after = Path.Combine(root, "after.md");
        await File.WriteAllTextAsync(before, "before template"); await File.WriteAllTextAsync(after, "after template");
        var profile = await OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], default);
        var releases = 0; var ids = 0;
        try
        {
            await profile.LoadPromptTemplatesAsync(Configuration(before), TextWriter.Null, default);
            var lifecycle = new PersistentSessionLifecycle(profile.Registry, () => 1, () => "entry-" + ++ids,
                runtimeForAttachment: (_, generation, _) =>
                {
                    var registry = profile.Registry;
                    var ownership = profile.CaptureInitialRuntimeViewOwnership(new Resource(() => { releases++; return Task.CompletedTask; }), generation);
                    return ValueTask.FromResult(new SessionRuntimeLease(registry, ownership.Resources, ownership.BindOwner));
                });
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "profile-view", timestamp = "2026-10-05T00:00:00Z", cwd = root }));
            var session = await lifecycle.CreateAsync(path, header, profile.SelectedModel);
            await profile.AttachOwnerAsync(session, lifecycle: lifecycle);
            var owner = profile.Sessions!;
            Check(profile.CommandCatalog.ToString().Contains("before", StringComparison.Ordinal));
            var navigation = await owner.CreateAsync(owner.Current, new(AgentSessionCreationKind.New));
            Check(navigation is not null && ReferenceEquals(navigation.Previous.Session, session) &&
                owner.Current.Generation == 2 && !ReferenceEquals(owner.Current.Session, session));
            // Navigation commits before its retained old-session retirement finishes.
            // Join that exact session's disposal original before sampling its release count.
            var retiredClose = session.DisposeAsync().AsTask();
            Console.Error.WriteLine("DIAGNOSTIC " + JsonSerializer.Serialize(new
            { source = "profile-view.reload", variant = rejectPublication ? "reject" : "publish", phase = "retirement-join",
                releases, currentGeneration = owner.Current.Generation, closeStatus = retiredClose.Status.ToString() }));
            await retiredClose;
            var beforeRetained = profile.CommandCatalog.ToString().Contains("before", StringComparison.Ordinal);
            Console.Error.WriteLine("DIAGNOSTIC " + JsonSerializer.Serialize(new
            { source = "profile-view.reload", variant = rejectPublication ? "reject" : "publish", phase = "retirement-joined",
                releases, currentGeneration = owner.Current.Generation, oldDisposed = session.Snapshot.IsDisposed, beforeRetained }));
            Check(session.Snapshot.IsDisposed && releases == 1 && beforeRetained);
            var templates = await PromptTemplateCliBinding.LoadAsync(Configuration(after));
            var staged = Gate(); var publish = Gate(); var registryBefore = profile.Registry;
            var rejection = new IOException("profile view publication rejected");
            profile.ConfigureReload(new(new(new object()), [], true, new([], []), new()
            {
                StageSettingsAsync = (_, _) => ValueTask.FromResult(new NativeHostReloadPayload(new object())),
                SyncQueueModesAsync = (_, _) => ValueTask.CompletedTask,
                ResetApiProvidersAsync = (_, _) => ValueTask.CompletedTask,
                ReloadResourcesAsync = (_, _) => ValueTask.CompletedTask,
                DescribeRuntimeAsync = (_, _) => ValueTask.FromResult(new HostReloadRuntime([], [])),
                BuildRuntimeAsync = async (candidate, _, _) =>
                {
                    var prepared = profile.PrepareRuntimeView(candidate, null, templates, null, null,
                        registryBefore.WithToolCatalog([], null), new Resource(() => { releases++; return Task.CompletedTask; }), null,
                        () => { if (rejectPublication) throw rejection; });
                    staged.TrySetResult(); await publish.Task; return prepared;
                },
                SessionShutdownAsync = (_, _, _) => ValueTask.CompletedTask,
                CleanupPreparationAsync = _ => ValueTask.CompletedTask,
                BeforeSessionStartAsync = (_, _) => ValueTask.CompletedTask,
                SessionStartAsync = (_, _, _) => ValueTask.CompletedTask,
                ReportUnhandledMcpServersAsync = (_, _) => ValueTask.CompletedTask,
                ExtendResourcesAsync = (_, _, _) => ValueTask.CompletedTask
            }));
            var previous = owner.Current;
            var reload = profile.ReloadAsync(previous);
            try
            {
                await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(ReferenceEquals(profile.Registry, registryBefore));
                Check(!profile.CommandCatalog.ToString().Contains("after", StringComparison.Ordinal));
            }
            finally { publish.TrySetResult(); await Settle(reload); }
            if (rejectPublication)
            {
                Check(Contains(await Failure(reload), rejection));
                try { _ = profile.CommandCatalog; throw new IOException("Old metadata was resurrected."); }
                catch (InvalidOperationException) { }
                await Settle(profile.DisposeAsync().AsTask()); Check(releases == 3); return;
            }
            var receipt = await reload;
            Check(receipt.Workflow.Authority == ResourceReloadAuthority.New && receipt.Workflow.Failures.IsEmpty);
            Check(!ReferenceEquals(profile.Registry, registryBefore));
            Check(profile.CommandCatalog.ToString().Contains("after", StringComparison.Ordinal));
            var result = await profile.InputAdmission.ReduceAsync(new("/after"), default);
            Check(result.Action == PromptInputAction.Transform && result.Text == "after template");
            await profile.DisposeAsync(); Check(releases == 3);
        }
        finally
        {
            await Settle(profile.DisposeAsync().AsTask());
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(root).StartsWith("pisharp-profile-view-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
    private static PromptTemplateCliConfiguration Configuration(string path) => new([new(path,
        new(path, "local", PromptTemplateSourceScope.Temporary, PromptTemplateSourceOrigin.TopLevel), ReportMissingPath: true)]);
    private sealed class Resource(Func<Task> close) : IAsyncDisposable
    { public ValueTask DisposeAsync() => new(close()); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Settle(Task task) { try { await task; } catch (Exception) { } }
    private static async Task<Exception> Failure(Task task)
    { try { await task; } catch (Exception error) { return task.Exception ?? error; } throw new IOException("Expected failure."); }
    private static bool Contains(Exception error, Exception original) => ReferenceEquals(error, original) ||
        (error is AggregateException aggregate ? aggregate.InnerExceptions.Any(child => Contains(child, original)) :
            error.InnerException is { } inner && Contains(inner, original));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Profile view contract failed."); }
}
