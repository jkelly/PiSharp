// Decision 0012: a team of agents. The lead (the orchestrator) answers a customer question with a typed C# tool (lookup_order), a JavaScript
// tool (shipping_quote), a subagent it delegates to (the researcher, whose check_stock tool is a C# script), and the orders workflow as a
// tool (run_workflow_orders). The concierge is the agent the workflow's vip-note action runs, and the scout (WebDemo) searches the web.
// Each agent sees only its own tools: a definition selects them, and children never inherit their parent's.
using System.ComponentModel;
using PiSharp.CustomAgents.JavaScript;
using PiSharp.CustomAgents.Scripting;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Tools;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Examples;

/// <summary>The arguments of lookup_order and run_workflow_orders.</summary>
internal sealed record OrderArgs([property: Description("The order ID, for example 42")] string OrderId);

/// <summary>The arguments of check_stock (public: a C# script reads them).</summary>
/// <param name="Skus">The product codes.</param>
public sealed record CheckStockArgs([property: Description("Product codes, for example KB-01")] string[] Skus);

/// <summary>Registers and runs the team.</summary>
internal static class TeamDemo
{
    public const string Lead = "lead";
    public const string Researcher = "researcher";
    public const string Concierge = "concierge";

    public const string Question =
        "Ada Lovelace asks whether her order 42 can reach Lisbon by Friday. Find out what she ordered, get a shipping quote, have the " +
        "researcher check stock for her items, and process the order with the orders workflow. Then write the reply to send her.";

    /// <summary>Registers the lead, the researcher and the concierge; with <paramref name="scout"/> the lead may also delegate to the web
    /// search scout (<see cref="WebDemo"/>).</summary>
    public static async Task RegisterAsync(AgentTeam team, Workflow<OrderData> orders, CSharpScriptHost host, TextWriter output, bool scout)
    {
        // A typed C# tool: the parameter schema comes from the record, and the Harness validates the model's arguments against it.
        var lookupOrder = AgentTool.Create<OrderArgs>("lookup_order", "Looks up an order's customer, lines and total.", (args, _) =>
        {
            if (!OrderStore.Orders.TryGetValue(args.OrderId, out var order)) return Task.FromResult(AgentToolResult.Error($"There is no order {args.OrderId}."));
            var lines = string.Join("; ", order.Lines.Select(line => $"{line.Quantity} x {line.Sku} at {line.Price} EUR"));
            return Task.FromResult<AgentToolResult>(
                $"Order {args.OrderId}: {order.Customer.Name} ({(order.Customer.Vip ? "VIP" : "regular")}), ships to {order.Customer.Country}. " +
                $"Lines: {lines}. Total {order.Lines.Sum(line => line.Quantity * line.Price)} EUR. Weight about {order.Lines.Sum(line => line.Quantity) * 800} g.");
        });

        // A JavaScript tool in the Jint sandbox (pure: no host access); its return value is the result.
        var shippingQuote = JsTool.Create(new JsToolDefinition("shipping_quote", "Quotes shipping cost and delivery days to a country.", """
            const rates = { PT: 6.5, ES: 6.5, DE: 5, US: 18 };
            const base = rates[args.country] ?? 25;
            return { country: args.country, cost: Math.round((base + 1.2 * args.weightGrams / 1000) * 100) / 100, currency: 'EUR',
                     businessDays: args.country === 'US' ? 7 : 3 };
            """)
        {
            Parameters = (JsonObjectValue)JsonValue.Parse("""
                {"type":"object","required":["country","weightGrams"],"properties":{
                  "country":{"type":"string","description":"ISO 3166 country code, for example PT"},
                  "weightGrams":{"type":"integer","description":"Parcel weight in grams"}}}
                """),
        });

        // A C# script tool (Roslyn), compiled once and cached by the hash of its source.
        var checkStock = await ScriptTool.CreateAsync<CheckStockArgs>("check_stock", "Reports stock levels for product codes.", """
            var stock = new Dictionary<string, int> { ["KB-01"] = 4, ["MS-02"] = 0, ["CB-07"] = 120 };
            return string.Join("; ", Args.Skus.Select(sku =>
            {
                var code = sku.Split(' ')[0].ToUpperInvariant();
                return stock.TryGetValue(code, out var count)
                    ? $"{code}: {count} in stock" + (count == 0 ? " (restock arrives Thursday)" : "")
                    : $"{code}: unknown product";
            }));
            """, host);

        // The workflow as a tool: the model passes only the order ID (OrderArgs), mapped to the Data a run starts from; the call waits for
        // the run and returns its final Data.
        var runOrders = WorkflowTools.RunTool(team.Workflows, orders, (OrderArgs args) => new OrderData { OrderId = args.OrderId });

        team.Catalog.Register(new AgentDefinition
        {
            Name = Lead,
            Description = "Handles customer questions about orders",
            Instructions = "You are the support lead of a small electronics shop. Use your tools for facts, delegate stock checks to the " +
                "researcher" + (scout ? ", delegate questions that need the web to the scout" : "") +
                ", and use run_workflow_orders to process an order. Finish with the reply to send the customer, at most four sentences.",
            Subagents = scout ? [Researcher, WebDemo.Scout] : [Researcher],
            Tools = [.. Logged(Lead, output, lookupOrder, shippingQuote, runOrders, WorkflowTools.StatusTool())],
        });
        team.Catalog.Register(new AgentDefinition
        {
            Name = Researcher,
            Description = "Checks stock levels for products",
            Instructions = "Use check_stock with the product codes you are given, then answer in one sentence.",
            Tools = [.. Logged(Researcher, output, checkStock)],
        });
        team.Catalog.Register(new AgentDefinition
        {
            Name = Concierge,
            Description = "Writes warm, short notes to VIP customers",
            Instructions = "Write warmly and briefly, with no subject line and no placeholders. Sign as 'The PiSharp Shop team'.",
        });
    }

