// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/ai/src/api/json-request.ts.
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.ModelOperations;

internal static class PiUsage
{
    internal static TokenUsage? Parse(JsonElement? value, OperationModel model)
    {
        if (value is not { ValueKind: JsonValueKind.Object } usage) return null;
        static double? Number(JsonElement obj, string key) =>
            ClassifierShared.Field(obj, key) is { } field && ProviderRequest.FiniteNumber(field, out var n) ? n : null;
        static double? Split(JsonElement obj, string key) => Number(obj, key) is >= 0 and var n ? n : null;
        var rawCost = ClassifierShared.Field(usage, "cost") ?? default;
        var reported = Number(rawCost, "total");
        if (Number(usage, "input") is null && Number(usage, "output") is null && reported is null) return null;
        var input = Split(usage, "input") ?? 0;
        var output = Split(usage, "output") ?? 0;
        var read = Split(usage, "cacheRead") ?? 0;
        var write = Split(usage, "cacheWrite") ?? 0;
        var image = Split(usage, "imageOutput");
        var total = Split(usage, "totalTokens") ?? input + output + read + write;
        var cost = model.Cost.Calculate(input, output, read, write, image);
        bool? fromProvider = false;
        if (reported.HasValue)
        {
            cost = (Split(rawCost, "input") ?? 0, Split(rawCost, "output") ?? 0, Split(rawCost, "cacheRead") ?? 0,
                Split(rawCost, "cacheWrite") ?? 0, Math.Max(0, reported.Value));
            fromProvider = ClassifierShared.Field(rawCost, "totalFromProvider")?.ValueKind switch
            { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        }
        return WithSplits(ProviderRequest.Usage(input, output, read, write, total, cost, fromProvider), Split(usage, "reasoning"), image);
    }

    internal static TokenUsage WithSplits(TokenUsage usage, double? reasoning, double? image)
    {
        var fields = JsonFields.Empty;
        if (reasoning.HasValue) fields = fields.Set("reasoning", JsonData.Parse(JsonNumber.Text(reasoning.Value)));
        if (image.HasValue) fields = fields.Set("imageOutput", JsonData.Parse(JsonNumber.Text(image.Value)));
        return usage with { ExtraProperties = fields, ExtrasBeforeTotal = true };
    }
}
