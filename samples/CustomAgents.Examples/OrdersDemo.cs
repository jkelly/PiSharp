// Decision 0012: the orders workflow. Every action receives the same OrderData and changes it; the engine commits the Data and the next
// action in one step, so a crash loses at most the action that was running. The actions show each kind the library offers:
//   load       an IAction class: code that fetches data (here from an in-memory store) and fails the run when the order is unknown
//   summarize  code that asks the model (ActionContext.PromptAsync): the answer is recorded, so a rerun after a crash reuses it
//   risk       a typed model call (Prompt<RiskVerdict>): structured output through submit_result, validated against RiskVerdict's schema
//   discount   JavaScript (Jint): reads the Data as JSON and returns a merge patch of its changes
//   priority   a C# script (Roslyn): changes the typed Data and stops the run for manual review when the risk is high
//   vip-note   a VIP order goes to the concierge agent (a team definition) ...
//   thank-you  ... any other gets a one-line model answer
using System.ComponentModel;
using PiSharp.CustomAgents.JavaScript;
using PiSharp.CustomAgents.Scripting;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;

namespace PiSharp.CustomAgents.Examples;

/// <summary>A customer (public: the C# script action reads it).</summary>
public sealed class Customer
{
    /// <summary>The customer's name.</summary>
    public string Name { get; set; } = "";
    /// <summary>The country orders ship to.</summary>
    public string Country { get; set; } = "";
    /// <summary>How many orders the customer placed before.</summary>
    public int EarlierOrders { get; set; }
    /// <summary>Whether the customer is a VIP.</summary>
    public bool Vip { get; set; }
}

/// <summary>One line of an order.</summary>
public sealed class OrderLine
{
    /// <summary>The product.</summary>
    public string Sku { get; set; } = "";
    /// <summary>How many.</summary>
    public int Quantity { get; set; }
    /// <summary>The unit price in EUR.</summary>
    public double Price { get; set; }
}

/// <summary>The model's risk assessment (the schema of the risk action's structured output).</summary>
public sealed class RiskVerdict
{
    /// <summary>The risk level.</summary>
    [Description("The risk level: low, medium or high")]
    public string Level { get; set; } = "";
    /// <summary>Why.</summary>
    [Description("One short sentence on why")]
    public string Reason { get; set; } = "";
}

/// <summary>The Data every action of the orders workflow shares.</summary>
public sealed class OrderData
{
    /// <summary>The order to process (the input).</summary>
    public string OrderId { get; set; } = "";
    /// <summary>Loaded by <c>load</c>.</summary>
    public Customer? Customer { get; set; }
    /// <summary>Loaded by <c>load</c>.</summary>
    public List<OrderLine> Lines { get; set; } = [];
    /// <summary>Loaded by <c>load</c>, in EUR.</summary>
    public double Total { get; set; }
    /// <summary>Written by <c>summarize</c>.</summary>
    public string? Summary { get; set; }
    /// <summary>Written by <c>risk</c>.</summary>
    public RiskVerdict? Risk { get; set; }
    /// <summary>Written by <c>discount</c>.</summary>
    public int DiscountPercent { get; set; }
    /// <summary>Written by <c>discount</c>, in EUR.</summary>
    public double Payable { get; set; }
    /// <summary>Written by <c>priority</c>.</summary>
    public string? Priority { get; set; }
    /// <summary>Written by <c>vip-note</c> or <c>thank-you</c>.</summary>
    public string? Note { get; set; }
}

/// <summary>The orders the demo knows.</summary>
internal static class OrderStore
{
    public static readonly IReadOnlyDictionary<string, (Customer Customer, OrderLine[] Lines)> Orders = new Dictionary<string, (Customer, OrderLine[])>
    {
        ["42"] = (new Customer { Name = "Ada Lovelace", Country = "PT", EarlierOrders = 12, Vip = true },
            [new OrderLine { Sku = "KB-01 mechanical keyboard", Quantity = 1, Price = 129 }, new OrderLine { Sku = "MS-02 wireless mouse", Quantity = 2, Price = 35 }]),
        ["43"] = (new Customer { Name = "Grace Hopper", Country = "US", EarlierOrders = 1 },
            [new OrderLine { Sku = "CB-07 USB-C cable", Quantity = 3, Price = 9.5 }]),
    };
}

/// <summary>--crash-in: exits the process inside <paramref name="action"/> once per storage directory (a marker file remembers it), after
/// the action's work and before the engine commits the step: what a crash or a kill at that moment leaves behind.</summary>
internal sealed class Crash(string? action, string directory, TextWriter output)
{
    public void Point(ActionContext context)
    {
        if (action != context.Action || context.IsReplay) return;
        var marker = Path.Combine(directory, $"crashed-in-{action}");
        if (File.Exists(marker)) return;
        File.WriteAllText(marker, $"run {context.TaskId.Value}\n");
        output.WriteLine($"  [crash] exiting inside '{action}' of run {context.TaskId.Value} before the step committed; run the same command again to resume");
        output.Flush();
        Environment.Exit(3);
    }
}

/// <summary><c>load</c>: an action class that fetches the order and its customer.</summary>
internal sealed class LoadOrderAction(Crash crash) : IAction<OrderData>
{
    public string Name => OrdersDemo.Load;