    public static async Task<bool> RunAsync(AgentSession session, TextWriter output)
    {
        await output.WriteLineAsync("\n== team: the lead answers a customer question");
        await output.WriteLineAsync("  question: " + Question);
        // The request ID makes the question idempotent: run the demo again on the same storage and the settled answer comes back.
        AgentReply reply;
        try { reply = await session.AskAsync(Question, requestId: "demo-team-question"); }
        catch (InvalidOperationException error)
        {
            await output.WriteLineAsync("  failed: " + error.Message);
            return false;
        }
        await output.WriteLineAsync("  answer: " + reply.Text.ReplaceLineEndings("\n          "));
        foreach (var agent in await session.DirectoryAsync())
            await output.WriteLineAsync($"  agent {agent.Address}: {agent.Definition}, {agent.Kind}{(agent.Parent is null ? "" : ", parent " + agent.Parent)}");
        return true;
    }

    /// <summary><paramref name="tools"/>, each printing its calls as they start and the first line of what they return.</summary>
    internal static IEnumerable<ToolRegistration> Logged(string agent, TextWriter output, params ToolRegistration[] tools) => tools.Select(tool => tool with
    {
        Execute = async (arguments, api, context) =>
        {
            output.WriteLine($"  - {agent} calls {tool.Name} {arguments.ToJson()}");
            var result = await tool.Execute(arguments, api, context);
            var text = string.Concat((result.Output?.AsEnumerable() ?? []).OfType<JsonObjectValue>()
                .Select(part => part.TryGetValue("text", out var value) && value is JsonStringValue piece ? piece.Value : ""));
            var first = text.Split('\n', 2)[0];
            output.WriteLine($"    {(result.IsError == true ? "error" : "returned")}: {(first.Length > 160 ? first[..160] + "…" : first)}");
            return result;
        },
    });
}
