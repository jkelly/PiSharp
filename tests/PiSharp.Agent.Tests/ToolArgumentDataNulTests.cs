using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class ToolArgumentDataNulTests
{
    // Exact prior negative vector, now exercised positively through the complete prepared pipeline.
    private const string PriorArgumentVector = """{"input":"bad\u0000argument"}""";
    private const string PriorTransformedArgumentVector = """{"command":"a\u0000b"}""";
    private static readonly JsonData Initial = JsonData.Parse("""{ "input":"initial\u0000value", "nested":[null,{"text":"\u0000\u03c0","number":1.00}], "wide":9007199254740993 }""");
    private static readonly JsonData Final = JsonData.Parse("""{ "input":"final\u0000value", "nested":["\u0000",{"text":"\u03c0\u0000"}], "number":0.5, "nil":null }""");

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("tool argument NUL data preserves exact ownership through initial/final schema, policy and execution", OwnershipAndTransforms);
        yield return ("tool argument NUL data cannot authorize NUL executable target, operation, cwd, argv or environment", ExecutableStringsRemainDenied);
        yield return ("tool argument NUL data retains exact raw budgets, scalar Unicode, duplicate, output-number and cancellation guards", AdmissionBounds);
    }

    private static async Task OwnershipAndTransforms()
    {
        foreach (var raw in new[] { PriorArgumentVector, Initial.ToString() })
        foreach (var kind in new[] { PreparedToolActionKind.Extension, PreparedToolActionKind.Path, PreparedToolActionKind.Command })
        {
            var owned = JsonData.Parse(raw); var invocation = Invocation(owned);
            var schemaInputs = new List<JsonData>(); var adapter = new Adapter(kind);
            adapter.Validate = action =>
            {
                schemaInputs.Add(action.Arguments);
                // Entire fixture schema {} imposes no member constraints; prepared input must be an object.
                return action.Arguments.Value.ValueKind == JsonValueKind.Object;
            };
            var policy = new Policy();
            var result = await new ToolInvoker([adapter], policy).ExecuteAsync(invocation, default);
            Check(!result.IsError, "Owned argument data NUL became an admission failure.");
            Equal(1, adapter.Prepares); Equal(2, schemaInputs.Count); Equal(1, policy.Actions.Count); Equal(1, adapter.Executed.Count);
            Check(ReferenceEquals(owned, adapter.Prepared!.Arguments) && schemaInputs.All(input => ReferenceEquals(owned, input)) &&
                ReferenceEquals(owned, policy.Actions.Single().Arguments), "Preparation/schema/policy rewrote owned argument data.");
            Check(ReferenceEquals(policy.Actions.Single(), adapter.Executed.Single()), "Execution lost the exact final policy action.");
            Equal(raw, owned.ToString()); Equal(raw, adapter.Executed.Single().Arguments.ToString());
            Check(owned.Value.GetProperty("input").GetString()!.Contains('\0'), "Decoded argument data lost NUL.");
        }

        var initialText = Initial.ToString(); var finalText = Final.ToString();
        var transformedInputs = new List<JsonData>(); var transformedAdapter = new Adapter(PreparedToolActionKind.Extension);
        transformedAdapter.Validate = action => { transformedInputs.Add(action.Arguments); return true; };
        PreparedToolAction? transformed = null; var transformedPolicy = new Policy();
        var transformedResult = await new ToolInvoker([transformedAdapter], transformedPolicy,
            [(_, action, _) => { var finalAction = action with { Arguments = Final }; transformed = finalAction; return ValueTask.FromResult(finalAction); }])
            .ExecuteAsync(Invocation(Initial), default);
        Check(!transformedResult.IsError, "Final revalidation rejected admitted NUL JSON values.");
        Equal(2, transformedInputs.Count);
        Check(ReferenceEquals(Initial, transformedInputs[0]) && ReferenceEquals(Final, transformedInputs[1]), "Schema did not revalidate the final owned input.");
        Check(ReferenceEquals(transformed, transformedPolicy.Actions.Single()) && ReferenceEquals(transformed, transformedAdapter.Executed.Single()) &&
            ReferenceEquals(Final, transformedAdapter.Executed.Single().Arguments), "Final policy/execution lost the identical transformed action/data.");
        Equal(initialText, Initial.ToString()); Equal(finalText, Final.ToString());
        Equal("1.00", Initial.Value.GetProperty("nested")[1].GetProperty("number").GetRawText());
        Equal("9007199254740993", Initial.Value.GetProperty("wide").GetRawText()); Equal("0.5", Final.Value.GetProperty("number").GetRawText());

        // Preserve the second prior negative's exact transform and fixed safe Path action shape.
        var commandData = JsonData.Parse(PriorTransformedArgumentVector);
        var priorSchemaInputs = new List<JsonData>(); var priorAdapter = new Adapter(PreparedToolActionKind.Path, priorShape: true);
        priorAdapter.Validate = action => { priorSchemaInputs.Add(action.Arguments); return action.Arguments.Value.ValueKind == JsonValueKind.Object; };
        PreparedToolAction? priorFinalAction = null; var priorPolicy = new Policy();
        var priorResult = await new ToolInvoker([priorAdapter], priorPolicy,
            [(_, action, _) => { var finalAction = action with { Arguments = commandData }; priorFinalAction = finalAction; return ValueTask.FromResult(finalAction); }])
            .ExecuteAsync(Invocation(JsonData.EmptyObject, name: "lookup"), default);
        Check(!priorResult.IsError, "An inert command-named JSON value was treated as executable argv.");
        Equal(1, priorAdapter.Prepares); Equal(2, priorSchemaInputs.Count); Equal(1, priorPolicy.Actions.Count); Equal(1, priorAdapter.Executed.Count);
        Check(ReferenceEquals(JsonData.EmptyObject, priorSchemaInputs[0]) && ReferenceEquals(commandData, priorSchemaInputs[1]), "Migrated transform lost initial/final schema input ownership.");
        var priorAuthorized = priorPolicy.Actions.Single();
        Check(ReferenceEquals(priorFinalAction, priorAuthorized) && ReferenceEquals(priorAuthorized, priorAdapter.Executed.Single()) &&
            ReferenceEquals(commandData, priorAuthorized.Arguments), "Migrated transform lost final-action identity at policy/execution.");
        Equal("lookup", priorAuthorized.ToolName); Equal("read", priorAuthorized.Operation); Equal(PreparedToolActionKind.Path, priorAuthorized.Kind);
        Equal("/synthetic/file", priorAuthorized.Target);
        Check(priorAuthorized.WorkingDirectory is null && priorAuthorized.CommandArguments.IsEmpty && priorAuthorized.Environment.IsEmpty,
            "Inert command data changed the original safe executable fields.");
        Equal(PriorTransformedArgumentVector, commandData.ToString()); Equal("a\0b", commandData.Value.GetProperty("command").GetString());

        var denied = new Adapter(PreparedToolActionKind.Extension); var denial = new Policy { Allow = false };
        Failure(await new ToolInvoker([denied], denial).ExecuteAsync(Invocation(JsonData.Parse(PriorArgumentVector)), default), ToolFailureKind.Blocked);
        Equal(1, denial.Actions.Count); Equal(0, denied.Executed.Count);
        var rejectedSchema = new Adapter(PreparedToolActionKind.Extension) { Validate = _ => false }; var unusedPolicy = new Policy();
        Failure(await new ToolInvoker([rejectedSchema], unusedPolicy).ExecuteAsync(Invocation(JsonData.Parse(PriorArgumentVector)), default), ToolFailureKind.InvalidArguments);
        Equal(0, unusedPolicy.Actions.Count); Equal(0, rejectedSchema.Executed.Count);
    }

    private static async Task ExecutableStringsRemainDenied()
    {
        Func<PreparedToolAction, PreparedToolAction>[] corrupt =
        [
            action => action with { ToolName = "da\0ta" },
            action => action with { Operation = "in\0voke" },
            action => action with { Target = "owned\0target" },
            action => action with { WorkingDirectory = "owned\0directory" },
            action => action with { CommandArguments = ["arg\0value"] },
            action => action with { Environment = ImmutableDictionary<string, string>.Empty.Add("KE\0Y", "value") },
            action => action with { Environment = ImmutableDictionary<string, string>.Empty.Add("KEY", "va\0lue") }
        ];
        foreach (var mutation in corrupt)
        foreach (var finalTransform in new[] { false, true })
        {
            var adapter = new Adapter(PreparedToolActionKind.Command);
            if (!finalTransform) adapter.PrepareMutation = mutation;
            var policy = new Policy();
            var result = await new ToolInvoker([adapter], policy,
                finalTransform ? [(_, action, _) => ValueTask.FromResult(mutation(action))] : null)
                .ExecuteAsync(Invocation(JsonData.Parse(PriorArgumentVector)), default);
            Failure(result, ToolFailureKind.InvalidArguments); Equal(1, adapter.Prepares); Equal(0, policy.Actions.Count); Equal(0, adapter.Executed.Count);
            Equal(finalTransform ? 1 : 0, adapter.Validations);
        }
        // Virtual Extension actions still reject executable fields instead of acquiring shell/path authority.
        foreach (var mutation in new Func<PreparedToolAction, PreparedToolAction>[]
        {
            action => action with { Target = "owner/1/\0data" },
            action => action with { WorkingDirectory = "cwd" },
            action => action with { CommandArguments = ["argument"] },
            action => action with { Environment = ImmutableDictionary<string, string>.Empty.Add("KEY", "value") }
        })
        {
            var adapter = new Adapter(PreparedToolActionKind.Extension) { PrepareMutation = mutation }; var policy = new Policy();
            Failure(await new ToolInvoker([adapter], policy).ExecuteAsync(Invocation(JsonData.Parse(PriorArgumentVector)), default), ToolFailureKind.InvalidArguments);
            Equal(0, policy.Actions.Count); Equal(0, adapter.Executed.Count);
        }
    }

    private static async Task AdmissionBounds()
    {
        var owned = JsonData.Parse(PriorArgumentVector); var original = owned.ToString();
        foreach (var (limit, accepted) in new[] { (original.Length, true), (original.Length - 1, false) })
        {
            var adapter = new Adapter(PreparedToolActionKind.Extension); var policy = new Policy();
            var result = await new ToolInvoker([adapter], policy, options: new(MaximumArgumentCharacters: limit)).ExecuteAsync(Invocation(owned), default);
            if (accepted) Check(!result.IsError, "Exact raw NUL argument budget was rejected."); else Failure(result, ToolFailureKind.InvalidArguments);
            Equal(accepted ? 1 : 0, adapter.Prepares); Equal(accepted ? 1 : 0, policy.Actions.Count); Equal(accepted ? 1 : 0, adapter.Executed.Count);
            Equal(original, owned.ToString());
        }
        var actionSize = "data".Length + "invoke".Length + "owner/1/data".Length + original.Length;
        foreach (var (limit, accepted) in new[] { (actionSize, true), (actionSize - 1, false) })
        {
            var adapter = new Adapter(PreparedToolActionKind.Extension); var policy = new Policy();
            var result = await new ToolInvoker([adapter], policy, options: new(MaximumActionCharacters: limit)).ExecuteAsync(Invocation(owned), default);
            if (accepted) Check(!result.IsError, "Exact action budget changed for inert NUL data."); else Failure(result, ToolFailureKind.InvalidArguments);
            Equal(accepted ? 1 : 0, policy.Actions.Count); Equal(accepted ? 1 : 0, adapter.Executed.Count);
        }
        var finalOverBudget = new Adapter(PreparedToolActionKind.Extension); var finalPolicy = new Policy();
        Failure(await new ToolInvoker([finalOverBudget], finalPolicy,
            [(_, action, _) => ValueTask.FromResult(action with { Arguments = Initial })], options: new(MaximumArgumentCharacters: original.Length))
            .ExecuteAsync(Invocation(owned), default), ToolFailureKind.InvalidArguments);
        Equal(1, finalOverBudget.Validations); Equal(0, finalPolicy.Actions.Count); Equal(0, finalOverBudget.Executed.Count);

        foreach (var raw in new[] { """{"text":"\u0000\ud800"}""", """{"text":"\udc00\u0000"}""", """{"nested":[{"text":"\u0000"}]}""" })
        {
            var adapter = new Adapter(PreparedToolActionKind.Extension); var policy = new Policy();
            Failure(await new ToolInvoker([adapter], policy, options: new(MaximumJsonDepth: 2)).ExecuteAsync(Invocation(JsonData.Parse(raw)), default), ToolFailureKind.InvalidArguments);
            Equal(0, adapter.Prepares); Equal(0, policy.Actions.Count); Equal(0, adapter.Executed.Count);
        }
        Throws<JsonException>(() => JsonData.Parse("""{"text":"\u0000","\u0074ext":"other"}"""));
        var malformedFinal = new Adapter(PreparedToolActionKind.Extension); var malformedPolicy = new Policy();
        Failure(await new ToolInvoker([malformedFinal], malformedPolicy,
            [(_, action, _) => ValueTask.FromResult(action with { Arguments = JsonData.Parse("""{"text":"\u0000\ud800"}""") })])
            .ExecuteAsync(Invocation(owned), default), ToolFailureKind.InvalidArguments);
        Equal(0, malformedPolicy.Actions.Count); Equal(0, malformedFinal.Executed.Count);
        var nonfiniteOutput = new Adapter(PreparedToolActionKind.Extension)
        { Execute = _ => ToolResultValueCodec.Read("""{"content":[],"details":{"number":1e400}}""") };
        Failure(await new ToolInvoker([nonfiniteOutput], new Policy()).ExecuteAsync(Invocation(owned), default), ToolFailureKind.ExecutionError);
        Equal(1, nonfiniteOutput.Executed.Count);

        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var unused = new Adapter(PreparedToolActionKind.Extension); var unusedPolicy = new Policy();
        Failure(await new ToolInvoker([unused], unusedPolicy).ExecuteAsync(Invocation(owned), canceled.Token), ToolFailureKind.Canceled);
        Equal(0, unused.Prepares); Equal(0, unusedPolicy.Actions.Count); Equal(0, unused.Executed.Count);
    }

    private static ToolInvocation Invocation(JsonData arguments, string name = "data")
    {
        var call = new ToolCallContent(name == "lookup" ? "call-lookup" : "data-call", name, arguments);
        var assistant = new AssistantMessage("openai-responses", "authored-provider", "data-model", 123, [call], TokenUsage.Zero, StopReason.ToolUse);
        return new(assistant, call, 0);
    }
    private sealed class Adapter(PreparedToolActionKind kind, bool priorShape = false) : IPreparedToolAdapter
    {
        public string Name => priorShape ? "lookup" : "data";
        internal int Prepares, Validations; internal PreparedToolAction? Prepared;
        internal readonly List<PreparedToolAction> Executed = [];
        internal Func<PreparedToolAction, PreparedToolAction>? PrepareMutation;
        internal Func<PreparedToolAction, bool>? Validate;
        internal Func<PreparedToolAction, ToolResult>? Execute;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Prepares++;
            var action = new PreparedToolAction(Name, priorShape ? "read" : "invoke", kind, priorShape ? "/synthetic/file" : "owner/1/data", invocation.Call.Arguments,
                kind == PreparedToolActionKind.Command ? ["safe-argument"] : [],
                kind == PreparedToolActionKind.Command ? "owned-directory" : null, ImmutableDictionary<string, string>.Empty);
            var finalAction = PrepareMutation is { } transform ? transform(action) : action;
            Prepared = finalAction;
            return ValueTask.FromResult(finalAction);
        }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Validations++; return ValueTask.FromResult(Validate?.Invoke(action) ?? true); }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Executed.Add(action); return ValueTask.FromResult(Execute?.Invoke(action) ?? ToolResult.Success("effect")); }
    }
    private sealed class Policy : IToolActionPolicy
    {
        internal bool Allow = true; internal readonly List<PreparedToolAction> Actions = [];
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Actions.Add(action); return ValueTask.FromResult(new ToolActionAuthorization(Allow)); }
    }
    private static void Failure(ToolResult result, ToolFailureKind kind)
    { Check(result.IsError && result.Failure?.Kind == kind, "Expected " + kind + "; actual " + result.Failure?.Kind); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
