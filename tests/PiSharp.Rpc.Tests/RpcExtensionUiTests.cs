using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;
using PiSharp.Extensions.Events;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Rpc.Ui;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RpcExtensionUiTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("ui-model", "openai-responses", "authored-offline");
    private static readonly JsonData Schema = JsonData.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"ui-model","api":"openai-responses","provider":"authored-offline","name":"UI fixture","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":4096,"maxTokens":128,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("rpc.ui-real-extension-four-dialogs-notifications-and-durable-next-turn", DialogsAndDurability);
        yield return ("rpc.ui-no-ui-and-explicit-capabilities-never-default-to-approval", NoUiAndCapabilities);
        yield return ("rpc.ui-controlled-timer-caller-session-and-extension-cancellation", TimersAndCancellation);
        yield return ("rpc.ui-duplicate-null-missing-stale-owner-and-registered-reducer-contexts", ResolutionAndGeneration);
        yield return ("rpc.ui-deferred-ordinary-frame-before-reply-and-shared-retained-bounds", DeferredAndBounds);
        yield return ("rpc.ui-eof-write-fault-concurrent-disposal-and-actual-callback-cleanup", ShutdownAndFault);
    }

    private static async Task DialogsAndDurability()
    {
        Fixture? fixture = null; IExtensionUiContext? saved = null; var outcomes = new List<ExtensionUiOutcomeKind>(); var effects = 0;
        var clock = new ManualClock();
        await using var owned = fixture = await Fixture.Create(async (context, token) =>
        {
            var ui = ((IExtensionUiContext)context).Ui; saved = (IExtensionUiContext)context;
            Equal(ExtensionUiMode.Rpc, ui.Capabilities.Mode); Equal(7L, ui.Capabilities.ConnectionGeneration); Equal(11L, ui.Capabilities.SessionGeneration);
            Check(!ui.Capabilities.Supports(ExtensionUiFeature.CustomTerminalComponent), "RPC advertised terminal components.");
            var selected = await ui.SelectAsync("choose", ["", "other"], new(0), token); Equal("", selected.Value); outcomes.Add(selected.Kind);
            var confirm = await ui.ConfirmAsync("confirm", "message", cancellationToken: token);
            Equal(false, confirm.Value); Check(!ExtensionUiSourceDefaults.Confirmation(confirm), "False answer became approval."); outcomes.Add(confirm.Kind);
            var allowed = await ui.ConfirmAsync("allow once", "explicit approval", cancellationToken: token); outcomes.Add(allowed.Kind);
            if (ExtensionUiSourceDefaults.Confirmation(allowed)) { await File.WriteAllTextAsync(fixture!.EffectPath, "one approved callback effect", token); effects++; }
            var input = await ui.InputAsync("input", cancellationToken: token); Equal("", input.Value); outcomes.Add(input.Kind);
            var editor = await ui.EditorAsync("editor", "", token); Equal("line\nπ\0🙂", editor.Value); outcomes.Add(editor.Kind);
            foreach (var notice in new ExtensionUiNotification[] { new ExtensionUiNotify("notice"), new ExtensionUiStatus("status", null),
                new ExtensionUiTextWidget("widget", []), new ExtensionUiTitle("title"), new ExtensionUiEditorText("text\0") })
                Equal(ExtensionUiOutcomeKind.Value, (await ui.PublishAsync(notice, token)).Kind);
            fixture!.Storage.Arm(); return Result("complete UI values\0π");
        }, clock: clock);
        fixture.Output.OnFlushed = record =>
        {
            if (Type(record) != "extension_ui_request") return;
            var body = record.Value; var id = body.GetProperty("id").GetString();
            switch (body.GetProperty("method").GetString())
            {
                case "select": Equal(0d, body.GetProperty("timeout").GetDouble()); fixture.Input.Push(new { type = "extension_ui_response", id, value = "" }); break;
                case "confirm": Check(!body.TryGetProperty("timeout", out _), "Absent timeout was invented."); fixture.Input.Push(new { type = "extension_ui_response", id, confirmed = body.GetProperty("title").GetString() == "allow once" }); break;
                case "input": Check(!body.TryGetProperty("placeholder", out _), "Absent placeholder became null."); fixture.Input.Push(new { type = "extension_ui_response", id, value = "" }); break;
                case "editor": Equal("", body.GetProperty("prefill").GetString()); Check(!body.TryGetProperty("timeout", out _), "Editor acquired a non-source timeout field."); fixture.Input.Push(new { type = "extension_ui_response", id, value = "line\nπ\0🙂" }); break;
                case "notify": Check(!body.TryGetProperty("notifyType", out _), "Absent notification kind was rewritten."); break;
                case "setStatus": Check(!body.TryGetProperty("statusText", out _), "Status clear became JSON null."); break;
                case "setWidget": Equal(0, body.GetProperty("widgetLines").GetArrayLength()); Check(!body.TryGetProperty("widgetPlacement", out _), "Widget default placement was invented."); break;
            }
        };
        await fixture.Prompt(); await Stage(fixture.Storage.CheckpointEntered.Task, fixture.Run, "tool-result durable checkpoint");
        Equal(1, fixture.Source.Requests.Count); Equal(5, outcomes.Count); Equal(0, clock.ActiveTimers); Equal(1, effects);
        Equal("one approved callback effect", await File.ReadAllTextAsync(fixture.EffectPath));
        Check(!fixture.Output.Records().Any(IsToolMessageEnd), "UI callback result overtook its durable checkpoint.");
        fixture.Storage.CheckpointRelease.TrySetResult(); await fixture.Idle();
        Equal(2, fixture.Source.Requests.Count); Equal(1, fixture.Plugin.Calls); Equal(1, fixture.Policy.Calls);
        var canonical = fixture.Session.Snapshot.Context.Messages.Single(entry => entry.Role == "toolResult");
        Equal("complete UI values\0π", canonical.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        Equal(canonical.WireBody.ToString(), fixture.Source.Requests[1].Messages.Single(entry => entry.Role == "toolResult").WireBody.ToString());
        Equal(10, fixture.Output.Records().Count(record => Type(record) == "extension_ui_request"));
        Check(!fixture.Output.Records().Any(record => Type(record) == "response" && record.Value.GetProperty("command").GetString() == "extension_ui_response"), "UI reply produced an ordinary acknowledgement.");
        Equal(ExtensionUiUnavailableReason.StaleContext, (await saved!.Ui.ConfirmAsync("late", "late")).UnavailableReason);
        var path = fixture.Session.Path; var expected = canonical.WireBody.ToString(); await fixture.CloseHost();
        await using var log = await SessionLogStore.OpenAsync(path);
        Equal(expected, log.Snapshot.Entries.Single(entry => entry.Type == "message" && entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult")
            .WireBody.Value.GetProperty("message").GetRawText());
    }

    private static async Task NoUiAndCapabilities()
    {
        foreach (var noUi in new[] { true, false })
        {
            var effects = 0; string? effectPath = null;
            await using var fixture = await Fixture.Create(async (context, token) =>
            {
                var ui = ((IExtensionUiContext)context).Ui;
                var answer = await ui.ConfirmAsync("allow", "effect?", cancellationToken: token);
                Equal(ExtensionUiOutcomeKind.Unavailable, answer.Kind);
                Equal(noUi ? ExtensionUiUnavailableReason.NoUi : ExtensionUiUnavailableReason.UnsupportedCapability, answer.UnavailableReason);
                if (ExtensionUiSourceDefaults.Confirmation(answer)) { await File.WriteAllTextAsync(effectPath!, "unexpected approval", token); effects++; }
                Check(!ui.Capabilities.Supports(ExtensionUiFeature.CustomTerminalComponent), "No terminal is bound.");
                if (noUi)
                {
                    Equal(ExtensionUiMode.Print, ui.Capabilities.Mode);
                    Equal(null, ExtensionUiSourceDefaults.Text(await ui.SelectAsync("select", [], cancellationToken: token)));
                    Equal(null, ExtensionUiSourceDefaults.Text(await ui.InputAsync("input", cancellationToken: token)));
                    Equal(null, ExtensionUiSourceDefaults.Text(await ui.EditorAsync("editor", cancellationToken: token)));
                }
                return Result("safe no approval");
            }, noUi: noUi, capabilities: [ExtensionUiFeature.Notify]);
            effectPath = fixture.EffectPath;
            await fixture.Prompt(); await fixture.Idle(); Equal(0, effects);
            Check(!File.Exists(effectPath), "Unavailable confirmation executed an actual callback file effect.");
            Check(!fixture.Output.Records().Any(record => Type(record) == "extension_ui_request"), "Unavailable dialog emitted a request.");
            Equal(1, fixture.Plugin.Calls); Equal(2, fixture.Source.Requests.Count);
        }
    }

    private static async Task TimersAndCancellation()
    {
        var clock = new ManualClock(); using var caller = new CancellationTokenSource();
        var timed = Gate(); var cancelled = Gate(); ExtensionUiOutcome<bool>? first = null, second = null;
        await using (var fixture = await Fixture.Create(async (context, token) =>
        {
            var ui = ((IExtensionUiContext)context).Ui;
            first = await ui.ConfirmAsync("timer", "timeout", new(100.75), token); timed.TrySetResult();
            second = await ui.ConfirmAsync("caller", "cancel", cancellationToken: caller.Token); cancelled.TrySetResult();
            return Result("timer and cancellation");
        }, clock: clock))
        {
            await fixture.Prompt(); var request = await fixture.Output.Wait(record => IsUi(record, "timer"));
            Equal(100.75, request.Value.GetProperty("timeout").GetDouble()); Equal(1, clock.ActiveTimers);
            clock.Advance(99); Check(!timed.Task.IsCompleted, "Timer fired before the source truncated delay."); clock.Advance(1);
            await Stage(timed.Task, fixture.Run, "typed timeout"); Equal(ExtensionUiOutcomeKind.TimedOut, first!.Kind);
            Check(!ExtensionUiSourceDefaults.Confirmation(first), "Timeout approved.");
            await fixture.Output.Wait(record => IsUi(record, "caller")); caller.Cancel();
            await Stage(cancelled.Task, fixture.Run, "caller cancellation"); Equal(ExtensionUiOutcomeKind.Cancelled, second!.Kind);
            await fixture.Idle(); Equal(0, clock.ActiveTimers);
        }
        foreach (var extensionLifetime in new[] { false, true })
        {
            using var sessionToken = new CancellationTokenSource(); ExtensionUiOutcome<bool>? outcome = null;
            await using var fixture = await Fixture.Create(async (context, token) =>
            {
                outcome = await ((IExtensionUiContext)context).Ui.ConfirmAsync("owned cancellation", "effect?", cancellationToken: token);
                Check(!ExtensionUiSourceDefaults.Confirmation(outcome), "Owned cancellation approved.");
                await CancellationObserved(token);
                token.ThrowIfCancellationRequested(); return Result("unreachable");
            }, sessionToken: sessionToken.Token, holdCallbackCleanup: true);
            await fixture.Prompt(); await fixture.Output.Wait(record => IsUi(record, "owned cancellation"));
            Task? closing = null;
            if (extensionLifetime) closing = fixture.Scope.DisposeAsync().AsTask(); else sessionToken.Cancel();
            await Stage(fixture.Plugin.CleanupEntered.Task, fixture.Run, "owned callback cancellation cleanup");
            Equal(ExtensionUiOutcomeKind.Cancelled, outcome!.Kind); Check(fixture.Plugin.TokenAtCleanup, "Actual linked callback token was not cancelled.");
            if (closing is not null) Check(!closing.IsCompleted, "Owner disposal skipped callback cleanup.");
            fixture.Plugin.CleanupRelease.TrySetResult(); if (closing is not null) await closing.WaitAsync(Deadline);
            await fixture.Idle(); Check(!File.Exists(fixture.EffectPath), "Owned cancellation performed an effect.");
        }
    }

    private static async Task ResolutionAndGeneration()
    {
        var outcomes = new List<ExtensionUiOutcomeKind>(); IExtensionUiContext? old = null;
        await using var fixture = await Fixture.Create(async (context, token) =>
        {
            old = (IExtensionUiContext)context;
            var first = await old.Ui.ConfirmAsync("cancel first", "deny", cancellationToken: token); outcomes.Add(first.Kind);
            var missing = await old.Ui.InputAsync("missing", cancellationToken: token); outcomes.Add(missing.Kind);
            var nil = await old.Ui.InputAsync("null", cancellationToken: token); outcomes.Add(nil.Kind);
            foreach (var title in new[] { "select missing", "select null" }) outcomes.Add((await old.Ui.SelectAsync(title, [], cancellationToken: token)).Kind);
            foreach (var title in new[] { "editor missing", "editor null" }) outcomes.Add((await old.Ui.EditorAsync(title, cancellationToken: token)).Kind);
            foreach (var title in new[] { "confirm missing", "confirm null" }) outcomes.Add((await old.Ui.ConfirmAsync(title, "deny", cancellationToken: token)).Kind);
            return Result("cancelled and unavailable");
        });
        await fixture.Prompt(); var firstRequest = await fixture.Output.Wait(record => IsUi(record, "cancel first")); var firstId = Id(firstRequest);
        fixture.Input.Push(new { type = "extension_ui_response", id = "unknown-id", confirmed = true });
        fixture.Input.Push(new { type = "extension_ui_response", id = firstId, cancelled = true, confirmed = true });
        var missingRequest = await fixture.Output.Wait(record => IsUi(record, "missing"));
        fixture.Input.Push(new { type = "extension_ui_response", id = firstId, confirmed = true });
        fixture.Input.Push(new { type = "extension_ui_response", id = Id(missingRequest) });
        var nullRequest = await fixture.Output.Wait(record => IsUi(record, "null"));
        fixture.Input.Push(new { type = "extension_ui_response", id = Id(nullRequest), value = (string?)null });
        foreach (var title in new[] { "select missing", "select null", "editor missing", "editor null", "confirm missing", "confirm null" })
        {
            var request = await fixture.Output.Wait(record => IsUi(record, title));
            var response = new Dictionary<string, object?> { ["type"] = "extension_ui_response", ["id"] = Id(request) };
            if (title.EndsWith("null", StringComparison.Ordinal)) response[title.StartsWith("confirm", StringComparison.Ordinal) ? "confirmed" : "value"] = null;
            fixture.Input.Push(response);
        }
        await fixture.Idle(); Equal(9, outcomes.Count); Equal(ExtensionUiOutcomeKind.Cancelled, outcomes[0]); Check(outcomes.Skip(1).All(kind => kind == ExtensionUiOutcomeKind.Unavailable), "Null/missing reply became a value.");
        Equal(ExtensionUiUnavailableReason.StaleContext, (await old!.Ui.ConfirmAsync("captured", "deny")).UnavailableReason);
        var prior = fixture.Registry.CaptureSnapshot(); var oldGeneration = fixture.Scope.OwnerGeneration;
        await fixture.Scope.DisposeAsync(); var next = new Plugin(async (context, token) =>
        {
            Check(context.OwnerGeneration > oldGeneration, "Replacement did not receive a new owner generation.");
            var reply = await ((IExtensionUiContext)context).Ui.ConfirmAsync("new owner", "deny", cancellationToken: token);
            Equal(ExtensionUiOutcomeKind.Value, reply.Kind); Equal(false, reply.Value); return Result("new owner");
        });
        await using var replacement = await fixture.Registry.ActivateAsync("ui-owner", next);
        var stale = await Throws<ExtensionRegistrationException>(() => fixture.Registry.InvokeCommandAsync(prior, "ui_command", JsonData.EmptyObject).AsTask());
        Equal(ExtensionRegistrationFailure.StaleSnapshot, stale.Failure);
        var command = fixture.Registry.InvokeCommandAsync(fixture.Registry.CaptureSnapshot(), "ui_command", JsonData.EmptyObject).AsTask();
        var newRequest = await fixture.Output.Wait(record => IsUi(record, "new owner")); Check(Id(newRequest) != firstId, "Correlation ID was reused.");
        fixture.Input.Push(new { type = "extension_ui_response", id = firstId, confirmed = true });
        fixture.Input.Push(new { type = "extension_ui_response", id = Id(newRequest), confirmed = false }); await command.WaitAsync(Deadline);
        // A real registered reducer also receives the leased optional UI context, rather than the standalone reducer context.
        var reducers = new RegisteredExtensionEventDispatcher(fixture.Registry, static _ => { });
        var reduced = await reducers.DispatchInputAsync(fixture.Registry.CaptureSnapshot(), new ExtensionInputEvent("hello", ExtensionInputSource.Rpc));
        Equal(ExtensionInputAction.Continue, reduced.Action); Equal(1, next.ReducerCalls);
        await fixture.Send("get_state", "after-stale");
        Equal(0, fixture.Output.Records().Count(record => Type(record) == "response" && record.Value.GetProperty("command").GetString() == "extension_ui_response"));
    }

    private static async Task DeferredAndBounds()
    {
        var replied = Gate();
        await using (var fixture = await Fixture.Create(async (context, token) =>
        {
            var result = await ((IExtensionUiContext)context).Ui.ConfirmAsync("capacity dialog", "deny", cancellationToken: token);
            Equal(ExtensionUiOutcomeKind.Value, result.Kind); Equal(false, result.Value); replied.TrySetResult(); return Result("reply bypassed FIFO");
        }, dispatchOptions: new(MaximumConcurrentCommands: 1)))
        {
            await fixture.Prompt(); var request = await fixture.Output.Wait(record => IsUi(record, "capacity dialog"));
            fixture.Output.HoldResponseId = "ordinary-one";
            fixture.Input.Push(new { type = "get_state", id = "ordinary-one" }); await Stage(fixture.Output.WriteHeld.Task, fixture.Run, "first ordinary output hold");
            fixture.Input.Push(new { type = "get_state", id = "ordinary-two" });
            fixture.Input.Push(new { type = "extension_ui_response", id = Id(request), confirmed = false });
            await Stage(replied.Task, fixture.Run, "reply behind deferred ordinary frame");
            Check(!fixture.Output.Records().Any(record => IsResponse(record, "ordinary-two")), "Deferred command overtook output backpressure.");
            fixture.Output.WriteRelease.TrySetResult(); await fixture.Output.Wait(record => IsResponse(record, "ordinary-one"));
            await fixture.Output.Wait(record => IsResponse(record, "ordinary-two")); await fixture.Idle();
            Equal(1, fixture.Output.Records().Count(record => IsResponse(record, "ordinary-one"))); Equal(1, fixture.Output.Records().Count(record => IsResponse(record, "ordinary-two")));
            Equal(1, fixture.Output.MaximumConcurrentWrites);
        }
        await using (var fixture = await Fixture.Create(async (context, token) =>
        {
            var ui = ((IExtensionUiContext)context).Ui; var one = ui.ConfirmAsync("one", "deny", cancellationToken: token).AsTask();
            var two = await ui.ConfirmAsync("two", "deny", cancellationToken: token);
            Check(!ExtensionUiSourceDefaults.Confirmation(two), "Over-budget second dialog approved.");
            var first = await one; Check(!ExtensionUiSourceDefaults.Confirmation(first), "Poisoned first dialog approved.");
            return Result("limit");
        }, uiOptions: new(MaximumOutstandingRequests: 1)))
        {
            fixture.Input.Push(new { type = "prompt", id = "limit", message = "run" });
            Equal(RpcDispatchFailure.ResourceLimit, (await Throws<RpcDispatchException>(() => fixture.Run)).Failure);
            Equal(1, fixture.Source.Requests.Count); Check(!File.Exists(fixture.EffectPath), "Quota closure performed an effect."); Check(fixture.Plugin.Cleaned, "Quota closure skipped actual callback cleanup.");
        }
        await using (var fixture = await Fixture.Create(async (context, token) =>
        {
            var reply = await ((IExtensionUiContext)context).Ui.ConfirmAsync("byte limit", "deny", cancellationToken: token);
            Check(!ExtensionUiSourceDefaults.Confirmation(reply), "Byte exhaustion approved."); return Result("closed");
        }, dispatchOptions: new(MaximumConcurrentCommands: 1), uiOptions: new(MaximumDeferredOrdinaryFrames: 1, MaximumRetainedOrdinaryBytes: 180)))
        {
            await fixture.Prompt(); await fixture.Output.Wait(record => IsUi(record, "byte limit"));
            fixture.Output.HoldResponseId = "byte-one"; fixture.Input.Push(new { type = "get_state", id = "byte-one" });
            await Stage(fixture.Output.WriteHeld.Task, fixture.Run, "bounded ordinary hold");
            fixture.Input.Push(new { type = "get_state", id = "byte-two", opaque = new string('π', 90) });
            await Stage(fixture.Plugin.CleanupEntered.Task, fixture.Run, "ordinary byte poison callback cleanup");
            fixture.Output.WriteRelease.TrySetResult(); Equal(RpcDispatchFailure.ResourceLimit, (await Throws<RpcDispatchException>(() => fixture.Run)).Failure);
            Equal(1, fixture.Source.Requests.Count); Check(!File.Exists(fixture.EffectPath), "Ordinary byte poison performed an effect.");
        }
        await using (var fixture = await Fixture.Create(async (context, token) =>
        {
            var reply = await ((IExtensionUiContext)context).Ui.ConfirmAsync("frame limit", "deny", cancellationToken: token);
            Check(!ExtensionUiSourceDefaults.Confirmation(reply), "Deferred count exhaustion approved."); return Result("closed");
        }, dispatchOptions: new(MaximumConcurrentCommands: 1), uiOptions: new(MaximumDeferredOrdinaryFrames: 1)))
        {
            await fixture.Prompt(); await fixture.Output.Wait(record => IsUi(record, "frame limit"));
            fixture.Output.HoldResponseId = "frame-one"; fixture.Input.Push(new { type = "get_state", id = "frame-one" });
            await Stage(fixture.Output.WriteHeld.Task, fixture.Run, "bounded deferred-count hold");
            fixture.Input.Push(new { type = "get_state", id = "frame-two" }); fixture.Input.Push(new { type = "get_state", id = "frame-three" });
            await Stage(fixture.Plugin.CleanupEntered.Task, fixture.Run, "deferred-count poison callback cleanup");
            fixture.Output.WriteRelease.TrySetResult(); Equal(RpcDispatchFailure.ResourceLimit, (await Throws<RpcDispatchException>(() => fixture.Run)).Failure);
            Equal(1, fixture.Source.Requests.Count);
            Equal(1, fixture.Output.Records().Count(record => IsResponse(record, "frame-two")));
            Check(!fixture.Output.Records().Single(record => IsResponse(record, "frame-two")).Value.GetProperty("success").GetBoolean(), "Deferred frame executed after poison.");
        }
    }

    private static async Task ShutdownAndFault()
    {
        foreach (var fault in new[] { false, true })
        {
            ExtensionUiOutcome<bool>? result = null; string? effectPath = null; var effects = 0;
            await using var fixture = await Fixture.Create(async (context, token) =>
            {
                result = await ((IExtensionUiContext)context).Ui.ConfirmAsync("shutdown", "effect?", cancellationToken: token);
                if (ExtensionUiSourceDefaults.Confirmation(result)) { await File.WriteAllTextAsync(effectPath!, "unexpected approval", token); effects++; }
                Check(!ExtensionUiSourceDefaults.Confirmation(result), "Disconnect/fault approved.");
                await CancellationObserved(token); return Result("shutdown");
            }, holdCallbackCleanup: true, failUiWrite: fault, holdOutputCleanup: fault);
            effectPath = fixture.EffectPath;
            fixture.Input.Push(new { type = "prompt", id = "shutdown-prompt", message = "run" });
            if (fault)
            {
                await Stage(fixture.Output.DisposeEntered.Task, fixture.Run, "failed UI write actual stream cleanup");
                Check(!fixture.Run.IsCompleted, "Output failure abandoned owned stream cleanup."); fixture.Output.DisposeRelease.TrySetResult();
            }
            else
            {
                await fixture.Output.Wait(record => IsUi(record, "shutdown"));
                await fixture.Send("steer", "retained-steer", "retain"); await fixture.Send("follow_up", "retained-followup", "retain");
                fixture.Input.Complete();
            }
            await Stage(fixture.Plugin.CleanupEntered.Task, fixture.Run, "real UI callback finally");
            Check(fixture.Plugin.TokenAtCleanup, "Shutdown did not cancel the actual callback token.");
            Equal(ExtensionUiOutcomeKind.Unavailable, result!.Kind);
            var first = fixture.Dispatcher.DisposeAsync().AsTask(); var second = fixture.Dispatcher.DisposeAsync().AsTask();
            Check(ReferenceEquals(first, second) && !first.IsCompleted, "Concurrent disposal did not share actual callback settlement.");
            fixture.Plugin.CleanupRelease.TrySetResult();
            if (fault)
            { Equal(RpcDispatchFailure.OutputFailed, (await Throws<RpcDispatchException>(() => fixture.Run)).Failure); await Ignore(first); }
            else
            {
                await fixture.Run.WaitAsync(Deadline); await first.WaitAsync(Deadline);
                Equal(1, fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length); Equal(1, fixture.Session.GetPendingInputQueueSnapshot().FollowUpMessages.Length);
            }
            Equal(1, fixture.Source.Requests.Count); Equal(0, effects); Check(!File.Exists(effectPath), "Shutdown/fault performed an actual callback effect."); Equal(1, fixture.Output.Disposals);
            Check(fixture.Plugin.Cleaned && fixture.Session.Snapshot.IsDisposed && !fixture.Session.Snapshot.Agent.IsRunning, "Owned shutdown did not join callback/session cleanup.");
        }
    }

    private static JsonData Result(string text) => JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } }, details = new { nil = (string?)null, number = 0.5 }, usage = new { source = "ui" }, isError = false }));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task CancellationObserved(CancellationToken token)
    { var observed = Gate(); using var registration = token.UnsafeRegister(_ => observed.TrySetResult(), null); await observed.Task; }
    private static string Type(JsonData record) => record.Value.GetProperty("type").GetString()!;
    private static string Id(JsonData record) => record.Value.GetProperty("id").GetString()!;
    private static bool IsUi(JsonData record, string title) => Type(record) == "extension_ui_request" && record.Value.TryGetProperty("title", out var value) && value.GetString() == title;
    private static bool IsResponse(JsonData record, string id) => Type(record) == "response" && record.Value.TryGetProperty("id", out var value) && value.GetString() == id;
    private static bool IsToolMessageEnd(JsonData record) => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult";
    private static async Task Stage(Task witness, Task run, string stage)
    { var completed = await Task.WhenAny(witness, run).WaitAsync(Deadline); if (completed != witness) { await run; throw new InvalidOperationException("Run settled before " + stage); } await witness; }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Deadline); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Ignore(Task task) { try { await task.WaitAsync(Deadline); } catch (RpcDispatchException) { } }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Expected " + expected + "; actual " + actual);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory; private bool closed;
        public ExtensionRegistry Registry { get; } public RegistrationScope Scope { get; } public Plugin Plugin { get; }
        public PersistentAgentSession Session { get; } public RpcSessionDispatcher Dispatcher { get; } public Task Run { get; }
        public Feed Input { get; } public Capture Output { get; } public Source Source { get; } public Policy Policy { get; } public AuditedFactory Storage { get; }
        public string EffectPath => System.IO.Path.Combine(directory, "approved-effect.txt");
        private Fixture(string directory, ExtensionRegistry registry, RegistrationScope scope, Plugin plugin,
            PersistentAgentSession session, RpcSessionDispatcher dispatcher, Feed input, Capture output, Source source, Policy policy, AuditedFactory storage)
        { this.directory = directory; Registry = registry; Scope = scope; Plugin = plugin; Session = session; Dispatcher = dispatcher; Input = input; Output = output; Source = source; Policy = policy; Storage = storage; Run = dispatcher.RunAsync(new JsonlReader(input)); }
        public static async Task<Fixture> Create(Func<IExtensionToolContext, CancellationToken, ValueTask<JsonData>> callback,
            ManualClock? clock = null, bool noUi = false, ImmutableArray<ExtensionUiFeature>? capabilities = null,
            CancellationToken sessionToken = default, bool holdCallbackCleanup = false, RpcDispatchOptions? dispatchOptions = null,
            RpcExtensionUiOptions? uiOptions = null, bool failUiWrite = false, bool holdOutputCleanup = false)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-rpc-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var ui = noUi ? null : new RpcExtensionUiCoordinator(uiOptions, clock, 7, 11, capabilities);
            var registry = new ExtensionRegistry(uiProvider: ui); var plugin = new Plugin(callback, holdCallbackCleanup);
            var scope = await registry.ActivateAsync("ui-owner", plugin); var policy = new Policy();
            var binding = new ExtensionAgentBinding(registry, policy, static (tool, arguments, token) =>
            { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(tool.Parameters.ToString() == Schema.ToString() && arguments.Value.ValueKind == JsonValueKind.Object && !arguments.Value.EnumerateObject().Any()); }, sessionCancellationToken: sessionToken);
            var path = System.IO.Path.Combine(directory, "session.jsonl"); var codec = new SessionEntryCodec();
            var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "ui-session", timestamp = "2026-10-01T00:00:00.000Z", cwd = directory }));
            await using (var seed = await SessionLogStore.CreateNewAsync(path, header))
                await seed.AppendAsync([codec.Parse(JsonSerializer.Serialize(new { type = "message", id = "declarations", parentId = (string?)null,
                    timestamp = "2026-10-01T00:00:00.000Z", message = binding.CreateDeclarationMessage("native UI tool", 123).WireBody.Value }))]);
            var source = new Source(); var storage = new AuditedFactory(); var next = 0;
            var session = await PersistentAgentSession.OpenAsync(path, new AgentConfiguration(Model, source, binding.Tools), () => 123,
                () => "ui-entry-" + Interlocked.Increment(ref next), new(SessionLogStoreOptions: new(StorageFactory: storage)));
            var input = new Feed(); var output = new Capture(failUiWrite, holdOutputCleanup);
            var dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(output, ownership: JsonlStreamOwnership.Owned), () => 123,
                [new(Model, ModelWire)], dispatchOptions, extensionUi: ui);
            return new(directory, registry, scope, plugin, session, dispatcher, input, output, source, policy, storage);
        }
        public Task Prompt() => Send("prompt", "prompt", "run native UI");
        public async Task Send(string type, string id, string? message = null)
        { if (message is null) Input.Push(new { type, id }); else Input.Push(new { type, id, message }); var response = await Output.Wait(record => IsResponse(record, id)); Check(response.Value.GetProperty("success").GetBoolean(), "Ordinary command failed."); }
        public Task Idle() => Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        public async Task CloseHost()
        {
            if (closed) return; closed = true; Input.Complete(); Output.WriteRelease.TrySetResult(); Output.DisposeRelease.TrySetResult();
            Plugin.CleanupRelease.TrySetResult(); Storage.CheckpointRelease.TrySetResult();
            await Ignore(Run); await Ignore(Dispatcher.DisposeAsync().AsTask()); await Session.DisposeAsync(); await Registry.DisposeAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await CloseHost(); await Input.DisposeAsync();
            var target = System.IO.Path.GetFullPath(directory); var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (System.IO.Path.GetDirectoryName(target) != parent || !System.IO.Path.GetFileName(target).StartsWith("PiSharp-rpc-ui-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned UI fixture cleanup target.");
            Directory.Delete(target, recursive: true);
        }
    }
    private sealed class Plugin(Func<IExtensionToolContext, CancellationToken, ValueTask<JsonData>> callback, bool holdCleanup = false) : IPiSharpExtension
    {
        public int Calls, ReducerCalls; public bool TokenAtCleanup, Cleaned;
        public TaskCompletionSource CleanupEntered { get; } = Gate(); public TaskCompletionSource CleanupRelease { get; } = Gate();
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        {
            registry.RegisterTool(new("ui-tool", "ui_tool", "Complete empty-object UI fixture schema", Schema, Execute));
            registry.RegisterCommand(new("ui-command", "ui_command", "UI fixture command", async (_, context, work) =>
                { await callback(new CommandToolContext(context), work); }));
            registry.RegisterInputHandler(new("ui-input", (input, context, work) =>
            {
                Check(context is IExtensionUiContext && ((IExtensionUiContext)context).Ui.Capabilities.Mode == ExtensionUiMode.Rpc, "Registered reducer lost its actual UI binding.");
                ReducerCalls++; return ValueTask.FromResult<ExtensionInputPatch?>(null);
            }));
            return ValueTask.CompletedTask;
        }
        private async ValueTask<JsonData> Execute(JsonData arguments, IExtensionToolContext context, CancellationToken token)
        {
            Calls++;
            try { return await callback(context, token); }
            finally { TokenAtCleanup = token.IsCancellationRequested; CleanupEntered.TrySetResult(); if (holdCleanup) await CleanupRelease.Task; Cleaned = true; }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CommandToolContext(IExtensionCommandContext context) : IExtensionToolContext, IExtensionUiContext
    {
        public string OwnerId => context.OwnerId; public long OwnerGeneration => context.OwnerGeneration;
        public CancellationToken OperationCancellationToken => context.OperationCancellationToken;
        public CancellationToken SessionCancellationToken => context.SessionCancellationToken;
        public CancellationToken ExtensionLifetimeCancellationToken => context.ExtensionLifetimeCancellationToken;
        public IExtensionUi Ui => ((IExtensionUiContext)context).Ui;
    }
    private sealed class Policy : IToolActionPolicy
    {
        public int Calls;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Check(action.Kind == PreparedToolActionKind.Extension && action.CommandArguments.IsEmpty && action.Environment.IsEmpty && action.WorkingDirectory is null, "Dialog acquired executable host authority."); return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class Source : IChatTransport
    {
        public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var index = Requests.Count; Requests.Add(request);
            if (index > 1) throw new InvalidOperationException("Unexpected queued provider continuation.");
            var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123,
                index == 0 ? [new ToolCallContent("ui-call", "ui_tool", JsonData.EmptyObject)] : [new TextContent("done")], TokenUsage.Zero, index == 0 ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            if (index == 0)
            { var call = (ToolCallContent)message.Content[0]; yield return new ToolCallStarted(0, call); yield return new ToolCallDelta(0, "{}"); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextDelta(0, "done"); yield return new TextEnded(0, "done"); }
            yield return new StreamDone(message.StopReason, message); await Task.CompletedTask;
        }
    }
    private sealed class Feed : Stream
    {
        private readonly Channel<byte[]> channel = Channel.CreateBounded<byte[]>(32); private byte[]? current; private int position;
        public void Push(object value)
        { var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n"); Check(bytes.Length <= 1_048_576 && channel.Writer.TryWrite(bytes), "Bounded authored input feed exhausted."); }
        public void Complete() => channel.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (current is null || position == current.Length)
            { if (!await channel.Reader.WaitToReadAsync(cancellationToken)) return 0; current = await channel.Reader.ReadAsync(cancellationToken); position = 0; }
            var count = Math.Min(buffer.Length, current.Length - position); current.AsMemory(position, count).CopyTo(buffer); position += count; return count;
        }
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class Capture(bool failUiWrite, bool holdCleanup) : Stream
    {
        private readonly object gate = new(); private readonly List<JsonData> records = [];
        private readonly List<(Func<JsonData, bool> Match, TaskCompletionSource<JsonData> Ready)> waiters = []; private JsonData? written; private int active;
        public Action<JsonData>? OnFlushed { get; set; } public string? HoldResponseId { get; set; }
        public int MaximumConcurrentWrites, Disposals;
        public TaskCompletionSource WriteHeld { get; } = Gate(); public TaskCompletionSource WriteRelease { get; } = Gate();
        public TaskCompletionSource DisposeEntered { get; } = Gate(); public TaskCompletionSource DisposeRelease { get; } = Gate();
        public JsonData[] Records() { lock (gate) return records.ToArray(); }
        public Task<JsonData> Wait(Func<JsonData, bool> match)
        {
            lock (gate)
            {
                var previous = records.FirstOrDefault(match); if (previous is not null) return Task.FromResult(previous);
                Check(waiters.Count < 32, "Authored output waiter bound exhausted."); var ready = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); waiters.Add((match, ready)); return ready.Task.WaitAsync(Deadline);
            }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites, Interlocked.Increment(ref active));
            try
            {
                var text = Encoding.UTF8.GetString(bytes.Span); Check(text.EndsWith('\n') && text.Count(c => c == '\n') == 1, "Shared output interleaved frames.");
                var record = JsonData.Parse(text);
                if (HoldResponseId is { } id && IsResponse(record, id)) { WriteHeld.TrySetResult(); await WriteRelease.Task.WaitAsync(token); }
                if (failUiWrite && Type(record) == "extension_ui_request") throw new IOException("private UI output failure");
                written = record;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public override Task FlushAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var record = written ?? throw new InvalidOperationException("Missing complete write."); written = null;
            List<TaskCompletionSource<JsonData>> ready = [];
            lock (gate)
            {
                Check(records.Count < 256, "Authored output bound exhausted."); records.Add(record);
                for (var i = waiters.Count - 1; i >= 0; i--) if (waiters[i].Match(record)) { ready.Add(waiters[i].Ready); waiters.RemoveAt(i); }
            }
            OnFlushed?.Invoke(record); foreach (var waiter in ready) waiter.TrySetResult(record); return Task.CompletedTask;
        }
        public override async ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); DisposeEntered.TrySetResult(); if (holdCleanup) await DisposeRelease.Task; }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class AuditedFactory : ISessionLogStorageFactory
    {
        private int armed; public TaskCompletionSource CheckpointEntered { get; } = Gate(); public TaskCompletionSource CheckpointRelease { get; } = Gate();
        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => new AuditedStorage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token), this);
        private sealed class AuditedStorage(ISessionLogStorage inner, AuditedFactory owner) : ISessionLogStorage
        {
            public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
            public void PositionForAppend(long expectedLength) => inner.PositionForAppend(expectedLength);
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes); public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
            public async ValueTask BeforeCheckpointAsync() { await inner.BeforeCheckpointAsync(); if (Interlocked.Exchange(ref owner.armed, 0) != 0) { owner.CheckpointEntered.TrySetResult(); await owner.CheckpointRelease.Task; } }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<Timer> timers = []; private long milliseconds;
        public int ActiveTimers { get { lock (gate) return timers.Count(timer => !timer.Disposed); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Check(period == Timeout.InfiniteTimeSpan, "Unexpected periodic UI timer."); lock (gate) { Check(timers.Count < 32, "Authored timer bound exhausted."); var timer = new Timer(this, callback, state, milliseconds + (long)dueTime.TotalMilliseconds); timers.Add(timer); return timer; }
        }
        public void Advance(long amount)
        {
            Timer[] ready; lock (gate) { milliseconds += amount; ready = timers.Where(timer => !timer.Disposed && !timer.Fired && timer.Due <= milliseconds).ToArray(); foreach (var timer in ready) { timer.Fired = true; timer.Executing = true; } }
            foreach (var timer in ready) try { timer.Callback(timer.State); } finally { lock (gate) { timer.Executing = false; if (timer.Disposed) timer.Settled.TrySetResult(); } }
        }
        private sealed class Timer(ManualClock owner, TimerCallback callback, object? state, long due) : ITimer
        {
            internal readonly TimerCallback Callback = callback; internal readonly object? State = state; internal readonly long Due = due;
            internal bool Disposed, Fired, Executing; internal readonly TaskCompletionSource Settled = Gate();
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { lock (owner.gate) { Disposed = true; if (!Executing) Settled.TrySetResult(); } }
            public ValueTask DisposeAsync() { Dispose(); return new(Settled.Task); }
        }
    }
}
