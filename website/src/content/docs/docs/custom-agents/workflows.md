---
title: Workflows
description: "Typed workflows: a series of actions over one shared data class, run on the durable harness. Experimental and unreleased."
sidebar:
  badge:
    text: Unreleased
    variant: caution
---

:::caution[Experimental and unreleased]
The custom agents library is on PiSharp's `main` branch but not in any release or NuGet package yet. To use it, build PiSharp from
source and reference the projects directly (see [Build from source](/docs/contribute/build/)). Its API is marked
`[Experimental("PISHARP_AGENTS001")]` and builds on durable APIs marked `[Experimental("PISHARP_DURABLE001")]`, so suppress `PISHARP_AGENTS001` and `PISHARP_DURABLE001` in your project to compile it, and it may change or be removed without notice.
:::

A workflow is a named series of actions over one data class. Every action receives the same data object, reads it and changes it. After each action the engine commits the data and the next action to storage in one step, so a crash loses at most the action that was running. The types are in `PiSharp.CustomAgents.Workflows` and, for hosting, `PiSharp.CustomAgents.Workflows.Hosting`.

See [Custom agents](/docs/custom-agents/overview/) for setup, the replay guarantees and the data limits.

## Build a workflow

Start with `Workflow.For<TData>(name)`, add steps in order, and call `Build()`. `TData` is any class that `System.Text.Json` can round-trip. Steps run in the order you add them unless an action jumps elsewhere.

```csharp
using System.ComponentModel;
using PiSharp.CustomAgents.Workflows;

var orders = Workflow.For<Order>("orders")
    .Describe("Loads an order, assesses its risk and writes the customer a note.")
    .Then(new LoadOrder())
    .Prompt<RiskVerdict>("risk",
        order => $"Assess the fraud risk of order {order.Id}: {order.Total} EUR from {order.Customer}.",
        (order, verdict) => order.Risk = verdict)
    .Then("hold", (order, context, token) =>
        Task.FromResult(order.Risk?.Level == "high" ? ActionResult.Stop() : ActionResult.Next()))
    .Branch(order => order.Vip, "vip-note", "thank-you")
    .Prompt("vip-note", order => $"Write a two-sentence note to our VIP customer {order.Customer}.",
        (order, text) => order.Note = text, action: new ActionOptions { Next = Workflow.End })
    .Prompt("thank-you", order => $"Write a one-line thank-you to {order.Customer}.",
        (order, text) => order.Note = text)
    .Build();

public sealed class Order
{
    public string Id { get; set; } = "";
    public string Customer { get; set; } = "";
    public double Total { get; set; }
    public bool Vip { get; set; }
    public RiskVerdict? Risk { get; set; }
    public string? ChargeId { get; set; }
    public string? Note { get; set; }
}

public sealed class RiskVerdict
{
    [Description("The risk level: low, medium or high")]
    public string Level { get; set; } = "";
    [Description("One short sentence on why")]
    public string Reason { get; set; } = "";
}

public interface IOrderStore
{
    Task<Order?> FindAsync(string id, CancellationToken cancellationToken);
}

internal sealed class LoadOrder : IAction<Order>
{
    public string Name => "load";

    public async Task<ActionResult> ExecuteAsync(Order order, ActionContext context, CancellationToken cancellationToken)
    {
        var store = context.GetRequiredService<IOrderStore>();
        if (await store.FindAsync(order.Id, cancellationToken) is not { } found) return ActionResult.Fail($"There is no order {order.Id}.");
        order.Customer = found.Customer;
        order.Total = found.Total;
        order.Vip = found.Vip;
        await context.LogAsync($"loaded order {order.Id}", cancellationToken);
        return ActionResult.Next();
    }
}
```

Workflow names and action names follow `^[A-Za-z][A-Za-z0-9_.-]{0,63}$`, and an invalid name throws `ArgumentException`. Action names must be unique in the workflow. `Build()` throws `WorkflowDefinitionException` when the workflow has no steps, two steps share a name, a `Next` or branch target names no step, `MaxVisits` is below 1, a timeout is not positive, or a retry policy allows fewer than 1 attempt.

### Builder methods

Each method returns the builder.