    public async Task<ActionResult> ExecuteAsync(OrderData data, ActionContext context, CancellationToken cancellationToken)
    {
        if (!OrderStore.Orders.TryGetValue(data.OrderId, out var order)) return ActionResult.Fail($"There is no order {data.OrderId}.");
        data.Customer = order.Customer;
        data.Lines = [.. order.Lines];
        data.Total = data.Lines.Sum(line => line.Quantity * line.Price);
        await context.LogAsync($"loaded {data.Lines.Count} lines for {data.Customer.Name}", cancellationToken);
        crash.Point(context);
        return ActionResult.Next();
    }
}

/// <summary>Builds and runs the orders workflow.</summary>
internal static class OrdersDemo
{
    public const string Load = "load";
    public const string Summarize = "summarize";

    public static Workflow<OrderData> Build(CSharpScriptHost host, Crash crash) => Workflow.For<OrderData>("orders")
        .Describe("Processes an order: loads it, summarizes it, assesses its risk, prices and prioritizes it, and writes the customer a note. " +
            "Pass the orderId; the other fields are filled by the workflow.")
        .Then(new LoadOrderAction(crash))
        .Then(Summarize, async (data, context, token) =>
        {
            var lines = string.Join("; ", data.Lines.Select(line => $"{line.Quantity} x {line.Sku} at {line.Price} EUR"));
            data.Summary = await context.PromptAsync(
                $"Summarize order {data.OrderId} for a support agent in one sentence. Customer: {data.Customer!.Name}, {data.Customer.EarlierOrders} " +
                $"earlier orders{(data.Customer.Vip ? ", VIP" : "")}. Lines: {lines}. Total: {data.Total} EUR.", cancellationToken: token);
            await context.LogAsync(context.IsReplay ? "rerun after a restart: the model's recorded answer was reused" : "the model answered", token);
            crash.Point(context);
        })
        .Prompt<RiskVerdict>("risk",
            data => $"Assess the payment and fraud risk of this order: {data.Summary} It ships to {data.Customer!.Country}. " +
                "Answer by calling submit_result.",
            (data, verdict) => data.Risk = verdict)
        .Script("discount", """
            const percent = data.customer.vip ? 15 : (data.total >= 100 ? 5 : 0);
            return {
              updates: { discountPercent: percent, payable: Math.round(data.total * (100 - percent)) / 100 },
              output: `discount ${percent}%`,
            };
            """)
        .CSharp("priority", """
            Data.Priority = Data.Risk?.Level?.ToLowerInvariant() switch
            {
                "high" => "manual-review",
                _ when Data.Customer!.Vip => "express",
                _ => "standard",
            };
            if (Data.Priority == "manual-review")
            {
                Data.Note = "Held for manual review: " + Data.Risk!.Reason;
                return ActionResult.Stop();
            }
            """, host)
        .Branch(data => data.Customer!.Vip, "vip-note", "thank-you")
        .Agent("vip-note", TeamDemo.Concierge,
            data => $"Write a two-sentence note to our VIP customer {data.Customer!.Name} about order {data.OrderId} ({data.Summary}). " +
                $"They pay {data.Payable} EUR after a {data.DiscountPercent}% VIP discount and it ships with priority '{data.Priority}'.",
            (data, answer) => data.Note = answer.Text,
            action: new ActionOptions { Next = Workflow.End })
        .Prompt("thank-you",
            data => $"Write a one-line thank-you to {data.Customer!.Name} for order {data.OrderId}; they pay {data.Payable} EUR.",
            (data, text) => data.Note = text)
        .Build();

    public static async Task RunAsync(AgentSession session, Workflow<OrderData> orders, TextWriter output)
    {
        foreach (var orderId in new[] { "42", "43" })
        {
            await output.WriteLineAsync($"\n== orders workflow, order {orderId}");
            // A run key makes the start idempotent: after a restart the same key finds the run the earlier process started, which
            // OpenAsync has already resumed.
            var run = await session.Workflows.StartAsync(orders, new OrderData { OrderId = orderId }, new WorkflowStartOptions { RunKey = "order-" + orderId });
            await output.WriteLineAsync(run.Existing ? $"  attached to run {run.TaskId.Value} started earlier" : $"  started run {run.TaskId.Value}");
            var result = await run.WaitAsync();
            var state = await run.StatusAsync();
            foreach (var line in state.History)
                await output.WriteLineAsync($"  - {line.Action}{(line.Attempt > 1 ? " (attempt " + line.Attempt + ")" : "")}: {line.Outcome}" +
                    (line.Next is null ? "" : " -> " + line.Next) + (line.Error is null ? "" : " (" + line.Error + ")"));
            foreach (var line in state.Log) await output.WriteLineAsync("    log: " + line);
            await output.WriteLineAsync($"  {result}");
            if (result.Data is not { } data) continue;
            await output.WriteLineAsync($"  summary:  {data.Summary}");
            await output.WriteLineAsync($"  risk:     {data.Risk?.Level} ({data.Risk?.Reason})");
            await output.WriteLineAsync($"  pricing:  {data.Total} EUR - {data.DiscountPercent}% = {data.Payable} EUR, priority {data.Priority}");
            await output.WriteLineAsync($"  note:     {data.Note}");
        }
    }
}
