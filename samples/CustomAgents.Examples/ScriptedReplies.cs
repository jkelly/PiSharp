// Decision 0012: the scripted model the demo runs on without --provider. FauxAgentModels routes each request by what it holds: the agent
// it is for (the "agent" prompt section names it), its last user text, the tools it offers and the last tool result. With --provider a
// real model answers the same requests instead.
using PiSharp.CustomAgents.Testing;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Examples;

internal static class ScriptedReplies
{
    public static void Configure(FauxAgentModels models)
    {
        // The orders workflow's model calls.
        models.On(request => request.LastUserText?.StartsWith("Summarize order", StringComparison.Ordinal) == true, request => Is42(request)
            ? "VIP Ada Lovelace (12 earlier orders) bought a mechanical keyboard and two wireless mice for 199 EUR."
            : "Grace Hopper (1 earlier order) bought three USB-C cables for 28.5 EUR.");
        models.On(request => request.Tools.Contains("submit_result"), request => FauxReply.ToolCall("submit_result", Is42(request)
            ? """{"level":"low","reason":"A long-standing VIP customer shipping within the EU."}"""
            : """{"level":"medium","reason":"A second order from a new customer, shipping overseas."}"""));
        models.On(request => Is(request, TeamDemo.Concierge),
            _ => "Dear Ada, thank you for order 42: your keyboard and mice ship with express priority, and your VIP discount brings it to " +
                "169.15 EUR. We hope they make every keystroke a pleasure. The PiSharp Shop team");
        models.On(request => request.LastUserText?.StartsWith("Write a one-line thank-you", StringComparison.Ordinal) == true,
            _ => "Thank you, Grace, for order 43: your cables are on their way.");

        // The team: the lead looks the order up, gets a quote, delegates the stock check, runs the workflow, then answers.
        models.On(request => Is(request, TeamDemo.Researcher) && !request.AfterTools,
            _ => FauxReply.ToolCall("check_stock", """{"skus":["KB-01","MS-02"]}"""));
        models.On(request => Is(request, TeamDemo.Researcher), request => "Stock: " + request.LastToolResult);
        models.On(request => Is(request, TeamDemo.Lead) && request.LastToolName is null, _ => FauxReply.ToolCall("lookup_order", """{"orderId":"42"}"""));
        models.On(request => Is(request, TeamDemo.Lead) && request.LastToolName == "lookup_order",
            _ => FauxReply.ToolCall("shipping_quote", """{"country":"PT","weightGrams":2400}"""));
        models.On(request => Is(request, TeamDemo.Lead) && request.LastToolName == "shipping_quote",
            _ => FauxReply.ToolCall("delegate", """{"agent":"researcher","task":"Check stock for KB-01 and MS-02."}"""));
        models.On(request => Is(request, TeamDemo.Lead) && request.LastToolName == "delegate",
            _ => FauxReply.ToolCall("run_workflow_orders", """{"orderId":"42"}"""));
        models.On(request => Is(request, TeamDemo.Lead),
            request => "(scripted) What the tools found:\n" + string.Join("\n", ToolResults(request).Select(result => $"{result.Name}: {OneLine(result.Text)}")));
    }

    private static bool Is(FauxRequest request, string agent) =>
        request.Sections.TryGetValue("agent", out var role) && role.Contains($"'{agent}'", StringComparison.Ordinal);

    private static bool Is42(FauxRequest request) => request.LastUserText?.Contains("Ada", StringComparison.Ordinal) == true;

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    /// <summary>Every tool result of the conversation so far (<see cref="FauxRequest.ToolResults"/> holds only the last round's).</summary>
    private static IEnumerable<(string Name, string Text)> ToolResults(FauxRequest request) =>
        from message in request.Messages.OfType<JsonObjectValue>()
        where message.TryGetValue("role", out var role) && role is JsonStringValue { Value: "toolResult" }
        let name = message.TryGetValue("toolName", out var value) && value is JsonStringValue text ? text.Value : "?"
        let content = message.TryGetValue("content", out var parts) && parts is JsonArrayValue array
            ? string.Concat(array.OfType<JsonObjectValue>().Select(part => part.TryGetValue("text", out var item) && item is JsonStringValue s ? s.Value : ""))
            : ""
        select (name, content);
}