| Method | What it does |
| --- | --- |
| `Version(int version, WorkflowMigration? migration = null)` | Sets the definition version (default 1, at least 1). See [Versioning](#versioning). |
| `Describe(string description)` | Describes the workflow, for listings and the [workflow tool](#workflows-as-tools). |
| `WithModel(ModelRef model)` | The model for prompts and agent steps that name none. |
| `WithThinking(string level)` | The thinking level for prompts that name none: `off`, `minimal`, `low`, `medium`, `high` or `xhigh`. |
| `WithInstructions(string instructions)` | Instructions rendered into every prompt of the workflow. |
| `WithJsonOptions(JsonSerializerOptions options)` | The `System.Text.Json` options for the data class. The builder copies and freezes them. Default: `JsonSerializerDefaults.Web`. |
| `WithLimits(int maxSteps = 1000, int maxDataBytes = 256 * 1024)` | The run limits. `maxSteps` is at least 1 and `maxDataBytes` at least 64. |
| `OnAbort(Func<TData, ActionContext, CancellationToken, Task> handler)` | Runs when the run is aborted, with the last committed data. It runs at least once. Its data changes are kept in the aborted result, and an exception it throws is reported, not fatal. |
| `Then(IAction<TData> action, ActionOptions? options = null)` | Adds an action class. |
| `Then(string name, Func<TData, ActionContext, CancellationToken, Task<ActionResult>> run, ActionOptions? options = null)` | Adds a delegate that decides what happens next. |
| `Then(string name, Func<TData, ActionContext, CancellationToken, Task> run, ActionOptions? options = null)` | Adds an async delegate. The run continues with the next action. |
| `Then(string name, Action<TData> run, ActionOptions? options = null)` | Adds a synchronous delegate that changes the data. |
| `Branch(Func<TData, bool> condition, string ifTrue, string ifFalse, string? name = null)` | Routes to one of two steps. |
| `Switch(Func<TData, string> select, string? name = null)` | Routes to the step the function names, or `Workflow.End`. |
| `Prompt(string name, Func<TData, string> prompt, Action<TData, string> apply, PromptOptions? options = null, ActionOptions? action = null)` | Asks the model and applies the answer text. |
| `Prompt<TResult>(string name, Func<TData, string> prompt, Action<TData, TResult> apply, PromptOptions? options = null, ActionOptions? action = null)` | Asks the model for a typed result. See [Typed prompts](#typed-prompts). |
| `DeclareResult<TResult>()` | Declares a result type for `PromptAsync<T>` calls in code actions. |
| `Agent(string name, string agent, Func<TData, string> input, Action<TData, ModelAnswer> apply, AgentRunOptions? options = null, ActionOptions? action = null)` | Runs a team agent and applies its answer. See [Agent steps](#agent-steps). |
| `Run`, `Parallel`, `ForEach`, `WaitForSignal` | Composite steps. See [Composition](#composition). |
| `Script(...)`, `CSharp(...)` | Script steps, from the JavaScript and Scripting projects. See [Script steps](#script-steps). |
| `Build()` | Validates the steps and returns the `Workflow<TData>`. |

The built `Workflow<TData>` exposes `Name`, `Version`, `Description`, `Steps` (the step names in order), `DataType`, `Fingerprint`, `TaskKind`, `JsonOptions`, `Limits`, `Model`, `Thinking` and `Instructions`. `Fingerprint` is 16 hex characters of a SHA-256 over the name, version, data type and step shape. `TaskKind` is the durable task kind the workflow compiles to, `pisharp.workflow.<name>`, which `Workflow.TaskKind(name)` also returns.

## Actions

An action is anything that implements `IAction<TData>`:

```csharp
public interface IAction<TData> where TData : class
{
    string Name { get; }
    Task<ActionResult> ExecuteAsync(TData data, ActionContext context, CancellationToken cancellationToken);
}
```

The `Then` overloads wrap a delegate in a `DelegateAction<TData>(name, run)`. Use a class when the action has dependencies or is reused, and a delegate for short steps.

An action changes the data in place. When it returns, the engine serializes the data, checks it against the [limits](#limits-and-errors), and commits it with the next action. An exception from the action is a failed attempt: its data changes are discarded, and the [retry policy](#retries-and-timeouts) decides what happens.

### What happens next

The `ActionResult` an action returns decides where the run goes:

| Factory | Kind | Effect |
| --- | --- | --- |
| `ActionResult.Next()` | `Next` | Continues with the action's `ActionOptions.Next`, else the following step, else the end. |
| `ActionResult.GoTo(string action)` | `GoTo` | Continues with the named action or router, or `Workflow.End`. |
| `ActionResult.Stop()` | `Stop` | Completes the run successfully here. |
| `ActionResult.Fail(string reason)` | `Fail` | Fails the run with `reason`, with no retry. The error kind is `failed`. |
| `ActionResult.Repeat(TimeSpan after)` | `Repeat` | Runs the same action again after the delay, as a new visit. Use it to poll. |
| `ActionResult.Sleep(TimeSpan delay, string? then = null)` | `Sleep` | Waits durably, then continues with `then`, or the next step when it is null. |

Delays can't be negative. A sleep is a durable timer, so a restart during it keeps waiting for the time that is left. It holds whatever comes next, including a composite step or the end of the run. The result's `Kind`, `Target`, `Reason` and `Delay` properties hold the decision.

The next snippets use a payment service of your own, registered in the catalog's services:

```csharp
public interface IPayments
{
    Task<string> ChargeAsync(string orderId, double amount, string idempotencyKey, CancellationToken cancellationToken);
    Task<bool> IsSettledAsync(string chargeId, CancellationToken cancellationToken);
}
```

This action polls until the payment settles, and gives up after 12 visits:

```csharp
.Then("await-payment", async (order, context, token) =>
{
    var payments = context.GetRequiredService<IPayments>();
    if (await payments.IsSettledAsync(order.ChargeId!, token)) return ActionResult.Next();
    return ActionResult.Repeat(TimeSpan.FromMinutes(5));
}, new ActionOptions { MaxVisits = 12 })
```

### Action options

| `ActionOptions` property | Default | Meaning |
| --- | --- | --- |
| `Next` | the following step | The step `ActionResult.Next()` continues with. `Build()` checks that it exists. |
| `Retry` | `RetryPolicy.None` | How exceptions are retried. |
| `Timeout` | none | The longest one attempt may take. Past it the attempt is cancelled and fails with a `TimeoutException`, which the retry policy can retry. |
| `MaxVisits` | unlimited | How many times the run may enter this action. Past it the run fails with error kind `limit`. |

### Retries and timeouts

A `RetryPolicy` retries an action that throws. Each retry is a new attempt of the same visit. The failed attempt's data changes are discarded, but results recorded with `OnceAsync` are kept.

| Member | Default | Meaning |
| --- | --- | --- |
| `RetryPolicy.None` | | One attempt, no retries. |
| `RetryPolicy.Exponential(int attempts, TimeSpan? delay = null, TimeSpan? maxDelay = null)` | delay 1 s, max 5 min | Doubles the wait after each failure (`Backoff = 2`). |
| `RetryPolicy.Fixed(int attempts, TimeSpan? delay = null)` | delay zero | The same wait between attempts. |
| `MaxAttempts` | 1 | Total attempts, the first included. |
| `Delay` | zero | The wait before the second attempt. |
| `Backoff` | 1 | The factor applied to the wait after each further failure. |
| `MaxDelay` | 5 min | The longest wait. |
| `RetryOn` | null | Which exceptions are retried. Null retries every exception except cancellation. |

`RetryPolicy` is a record, so you can adjust a factory's result with `with`. Data errors (`WorkflowDataException`) are never retried. When the last attempt fails, the run fails with error kind `action_failed` and the message `Action '<name>' failed after N attempts: ...`.

```csharp
.Then("charge", async (order, context, token) =>
{
    var payments = context.GetRequiredService<IPayments>();
    // Recorded once per visit: a retry or a rerun after a crash reuses the charge ID.
    order.ChargeId = await context.OnceAsync("charge",
        ct => payments.ChargeAsync(order.Id, order.Total, context.IdempotencyKey("charge"), ct), token);
}, new ActionOptions
{
    Retry = RetryPolicy.Exponential(4) with { RetryOn = error => error is HttpRequestException or TimeoutException },
    Timeout = TimeSpan.FromSeconds(30),
})
```

## The action context

`ActionContext` describes the current attempt and gives an action its tools for side effects, model calls and logging.

| Member | Meaning |
| --- | --- |
| `Workflow` | The workflow's name. |
| `Action` | This action's name. |
| `TaskId`, `ConversationId` | The run's durable task and conversation. |
| `Step` | The step number of this visit. It stays the same across the visit's retries and changes on every new visit, loops included. |
| `Attempt` | The 1-based attempt of this visit. |
| `Visit` | How many times the run has entered this action, this visit included. |
| `IsReplay` | True when an earlier attempt at this exact point started and didn't commit, after a crash or restart. Side effects it made may already exist. |
| `Now` | The harness clock. |
| `Runtime`, `Durable` | The durable `ITaskRuntime` and `DurableContext`, as an escape hatch to memos, documents and child tasks. |
| `IdempotencyKey(string key)` | A key that is the same on every attempt and replay of this visit: `wf:<task>:<step>:<key>`. Pass it to external systems. |
| `OnceAsync<T>(string key, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)` | Runs `work` unless this visit already recorded a result for `key`, then returns the recorded result. Work that crashed before recording runs again, so it is at least once. Keep results small: they are stored with the run. |
| `OnceAsync(string key, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)` | The same, for work without a result. |
| `GetService<T>()` | A service from the catalog's `IServiceProvider`, or null. |
| `GetRequiredService<T>()` | The same, but throws `InvalidOperationException` when the service is missing. |
| `LogAsync(string message, CancellationToken cancellationToken = default)` | Appends a line to the run's log. The newest 200 lines are kept. |
| `PromptAsync`, `PromptAnswerAsync`, `PromptAsync<T>` | Model calls. See [Prompts](#prompts). |
| `RunAgentAsync(string agent, string input, AgentRunOptions? options = null, CancellationToken cancellationToken = default)` | Runs a team agent. See [Agent steps](#agent-steps). |
| `Messages` | An `IAgentMessenger` at the address `workflow.<task id>`, to message the team's agents. It needs a team. See [Messaging](/docs/custom-agents/teams/#messaging). |

Services come from `WorkflowCatalogOptions.Services`, or `AgentTeamOptions.Services` in a team. Pass any `IServiceProvider`, for example one built with `Microsoft.Extensions.DependencyInjection`.

## Prompts

A prompt sends text to a model and returns its answer. Each call runs in a child conversation owned by the run's task, one per visit and call key. Within one attempt, a rerun after a crash returns the recorded answer instead of asking again. A new attempt after a failure asks again.

From a code action:

- `PromptAsync(string prompt, PromptOptions? options = null, CancellationToken cancellationToken = default)` returns the answer text.
- `PromptAnswerAsync(...)` returns a `ModelAnswer(string Text, JsonObjectValue Message, ConversationId ConversationId)`: the text, the assistant message and the conversation that answered.
- `PromptAsync<T>(...)` returns a typed result (see below).

The `Prompt` builder step does the same for you with key `prompt`.

| `PromptOptions` property | Default | Meaning |
| --- | --- | --- |
| `Model` | the workflow's, else the catalog's `DefaultModel` | The model. |
| `Thinking` | the workflow's, else the catalog's `DefaultThinking`, else `off` | The thinking level. |
| `Instructions` | none | Instructions for this call, rendered after the workflow's. |
| `Timeout` | none | How long the model may take. On expiry the call is aborted and the attempt fails with a `TimeoutException`. |
| `MaxRepairs` | 2 | For typed calls: how many repair rounds follow an answer that doesn't fit the type. |
| `Key` | `prompt`, `prompt2`, ... in call order | Distinguishes calls within one action visit. Set it when the number or order of calls can change between attempts. |

With no model anywhere, the call throws `ModelCallException` with reason `no_model`: "No model is configured for the prompt; set PromptOptions.Model, Workflow.WithModel or the catalog's DefaultModel." `ModelCallException` and `ModelAnswer` live in `PiSharp.CustomAgents.Workflows.Engine`.

The model's request ID is deterministic, `wf:<task>:<step>:<key>:<attempt>`, with `:r<n>` for the nth repair round.

### Typed prompts

`Prompt<TResult>` and `PromptAsync<T>` ask for structured output. The engine gives the call a `submit_result` tool whose parameters are `T`'s JSON schema, and appends this to the instructions: "When you have the answer, call the submit_result tool with it. Do not answer in plain text." `[Description]` attributes on `T`'s properties become schema descriptions.

- The engine reads the arguments of the last `submit_result` call. If there is none, it reads JSON from the answer text, fenced or bare.
- A type that isn't a JSON object, such as `string` or `int[]`, is wrapped as `{ "value": ... }` in the schema. A recursive type is refused.
- An answer that doesn't fit gets a repair prompt: "Your answer could not be used: {error}" followed by "Call the submit_result tool with arguments that match its schema."
- After `MaxRepairs + 1` answers that don't fit, the call throws `ModelCallException`: "The model did not return a valid {T} for '{key}' after N answers: ...".

A `Prompt<T>` step declares `T` for you. A code action that calls `PromptAsync<T>` needs `DeclareResult<T>()` on the builder, so the result tool is installed before a restart resumes the call. Without it the call throws `InvalidOperationException`.

```csharp
var triage = Workflow.For<Ticket>("triage")
    .DeclareResult<Verdict>()
    .Then("classify", async (ticket, context, token) =>
    {
        var verdict = await context.PromptAsync<Verdict>($"Classify this support ticket: {ticket.Text}",
            new PromptOptions { Thinking = "low", MaxRepairs = 1 }, token);
        ticket.Urgent = verdict.Urgent;
        ticket.Category = verdict.Category;
    })
    .Build();

public sealed class Ticket
{
    public string Text { get; set; } = "";
    public bool Urgent { get; set; }
    public string? Category { get; set; }
    public string? Triage { get; set; }
    public string? Escalation { get; set; }
}

public sealed record Verdict(
    [property: Description("billing, shipping or product")] string Category,
    [property: Description("Whether a person must answer today")] bool Urgent);
```

## Agent steps

An agent step runs a team agent, by its definition name, on an input made from the data. It needs a team: the workflow must be added to `AgentTeam.Workflows`, whose agent provider is the team's catalog. See [Agent teams](/docs/custom-agents/teams/).

```csharp
var support = Workflow.For<Ticket>("support")
    .Agent("triage", "triager", ticket => $"Triage this ticket: {ticket.Text}",
        (ticket, answer) => ticket.Triage = answer.Text,
        new AgentRunOptions { Timeout = TimeSpan.FromMinutes(5) })
    .Then("escalate", async (ticket, context, token) =>
    {
        if (ticket.Triage?.Contains("urgent", StringComparison.OrdinalIgnoreCase) != true) return;
        var answer = await context.RunAgentAsync("escalation", ticket.Text, cancellationToken: token);
        ticket.Escalation = answer.Text;
    })
    .Build();
```

The agent gets its definition's model, instructions and tools. It runs in a child conversation of the run, listed in the team's directory as kind `Workflow` at `<definition>#<n>` while the call runs. Like a prompt, the call is asked once per attempt.

| `AgentRunOptions` property | Default | Meaning |
| --- | --- | --- |
| `Timeout` | none | How long the agent may take. |
| `Key` | `agent`, `agent2`, ... in call order | Distinguishes calls within one action visit. |

`RunAgentAsync` throws `InvalidOperationException` when the catalog has no agent provider, and `ModelCallException` with reason `unknown_agent` when the name isn't registered.

## Branching

`Branch` and `Switch` add routers: steps that pick the next step from the data and do nothing else. Routers aren't checkpointed and don't count as phases, so they must be pure functions of the data. A router is named `#branch<n>` or `#switch<n>` unless you name it, and an action can `GoTo` a named router. A run may pass through at most 64 routers in a row without reaching an action, and fails with error kind `routing` past that, or when a router names a step that doesn't exist.

```csharp
.Switch(ticket => ticket.Category switch
{
    "billing" => "billing-reply",
    "shipping" => "shipping-reply",
    _ => Workflow.End,
}, name: "route")
```

## Composition

Composite steps run other workflows as durable child tasks and merge their results into the data. Add every workflow a step runs to the same catalog; `WorkflowCatalog.Add` adds the sub-workflows of a workflow for you.

| Method | What it does |
| --- | --- |
| `Run<TChild>(string name, Workflow<TChild> child, Func<TData, TChild> input, Action<TData, WorkflowResult<TChild>> output, bool failOnError = true)` | Runs a sub-workflow with the input made from the data, then applies its result. A child that doesn't complete fails this run with error kind `child_failed`, unless `failOnError` is false. Then `output` sees the failure. |
| `Parallel(string name, IEnumerable<ParallelBranch<TData>> branches, Action<TData, IReadOnlyList<BranchResult<TData>>> merge, JoinPolicy policy = JoinPolicy.FailFast)` | Runs branches at once, each on a copy of the data, then merges their results. |
| `ForEach<TItem, TChild>(string name, Func<TData, IEnumerable<TItem>> items, Workflow<TChild> body, Func<TData, TItem, int, TChild> input, Action<TData, IReadOnlyList<ItemResult<TItem, TChild>>> merge, ForEachOptions? options = null)` | Runs `body` once per item, a batch at a time, then merges the results in item order. An empty list merges an empty list. `items` must be a pure function of the data. |
| `WaitForSignal<TSignal>(string name, string signal, Action<TData, TSignal> apply, TimeSpan? timeout = null, string? onTimeout = null)` | Waits for a named signal and applies its value. |

Branches are built with `ParallelBranch<TData>.Of(name, IAction<TData>, ActionOptions?)`, `Of(name, Func<TData, ActionContext, CancellationToken, Task>, ActionOptions?)` or `Of(name, Workflow<TData>)`. Branch names follow the workflow name pattern. A single-action branch compiles to its own workflow named `<parent>.<step>.<branch>`. `BranchResult<TData>(string Branch, WorkflowResult<TData> Result)` and `ItemResult<TItem, TChild>(TItem Item, int Index, WorkflowResult<TChild> Result)` carry each result.

`JoinPolicy` comes from `PiSharp.Durable`. With `FailFast`, the first failure aborts the other children and fails the run with error kind `child_failed`, and `merge` doesn't run. With `AllSettled`, every result reaches `merge`, failures included. A `merge` that throws fails the run with error kind `merge_failed`, and an `input` function that throws fails it with `input_failed`.

| `ForEachOptions` property | Default | Meaning |
| --- | --- | --- |
| `Concurrency` | 4 | The most items running at once. Items start in batches of this size. At least 1. |
| `JoinPolicy` | `FailFast` | As for `Parallel`. |

`WaitForSignal` waits until the host sends the signal with `WorkflowRun<TData>.SignalAsync`. A signal sent before the run reaches the step is kept until it does, and signals are taken in the order sent. The timeout must be positive. Its deadline is stored, so a restart doesn't extend it. On timeout the run continues at `onTimeout`, or fails with error kind `timeout` when there is none. An `apply` that throws fails the run with error kind `signal_failed`.

```csharp
var check = Workflow.For<StockCheck>("stock-check")
    .Then("count", item => item.InStock = item.Sku != "MS-02")
    .Build();

var fulfil = Workflow.For<Shipment>("fulfil")
    .ForEach("stock", shipment => shipment.Skus, check,
        (shipment, sku, index) => new StockCheck { Sku = sku },
        (shipment, results) => shipment.Missing = [.. results.Where(item => item.Result.Data?.InStock != true).Select(item => item.Item)],
        new ForEachOptions { Concurrency = 2 })
    .Parallel("quotes",
        [
            ParallelBranch<Shipment>.Of("post", (shipment, _, _) => { shipment.PostQuote = 6.5; return Task.CompletedTask; }),
            ParallelBranch<Shipment>.Of("courier", (shipment, _, _) => { shipment.CourierQuote = 14; return Task.CompletedTask; }),
        ],
        (shipment, results) =>
        {
            shipment.PostQuote = results.Single(branch => branch.Branch == "post").Result.Data?.PostQuote;
            shipment.CourierQuote = results.Single(branch => branch.Branch == "courier").Result.Data?.CourierQuote;
        })
    .WaitForSignal<bool>("approval", "approved", (shipment, approved) => shipment.Approved = approved,
        timeout: TimeSpan.FromDays(2), onTimeout: "expired")
    .Branch(shipment => shipment.Approved, "ship", "expired")
    .Then("ship", shipment => shipment.Status = "shipped", new ActionOptions { Next = Workflow.End })
    .Then("expired", shipment => shipment.Status = "expired")
    .Build();

public sealed class StockCheck
{
    public string Sku { get; set; } = "";
    public bool InStock { get; set; }
}

public sealed class Shipment
{
    public string Id { get; set; } = "";
    public List<string> Skus { get; set; } = [];
    public List<string> Missing { get; set; } = [];
    public double? PostQuote { get; set; }
    public double? CourierQuote { get; set; }
    public bool Approved { get; set; }
    public string? Status { get; set; }
}
```

The host sends the signal to the run:

```csharp
team.Workflows.Add(fulfil);
await using var session = await team.OpenAsync(storage);
var run = await session.Workflows.StartAsync(fulfil, new Shipment { Id = "s-1", Skus = ["KB-01", "MS-02"] },
    new WorkflowStartOptions { RunKey = "s-1" });
await run.SignalAsync("approved", true, id: "approval-s-1");
var result = await run.WaitAsync();
Console.WriteLine($"{result.Status}: {result.Data?.Status}");
```

## Script steps

Two extension methods add steps written in a script language. Both compile or check the script when the workflow is built.

`Script(string name, string code, JsRuntime? runtime = null, ActionOptions? options = null)` comes from `PiSharp.CustomAgents.JavaScript` (class `JsWorkflowExtensions`). The code is an async function body that reads `data`, the data as JSON, and `ctx`, an object with `workflow`, `action`, `step`, `attempt`, `visit` and `isReplay`. It returns an object with these optional fields:

| Field | Meaning |
| --- | --- |
| `updates` | A JSON merge patch (RFC 7386) applied to the data. Use the data's JSON names, which are camelCase by default. |
| `next` | The step to continue with. |
| `stop` | `true` completes the run. |
| `fail` | A reason that fails the run. |
| `output` | A line added to the run's log. |

`fail` takes precedence over `stop`, and `stop` over `next`. An unknown field, a script that throws, or a return value that isn't an object fails the attempt with a `JsActionException`. A syntax error throws `WorkflowDefinitionException` when you call `Script`. The script runs on `JsRuntime.Shared` unless you pass a runtime. See [JavaScript tools](/docs/custom-agents/tools/#javascript-tools) for the sandbox and its limits.

`CSharp(string name, string code, CSharpScriptHost? host = null, ActionOptions? options = null)` comes from `PiSharp.CustomAgents.Scripting` (class `ScriptWorkflowExtensions`). The script sees `Data` (the typed data, changed in place), `Context` (the `ActionContext`), `CancellationToken` and `Messages`. It may return an `ActionResult`, and a script that ends without one continues with the next step. The data type must be public, because the script is compiled into its own assembly. A compile error throws `ScriptCompilationException` when you call `CSharp`. See [C# scripts](/docs/custom-agents/tools/#c-scripts) for the host options.

```csharp
using PiSharp.CustomAgents.JavaScript;
using PiSharp.CustomAgents.Scripting;

var pricing = Workflow.For<Basket>("pricing")
    .Script("discount", """
        const percent = data.vip ? 15 : (data.total >= 100 ? 5 : 0);
        return { updates: { discountPercent: percent }, output: `discount ${percent}%` };
        """)
    .CSharp("payable", """
        Data.Payable = Math.Round(Data.Total * (100 - Data.DiscountPercent) / 100, 2);
        if (Data.Payable == 0) return ActionResult.Stop();
        """)
    .Build();

public sealed class Basket
{
    public double Total { get; set; }
    public bool Vip { get; set; }
    public int DiscountPercent { get; set; }
    public double Payable { get; set; }
}
```

## Versioning

A run stores its workflow version with its data and position. When you change a workflow in a way that live runs can't follow, such as renaming or removing an action or changing the data's shape, bump the version and supply a migration. The durable harness applies the migration when it picks up a run stored by an older version.

| `WorkflowMigration` factory | What it does |
| --- | --- |
| `RenameActions(params (string From, string To)[] renames)` | A run stopped at an old action name continues at the new one. |
| `TransformData(Func<JsonNode?, int, JsonNode?> transform)` | Rewrites the stored data JSON. The `int` is the version the run was stored with. The JSON uses the data's JSON names (camelCase by default). |
| `Custom(Func<StoredRun, int, StoredRun> migrate)` | Rewrites a `StoredRun(string Action, JsonNode? Data)`: the action the run will run next and its data. |
| `Compose(params WorkflowMigration[] migrations)` | Applies migrations in order. |

```csharp
using System.Text.Json.Nodes;

var ordersV2 = Workflow.For<Order>("orders")
    .Version(2, WorkflowMigration.Compose(
        WorkflowMigration.RenameActions(("thank-you", "thanks")),
        WorkflowMigration.TransformData((data, fromVersion) =>
        {
            if (data is JsonObject fields && fields["note"] is null) fields["note"] = "";
            return data;
        })))
    .Then(new LoadOrder())
    .Prompt("thanks", order => $"Write a one-line thank-you to {order.Customer}.", (order, text) => order.Note = text)
    .Build();
```

What happens to a stored run:

- **Stored by an older version, with a migration:** the migration runs, then the run continues.
- **Stored by an older version, without a migration:** the run is blocked as `MigrationFailed`, even if the steps didn't change. Bump the version only with a migration.
- **Stored by a newer version than the code knows:** the run is blocked as `TaskTooOld`.
- **Same version, but the stored action no longer exists:** the run fails with error kind `missing_action` and a message that says to bump the version and add a migration. The shape change is also reported through the harness's `OnReport`.

## Hosting

A workflow runs on a durable harness. The usual host is an [agent team](/docs/custom-agents/teams/): add the workflow to `team.Workflows` before `OpenAsync`, then use `session.Workflows`, a `WorkflowRunner`.

```csharp
team.Workflows.Add(orders);
await using var session = await team.OpenAsync(storage);

var run = await session.Workflows.StartAsync(orders, new Order { Id = "42" }, new WorkflowStartOptions { RunKey = "order-42" });
Console.WriteLine(run.Existing ? $"attached to run {run.TaskId.Value}" : $"started run {run.TaskId.Value}");

var result = await run.WaitAsync();
var state = await run.StatusAsync();
foreach (var line in state.History)
    Console.WriteLine($"{line.Action} (attempt {line.Attempt}): {line.Outcome}{(line.Next is null ? "" : " -> " + line.Next)}");
if (!result.Completed) Console.WriteLine($"{result.ErrorKind} at {result.StoppedAt}: {result.Error}");
```

Add every workflow before the harness resumes, so runs left from an earlier process find their definition. Starting a workflow that isn't in the catalog throws `InvalidOperationException`: "Workflow '{name}' is not in the catalog; add it before running it."

### The catalog

`WorkflowCatalog(IRegistry registry, WorkflowCatalogOptions? options = null)` holds the workflows a harness can run and installs them into the durable registry.

| Member | Meaning |
| --- | --- |
| `Add(WorkflowDefinition workflow)` | Adds or replaces a workflow and the sub-workflows it runs. Returns the catalog. |
| `Remove(string name)` | Removes a workflow. Its live runs block as missing until it is added again. |
| `Find(string name)` | The workflow with that name, or null. |
| `Workflows` | The added workflows, by name. |
| `Agents` | The `IAgentProvider` that agent steps resolve agents through. A team sets it to its `AgentCatalog`. |
| `Registry`, `Options` | What the catalog was created with. |

| `WorkflowCatalogOptions` property | Meaning |
| --- | --- |
| `Services` | The `IServiceProvider` behind `ActionContext.GetService`. |
| `DefaultModel` | The model of prompts when neither the call nor the workflow names one. |
| `DefaultThinking` | The thinking level of prompts when neither the call nor the workflow sets one. |
| `TimeProvider` | The clock. |

### Starting and following runs

`WorkflowRunner(IHarness harness, WorkflowCatalog catalog)` starts and attaches runs:

| Method | What it does |
| --- | --- |
| `StartAsync<TData>(Workflow<TData> workflow, TData data, WorkflowStartOptions? options = null, DurableContext? context = null)` | Starts a run and returns a `WorkflowRun<TData>`. With a run key that is already known, it returns that run with `Existing` set. It also resumes the harness. |
| `RunAsync<TData>(...)` | Starts a run and waits for its `WorkflowResult<TData>`. |
| `Attach<TData>(Workflow<TData> workflow, TaskId taskId, ConversationId conversationId)` | A handle to a run you know the IDs of. |
| `FindAsync<TData>(Workflow<TData> workflow, string runKey, DurableContext? context = null)` | The run started with that key, or null. |
| `ListAsync(DurableContext? context = null)` | The runs started on this storage as `WorkflowRunInfo(Workflow, TaskId, ConversationId, RunKey, CreatedAt)`, oldest first. Only the newest 1000 are listed. |

| `WorkflowStartOptions` property | Meaning |
| --- | --- |
| `RunKey` | Makes the start idempotent. A second start with the same key, in this process or a later one, returns the first run. Keys are scoped to the workflow name. |
| `Conversation` | The conversation the run belongs to. Default: a new ownerless conversation. It must exist. |
| `Background` | Starts the run as a durable background task. |

A `WorkflowRun<TData>` has `Workflow`, `TaskId`, `ConversationId` and `Existing`, and these methods:

| Method | What it does |
| --- | --- |
| `WaitAsync(DurableContext? context = null)` | Waits for the run to end and returns its result. |
| `SignalAsync<T>(string signal, T value, string? id = null, DurableContext? context = null)` | Sends a signal. With an `id` the send is idempotent: a second send with the same ID returns false. At most 100 signals can wait untaken; one more throws `InvalidOperationException`. |
| `StatusAsync(DurableContext? context = null)` | The run's `WorkflowRunState`. |
| `AbortAsync(DurableContext? context = null)` | Asks the run to abort, runs its `OnAbort` handler, and returns at once. `WaitAsync` sees the end. |

`WorkflowRunState(Status, CurrentAction, Step, History, Log, Error, Task)` holds the newest 100 history entries and the newest 200 log lines. Each `WorkflowHistoryEntry(Seq, Action, Attempt, Outcome, Next, Error, At)` has an outcome of `next`, `goto`, `repeat`, `sleep`, `signal`, `timeout`, `stop`, `end`, `fail` or `retry`.

### Results

`WorkflowResult<TData>` describes a finished run:

| Member | Meaning |
| --- | --- |
| `Status` | A `WorkflowRunStatus`: `Pending`, `Running`, `Completed`, `Failed`, `Aborted` or `Faulted`. |
| `Data` | The final data, also for failed and aborted runs. Null when it couldn't be read. |
| `Error` | Why the run didn't complete. |
| `ErrorKind` | The failure kind (see below). |
| `StoppedAt` | The action the run ended at. |
| `Steps` | How many actions the run entered. |
| `TaskId` | The run's task. |
| `Completed` | Whether `Status` is `Completed`. |
| `EnsureCompleted()` | Returns the data of a completed run, else throws `WorkflowFailedException`, whose `Status` holds the status. |

`ToString()` returns the status, followed by `: ` and the error when there is one.

| Error kind | Cause |
| --- | --- |
| `failed` | An action returned `ActionResult.Fail`. |
| `action_failed` | An action threw and its retries ran out. |
| `limit` | The run passed `MaxSteps`, or an action passed its `MaxVisits`. |
| `data` | The data couldn't be serialized or read, was too large, or held a number a double can't store exactly. |
| `routing` | A jump named an unknown step, or the run passed through more than 64 routers in a row. |
| `missing_action` | The stored action is not in the workflow at the same version. |
| `child_failed` | A `Run` child, a `Parallel` branch or a `ForEach` item didn't complete. |
| `merge_failed`, `input_failed` | A composite step's `merge` or `input` function threw. |
| `signal_failed` | A `WaitForSignal` step's `apply` threw. |
| `timeout` | A `WaitForSignal` step timed out without an `onTimeout` step. |
| `aborted` | The run was aborted. |
| `faulted`, `orphaned`, `unknown` | The durable task ended without running the workflow's own outcome. The status is `Faulted`. |

### Hosting without a team

You don't need a team to run workflows. Create a catalog over a durable registry, open a harness with it, and use a `WorkflowRunner`:

```csharp
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;

using var models = new NativeDurableModels(new NativeDurableModelsOptions { Environment = Environment.GetEnvironmentVariable });
var registry = Registry.Create();
var catalog = new WorkflowCatalog(registry, new WorkflowCatalogOptions { DefaultModel = new ModelRef("xai", "grok-4.7") });
catalog.Add(orders);

var storage = await SqliteStorage.OpenAsync("orders.sqlite");
var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = models, Registry = registry }, DurableContext.Background);
harness.Resume();

var runner = new WorkflowRunner(harness, catalog);
var result = await runner.RunAsync(orders, new Order { Id = "42" }, new WorkflowStartOptions { RunKey = "order-42" });
Console.WriteLine(result);
await harness.CloseAsync(DurableContext.Background);
```

Agent steps and `ActionContext.Messages` need a team, so they don't work this way. `RunAgentAsync` works if you set `catalog.Agents` to your own `IAgentProvider`, whose `Resolve(string agent)` returns the durable `AgentChange` for a name.

## Workflows as tools

`WorkflowTools` turns a workflow into a tool an agent can call:

- `WorkflowTools.RunTool<TData>(WorkflowCatalog catalog, Workflow<TData> workflow, WorkflowToolOptions? options = null)`: the model passes the whole data object.
- `WorkflowTools.RunTool<TData, TInput>(WorkflowCatalog catalog, Workflow<TData> workflow, Func<TInput, TData> input, WorkflowToolOptions? options = null)`: the model passes a `TInput`, which you map to the data. Use it so the model fills only what a run starts from. `TInput` must be an object type.
- `WorkflowTools.StatusTool()`: `workflow_status(task_id)` reports a run's status, current action, last five history lines and log lines, and its final data once it ended.

The run tool is replay-safe, and it starts the run exactly once per tool call, even across a restart. The run is not owned by the call. If it is still running at the timeout, the tool returns "Workflow '{name}' run {id} is still running; call workflow_status with task_id {id} to follow it." On success it returns "Workflow '{name}' run {id} completed after {steps} steps." followed by a `Data:` line with the final data as JSON. A failed or aborted run is an error result.

| `WorkflowToolOptions` property | Default | Meaning |
| --- | --- | --- |
| `Name` | `run_workflow_<name>`, with `-` and `.` turned into `_` | The tool name. |
| `Description` | the workflow's description, else "Runs the '{name}' workflow on the given data and returns its final data." | The tool description. |
| `Wait` | true | Whether the call waits for the run. False returns at once with the run's task ID. |
| `Timeout` | 10 min | How long the call waits. Must be positive. |

```csharp
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Workflows.Hosting;

var runOrders = WorkflowTools.RunTool(team.Workflows, orders, (OrderArgs args) => new Order { Id = args.OrderId },
    new WorkflowToolOptions { Timeout = TimeSpan.FromMinutes(2) });

team.Catalog.Register(new AgentDefinition
{
    Name = "lead",
    Description = "Handles customer questions about orders",
    Tools = [runOrders, WorkflowTools.StatusTool()],
});

public sealed record OrderArgs([property: Description("The order ID, for example 42")] string OrderId);
```

## Limits and errors

| Limit | Default | Set with | When it's exceeded |
| --- | --- | --- | --- |
| Actions per run | 1000 | `WithLimits(maxSteps: ...)` | The run fails with error kind `limit`. |
| Data size | 256 KB | `WithLimits(maxDataBytes: ...)` | The run fails with error kind `data`. |
| Visits of one action | unlimited | `ActionOptions.MaxVisits` | The run fails with error kind `limit`. |
| Routers in a row | 64 | fixed | The run fails with error kind `routing`. |
| Log lines kept | 200 | fixed | The oldest lines are dropped. |
| History entries kept | 100 | fixed | The oldest entries are dropped. |
| Untaken signals | 100 | fixed | `SignalAsync` throws `InvalidOperationException`. |

The exceptions the library throws:

| Exception | When |
| --- | --- |
| `WorkflowDefinitionException` | `Build()` or a script step finds a problem in the definition. |
| `WorkflowDataException` | The data can't be serialized or read, is too large, or holds a number a double can't store exactly. `Path` holds the JSON path when there is one. Never retried. |
| `WorkflowFailedException` | `EnsureCompleted()` on a run that didn't complete. `Status` holds its status. |
| `ModelCallException` | A prompt or agent call failed: no model, an unknown agent, or no valid typed answer. `Reason` holds a short code such as `no_model` or `unknown_agent`. |
