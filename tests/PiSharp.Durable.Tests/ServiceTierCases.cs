// PiSharp-only (decision 0007): a conversation's service tier (pi.agent serviceTier) reaches its model requests and can be changed or
// cleared between requests; a conversation without one sends none.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static ChatTests;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ServiceTierCases()
    {
        yield return Case("service-tier.agent-tier-reaches-requests-and-clears", async () =>
        {
            var context = DurableContext.Background;
            var seen = new List<string?>();
            var chat = ChatTests.Setup();
            FauxStep Answer() => FauxStep.From(request => { lock (seen) seen.Add(request.Options?.ServiceTier); return FauxAssistantMessage("ok"); });
            chat.Faux.SetResponses(Answer(), Answer(), Answer());
            var (harness, root) = await OpenChat(new MemoryStorage(), chat);
            await (await root.SubmitAsync(SubmissionDraft.Input("one"), context)).WaitAsync(context);
            await root.ConfigureAsync(new AgentChange { ServiceTier = "priority" }, context);
            Equal("priority", (await root.AgentAsync(context)).ServiceTier, "resolved agent");
            await (await root.SubmitAsync(SubmissionDraft.Input("two"), context)).WaitAsync(context);
            await root.ConfigureAsync(new AgentChange { ServiceTier = Patch<string>.Clear }, context);
            await (await root.SubmitAsync(SubmissionDraft.Input("three"), context)).WaitAsync(context);
            Check(seen.Count == 3, "three requests: " + seen.Count);
            Check(seen[0] is null && seen[1] == "priority" && seen[2] is null, $"tiers: {string.Join(",", seen.Select(tier => tier ?? "null"))}");
            await harness.CloseAsync(context);
        });
    }
}
