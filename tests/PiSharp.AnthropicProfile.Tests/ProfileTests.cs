using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;

internal static class ProfileTests
{
    internal sealed class Original(string name)
    {
        internal readonly string Name = name;
        internal Task? Task;
        internal Exception? Direct;
        internal AggregateException? Aggregate;
    }
    internal static readonly List<Original> Originals = [];
    private static void Check(bool condition, [CallerArgumentExpression(nameof(condition))] string? expression = null,
        [CallerLineNumber] int line = 0)
    { if (!condition) throw new IOException($"Profile control line {line}: {expression}"); }
    private static LiveSessionSelection Selection() => LiveSessionSelection.Parse("anthropic", "claude-sonnet-4-5", "4096");
    private static ValueTask<AuthenticationResolution> Resolve(string name = "ANTHROPIC_API_KEY", string marker = "SYNTHETIC_KEY") =>
        InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(new ProviderEnvironmentSnapshot([KeyValuePair.Create<string, string?>(name, marker)]),
            (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(null));
    private static readonly ImmutableArray<TranscriptEntry> Messages =
        [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"synthetic prompt\"}]}"))];
    private static IChatTransport MainTransport(OfflineSessionProfile profile) =>
        profile.Registry.Resolve(profile.SelectedModel, Messages, prepareLoadout: false).Configuration.Transport;
    private static SessionSummaryRequest Summary(OfflineSessionProfile profile) =>
        new(SessionSummaryKind.History, profile.SelectedModel, "synthetic system", "synthetic summary", 51, null, "synthetic-session");
    internal static async Task<T> Join<T>(string name, Func<Task<T>> factory)
    {
        var record = new Original(name); lock (Originals) Originals.Add(record);
        try { record.Task = factory(); return await (Task<T>)record.Task; }
        catch (Exception error) { record.Direct = error; record.Aggregate = record.Task?.Exception; throw; }
    }
    internal static async Task Join(string name, Func<Task> factory)
    {
        var record = new Original(name); lock (Originals) Originals.Add(record);
        try { record.Task = factory(); await record.Task; }
        catch (Exception error) { record.Direct = error; record.Aggregate = record.Task?.Exception; throw; }
    }
    private static async Task Collect(string name, Func<Task> factory, List<Exception> failures)
    { try { await Join(name, factory); } catch (Exception error) { failures.Add(error); } }
    private static Original Capture(string name, Task original)
    {
        var record = new Original(name) { Task = original };
        lock (Originals) Originals.Add(record);
        return record;
    }
    private static async Task AwaitOriginal(Original record)
    {
        var original = record.Task ?? throw new IOException("Captured original missing.");
        try { await original; }
        catch (Exception error) { record.Direct = error; record.Aggregate = original.Exception; throw; }
    }
    private static async Task CollectOriginal(Original record, List<Exception> failures)
    { try { await AwaitOriginal(record); } catch (Exception error) { failures.Add(error); } }
    private static async Task Fixture(Func<string, Handler, Task> body, bool held = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-anthropic-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var handler = new Handler(held); var failures = new List<Exception>();
        var start = Originals.Count;
        try { await body(root, handler); } catch (Exception error) { failures.Add(error); }
        finally
        {
            handler.Release.TrySetResult();
            try { handler.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { Directory.Delete(root, true); } catch (Exception error) { failures.Add(error); }
        }
        foreach (var original in Originals.Skip(start))
        {
            if (original.Task is { IsCompleted: false }) failures.Add(new IOException("Unjoined original: " + original.Name));
            if (failures.Count != 0)
            {
                if (original.Aggregate is { } aggregate) failures.Add(aggregate);
                if (original.Direct is { } direct) failures.Add(direct);
            }
        }
        if (failures.Count != 0) throw new AggregateException("Exact profile/body/cleanup originals.", failures.Distinct<Exception>(ReferenceEqualityComparer.Instance));
    }
    private static Task<OfflineSessionProfile> Create(string root, Handler handler, AuthenticationResolution? auth,
        LiveSessionSelection? selection = null, LiveSessionRuntime? runtime = null, CancellationToken token = default) =>
        OfflineSessionProfile.CreateAsync(root, Path.Combine(root, "session.jsonl"), null, [], [], [], token,
            liveSelection: selection ?? Selection(), liveRuntime: runtime,
            resolvedAnthropicAuthentication: auth, resolvedAnthropicHandler: auth is null ? null : handler);
    private static async Task Close(OfflineSessionProfile? profile, List<Exception> failures)
    { if (profile is not null) await Collect("profile-final-close", () => profile.DisposeAsync().AsTask(), failures); }
    private static async Task Drain(OfflineSessionProfile profile, CancellationToken token = default, List<StreamTerminalEvent>? terminals = null)
    {
        var count = 0;
        await foreach (var item in MainTransport(profile).StreamAsync(new(profile.SelectedModel, Messages), token))
            if (item is StreamTerminalEvent terminal) { count++; terminals?.Add(terminal); }
        Check(count == 1);
    }
    internal static Task Channels() => Fixture(async (root, handler) =>
    {
        foreach (var (name, marker, bearer) in new[] { ("ANTHROPIC_API_KEY", "SYNTHETIC_KEY", false),
            ("ANTHROPIC_AUTH_TOKEN", "SYNTHETIC_BEARER", true), ("ANTHROPIC_OAUTH_TOKEN", "SYNTHETIC_sk-ant-oat_KEY", true) })
        {
            OfflineSessionProfile? profile = null; var errors = new List<Exception>(); var before = handler.Captures.Count;
            try
            {
                var auth = await Resolve(name, marker);
                profile = await Join("profile-create", () => Create(root, handler, auth,
                    runtime: new(_ => throw new IOException("Explicit route read legacy credentials."), () => throw new IOException("Explicit route requested legacy handler."))));
                Check(profile.IsLive && profile.SelectedModel == Selection().Model && handler.Captures.Count == before);
                await Join("profile-main", () => Drain(profile));
                var summaryOne = await Join("profile-summary-one", () => profile.SummaryGenerator.GenerateAsync(Summary(profile)).AsTask());
                var summaryTwo = await Join("profile-summary-two", () => profile.SummaryGenerator.GenerateAsync(Summary(profile)).AsTask());
                Check(summaryOne.Text == "synthetic text" && summaryTwo.Text == "synthetic text");
                Check(handler.Captures.Count == before + 3);
                for (var i = 0; i < 3; i++)
                {
                    var capture = handler.Captures[before + i]; using var json = JsonDocument.Parse(capture.Body);
                    Check(capture.Key == (bearer ? null : marker) && capture.Authorization == (bearer ? "Bearer " + marker : null));
                    Check(json.RootElement.GetProperty("max_tokens").GetInt32() == (i == 0 ? 4096 : 51));
                    if (i > 0) Check(!capture.Body.Contains("cache_control", StringComparison.Ordinal));
                    await Join("profile-original-send-" + i, () => handler.Sends[before + i]);
                }
                await Join("profile-explicit-close", () => profile.DisposeAsync().AsTask());
                Check(!handler.Disposed);
            }
            catch (Exception error) { errors.Add(error); }
            finally { await Close(profile, errors); }
            if (errors.Count > 0) throw new AggregateException(errors);
        }
    });
    internal static Task Legacy() => Fixture(async (root, handler) =>
    {
        var reads = 0; var creates = 0; OfflineSessionProfile? profile = null; var errors = new List<Exception>();
        try
        {
            // Pi 1.1.0 resolution: the session's environment is read once per provider variable at start; ANTHROPIC_API_KEY alone
            // (no stored credential, no AUTH_TOKEN/OAUTH_TOKEN) is sent as x-api-key.
            profile = await Join("legacy-profile-create", () => Create(root, handler, null, runtime: new(name => { reads++; return name == "ANTHROPIC_API_KEY" ? "SYNTHETIC_KEY" : null; }, () => { creates++; return handler; })));
            await Join("legacy-profile-main", () => Drain(profile));
            Check(reads == LiveSessionRuntime.ProviderVariables.Length && creates == 1 && handler.Captures.Single().Key == "SYNTHETIC_KEY");
            await Join("legacy-original-send", () => handler.Sends.Single());
        }
        catch (Exception error) { errors.Add(error); }
        finally { await Close(profile, errors); }
        Check(!handler.Disposed); if (errors.Count > 0) throw new AggregateException(errors);
    });
    internal static Task Admission() => Fixture(async (root, handler) =>
    {
        var auth = await Resolve(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var attempts = new Func<Task<OfflineSessionProfile>>[] {
            () => Create(root, handler, new(AuthenticationDiagnostic.Missing)),
            () => Create(root, handler, auth, LiveSessionSelection.Parse("openai", "gpt-4o", "1024")),
            () => Create(root, handler, auth, token: canceled.Token),
            () => OfflineSessionProfile.CreateAsync(root, Path.Combine(root,"session.jsonl"), null, [], [], [], default,
                liveSelection: Selection(), resolvedAnthropicHandler: handler)
        };
        for (var i = 0; i < attempts.Length; i++)
        {
            var refused = false;
            try { await Join("profile-admission-refusal-" + i, attempts[i]); }
            catch (ArgumentException) when (i != 2) { refused = true; }
            catch (OperationCanceledException error) when (i == 2 && error.CancellationToken == canceled.Token) { refused = true; }
            Check(refused);
        }
        Check(handler.Captures.Count == 0 && !handler.Disposed && !Directory.EnumerateFileSystemEntries(root).Any());
    });
    internal static Task Held(bool summary) => Fixture(async (root, handler) =>
    {
        OfflineSessionProfile? profile = null; Task? drain = null, close = null; var errors = new List<Exception>();
        Original? drainRecord = null, closeRecord = null, sendRecord = null;
        var drainJoined = false; var closeJoined = false; var sendJoined = false;
        using var cancellation = new CancellationTokenSource(); var terminals = new List<StreamTerminalEvent>();
        try
        {
            var auth = await Resolve();
            profile = await Join("held-profile-create", () => Create(root, handler, auth));
            drain = summary ? profile.SummaryGenerator.GenerateAsync(Summary(profile), cancellation.Token).AsTask()
                : Drain(profile, cancellation.Token, terminals);
            drainRecord = Capture("held-profile-drain", drain);
            var admissionDeadline = Task.Delay(TimeSpan.FromSeconds(10));
            var entered = await Task.WhenAny(handler.Entered.Task, drain, admissionDeadline);
            if (entered != handler.Entered.Task)
            {
                if (handler.Captured.Task.IsCompletedSuccessfully) sendRecord = Capture("held-original-send", handler.Captured.Task.Result);
                if (entered == drain) { drainJoined = true; await AwaitOriginal(drainRecord); }
                throw new IOException("Held handler did not admit before actual drain settlement or bounded deadline.");
            }
            var captured = await Task.WhenAny(handler.Captured.Task, drain, admissionDeadline);
            if (captured != handler.Captured.Task)
            {
                if (captured == drain) { drainJoined = true; await AwaitOriginal(drainRecord); }
                throw new IOException("Held handler did not publish original SendAsync Task before bounded deadline.");
            }
            sendRecord = Capture("held-original-send", await handler.Captured.Task);
            close = profile.DisposeAsync().AsTask(); closeRecord = Capture("held-profile-close", close);
            Check(!drain.IsCompleted && !close.IsCompleted);
            cancellation.Cancel(); handler.Release.TrySetResult();
            Exception? canceled = null;
            drainJoined = true;
            try { await AwaitOriginal(drainRecord); } catch (OperationCanceledException error) { canceled = error; }
            if (summary) Check(canceled is OperationCanceledException && drain.IsCanceled);
            else Check(canceled is null && terminals is [StreamError { Reason: StopReason.Aborted }] && drain.IsCompletedSuccessfully);
            closeJoined = true; await AwaitOriginal(closeRecord); Check(close.IsCompletedSuccessfully && !handler.Disposed);
            sendJoined = true; await AwaitOriginal(sendRecord);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            handler.Release.TrySetResult();
            if (!drainJoined && drainRecord is not null) await CollectOriginal(drainRecord, errors);
            if (!closeJoined && closeRecord is not null) await CollectOriginal(closeRecord, errors);
            if (!sendJoined && sendRecord is not null) await CollectOriginal(sendRecord, errors);
            await Close(profile, errors);
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }, held: true);
    internal static Task CallbackClose() => Fixture(async (root, handler) =>
    {
        OfflineSessionProfile? profile = null; Task? descendant = null; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>(); var refused = false;
        try
        {
            var auth = await Resolve();
            profile = await Join("callback-profile-create", () => Create(root, handler, auth));
            handler.OnSend = () =>
            {
                try { _ = profile.DisposeAsync(); }
                catch (InvalidOperationException error) when (error.Message == "A resolved callback cannot join its own connection.") { refused = true; }
                descendant = Task.Run(async () => { await release.Task; await Join("inactive-inherited-profile-close", () => profile.DisposeAsync().AsTask()); });
                return ValueTask.CompletedTask;
            };
            await Join("callback-profile-main", () => Drain(profile));
            Check(refused && descendant is { IsCompleted: false });
            // Active refusal did not install a profile settlement: a second genuine main stream still works.
            handler.OnSend = null; await Join("callback-profile-second-main", () => Drain(profile));
            release.TrySetResult(); await Join("inherited-descendant-original", () => descendant ?? throw new IOException("Descendant missing."));
            Check(!handler.Disposed && handler.Captures.Count == 2);
            foreach (var send in handler.Sends) await Join("callback-original-send", () => send);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            release.TrySetResult();
            if (descendant is not null) await Collect("inherited-descendant-final", () => descendant, errors);
            await Close(profile, errors);
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    });
    private sealed class Handler(bool held) : HttpMessageHandler
    {
        internal readonly List<(string Body, string? Key, string? Authorization)> Captures = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<HttpResponseMessage>? SendOriginal;
        internal readonly TaskCompletionSource<Task<HttpResponseMessage>> Captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<Task<HttpResponseMessage>> Sends = [];
        internal Func<ValueTask>? OnSend; internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            SendOriginal = Send(); Sends.Add(SendOriginal); Captured.TrySetResult(SendOriginal); return SendOriginal;
            async Task<HttpResponseMessage> Send()
            {
                if (OnSend is not null) await OnSend();
                Captures.Add((await (request.Content ?? throw new IOException("Content missing.")).ReadAsStringAsync(),
                    request.Headers.TryGetValues("x-api-key", out var key) ? key.Single() : null,
                    request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.Single() : null));
                Entered.TrySetResult(); if (held) await Release.Task;
                const string frames = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"synthetic\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\nevent: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"synthetic text\"}}\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(frames, Encoding.UTF8, "text/event-stream") };
            }
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
