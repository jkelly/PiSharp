using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

internal static class InitialToolPreparationTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("initial preparation preserves original call and final authorized arguments through registered execution", Ownership),
        ("initial preparation never converts registered hook replacements or bypasses final denial", FinalAuthority),
        ("initial preparation cancellation and owner disposal join the actual callback finally", Disposal),
        ("initial preparation rejects malformed output and stale or multicast registration", Admission)
    ];
    private static async Task Ownership()
    {
        await using var registry = new ExtensionRegistry(); var preparations = 0; JsonData? received = null;
        PreparedToolAction? authorized = null; var raw = JsonData.Parse("{\"name\":42,\"opaque\":{\"null\":null}}");
        var prepared = JsonData.Parse("{\"name\":\"42\",\"opaque\":{\"null\":null}}");
        await registry.ActivateAsync("hello-owner", new Entry((entries, _) =>
        {
            entries.RegisterTool(new("hello", "hello", "authored pure preparation control", JsonData.EmptyObject, (input, context, token) =>
            {
                Check(context is IExtensionToolInvocationContext actual && actual.ToolCallId == "call|item", "Original invocation ID disappeared.");
                received = input; return ValueTask.FromResult(Result);
            }) { PrepareInitialArgumentsAsync = (input, token) =>
                { preparations++; Check(ReferenceEquals(raw, input), "Preparation did not receive the owned original arguments."); return ValueTask.FromResult(prepared); } });
            return ValueTask.CompletedTask;
        }));
        var invocation = Call(raw);
        var binding = Binding(registry, new Policy((original, action, _) =>
        { Check(ReferenceEquals(original, invocation), "Policy lost original invocation identity."); authorized = action; return ValueTask.FromResult(new ToolActionAuthorization(true)); }));
        var result = await Invoker(binding).ExecuteAsync(invocation, CancellationToken.None);
        Check(!result.IsError && preparations == 1 && ReferenceEquals(received, prepared) && ReferenceEquals(authorized!.Arguments, received), "Prepared execution ownership changed.");
        Check(raw.ToString() == "{\"name\":42,\"opaque\":{\"null\":null}}" && ReferenceEquals(raw, invocation.Call.Arguments), "Preparation rewrote original history.");
    }
    private static async Task FinalAuthority()
    {
        foreach (var replacement in new[] { false, true })
        {
            await using var registry = new ExtensionRegistry(); var prepared = 0; var effects = 0; var policies = 0;
            await registry.ActivateAsync("hello-owner", new Entry((entries, _) =>
            {
                entries.RegisterTool(new("hello", "hello", "control", JsonData.EmptyObject, (_, _, _) =>
                { effects++; return ValueTask.FromResult(Result); }) { PrepareInitialArgumentsAsync = (_, _) =>
                { prepared++; return ValueTask.FromResult(JsonData.Parse("{\"name\":\"42\"}")); } });
                if (replacement) entries.RegisterToolCallHandler(new("replace", (_, _, _) =>
                    ValueTask.FromResult<ExtensionToolCallPatch?>(new(JsonData.Parse("{\"name\":7}")))));
                return ValueTask.CompletedTask;
            }));
            var binding = Binding(registry, new Policy((_, _, _) => { policies++; return ValueTask.FromResult(new ToolActionAuthorization(false)); }));
            var result = await Invoker(binding).ExecuteAsync(Call(JsonData.Parse("{\"name\":42}")), CancellationToken.None);
            Check(result.Failure?.Kind == (replacement ? ToolFailureKind.InvalidArguments : ToolFailureKind.Blocked), "Final authority changed.");
            Check(prepared == 1 && effects == 0 && policies == (replacement ? 0 : 1), "Replacement conversion or denied effect occurred.");
        }
    }
    private static async Task Disposal()
    {
        await using var registry = new ExtensionRegistry(); var entered = Gate(); var canceled = Gate(); var release = Gate();
        var callbackFinally = false; var extensionDisposed = false;
        var scope = await registry.ActivateAsync("hello-owner", new Entry((entries, _) =>
        {
            entries.RegisterTool(new("hello", "hello", "control", JsonData.EmptyObject, (_, _, _) => ValueTask.FromResult(Result))
            { PrepareInitialArgumentsAsync = async (input, token) =>
                { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); return input; }
                  catch (OperationCanceledException) { canceled.TrySetResult(); await release.Task; throw; }
                  finally { callbackFinally = true; } } });
            return ValueTask.CompletedTask;
        }, () => { extensionDisposed = true; return ValueTask.CompletedTask; }));
        var pending = registry.PrepareToolArgumentsAsync(registry.CaptureSnapshot(), "hello", JsonData.Parse("{\"name\":\"Ada\"}")).AsTask();
        Task? closing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); closing = scope.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!closing.IsCompleted && !callbackFinally && !extensionDisposed, "Owner disposal detached the held preparation.");
        }
        finally { release.TrySetResult(); try { await pending; } catch (OperationCanceledException) { } if (closing is not null) await closing; }
        Check(callbackFinally && extensionDisposed, "Preparation finally or owner cleanup was lost.");
    }
    private static async Task Admission()
    {
        foreach (var output in new[] { "[]", "{\"name\":\"\\ud800\"}", "{\"name\":1e400}", "{\"name\":\"" + new string('x', 65_536) + "\"}" })
        {
            await using var registry = new ExtensionRegistry(); var effects = 0;
            var scope = await registry.ActivateAsync("hello-owner", new Entry((entries, _) =>
            {
                entries.RegisterTool(new("hello", "hello", "control", JsonData.EmptyObject, (_, _, _) =>
                    { effects++; return ValueTask.FromResult(Result); }) { PrepareInitialArgumentsAsync = (_, _) => ValueTask.FromResult(JsonData.Parse(output)) });
                return ValueTask.CompletedTask;
            }));
            var captured = registry.CaptureSnapshot(); var outcome = await Invoker(Binding(registry, new Policy((_, _, _) => ValueTask.FromResult(new ToolActionAuthorization(true)))))
                .ExecuteAsync(Call(JsonData.Parse("{\"name\":\"Ada\"}")), CancellationToken.None);
            Check(outcome.Failure?.Kind == ToolFailureKind.InvalidArguments && effects == 0, "Malformed preparation gained execution.");
            await scope.DisposeAsync();
            try { await registry.PrepareToolArgumentsAsync(captured, "hello", JsonData.EmptyObject); throw new InvalidOperationException("Stale preparation was admitted."); }
            catch (ExtensionRegistrationException error) when (error.Failure is ExtensionRegistrationFailure.InactiveScope or ExtensionRegistrationFailure.StaleSnapshot) { }
        }
        await using var rejected = new ExtensionRegistry();
        ExtensionToolArgumentPreparationCallback callback = (input, _) => ValueTask.FromResult(input); callback += callback;
        try
        {
            await rejected.ActivateAsync("bad-owner", new Entry((entries, _) =>
            { entries.RegisterTool(new("hello", "hello", "control", JsonData.EmptyObject, (_, _, _) => ValueTask.FromResult(Result))
                { PrepareInitialArgumentsAsync = callback }); return ValueTask.CompletedTask; }));
            throw new InvalidOperationException("Multicast preparation was admitted.");
        }
        catch (ExtensionRegistrationException) { }
    }
    private static readonly JsonData Result = JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}");
    private static ToolInvocation Call(JsonData arguments)
    {
        var tool = new ToolCallContent("call|item", "hello", arguments);
        var message = new AssistantMessage("openai-responses", "fixture", "model", 0, [tool], TokenUsage.Zero, StopReason.ToolUse);
        return new(message, tool, 0);
    }
    private static ExtensionAgentBinding Binding(ExtensionRegistry registry, IToolActionPolicy policy) => new(registry, policy,
        (_, input, _) => ValueTask.FromResult(input.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String));
    private static IToolExecutor Invoker(ExtensionAgentBinding binding) => binding.Tools.Single().Executor;
    private sealed class Policy(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>> run) : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => run(invocation, action, token); }
    private sealed class Entry(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask; }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
