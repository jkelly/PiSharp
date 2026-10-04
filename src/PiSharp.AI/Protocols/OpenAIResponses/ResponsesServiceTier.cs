namespace PiSharp.AI.Protocols.OpenAIResponses;

// Pinned Pi openai-responses.ts pricing policy, not a live provider price catalog.
internal static class ResponsesServiceTier
{
    internal static bool Supported(string? tier) => tier is null or "auto" or "default" or "flex" or "scale" or "priority" or "fast" or "ultrafast";
    internal static decimal Multiplier(string model, string? tier) => tier switch
    {
        "flex" => 0.5m,
        "priority" or "fast" => model == "gpt-5.5" ? 2.5m : 2m,
        _ => 1m
    };
}