// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/classifier-shared.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.ModelOperations;

/// <summary>types.ts <c>ProviderClassifier</c>: one classifier API implementation. Implementations never throw for
/// request failures; they return an error or aborted <see cref="ClassifierResult"/>.</summary>
public interface IClassifierApi
{
    /// <summary>The classifier API id this implementation serves (<c>model.api</c>).</summary>
    string Api { get; }
    Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null,
        CancellationToken cancellationToken = default);
}

internal static class ClassifierShared
{
    internal static ClassifierResult Start(ClassifierModel model, ClassifierOptions? options) =>
        new(model.Api, model.Provider, model.Id, [], ModelOperationStopReason.Stop, ProviderRequest.Now(options));

    internal static ClassifierResult Fail(ClassifierResult output, string message, CancellationToken signal) => output with
    {
        Answers = [], StopReason = signal.IsCancellationRequested ? ModelOperationStopReason.Aborted : ModelOperationStopReason.Error,
        ErrorMessage = message
    };

    /// <summary>The questions in JavaScript property order (<c>Object.entries(context.questions)</c>).</summary>
    internal static ImmutableArray<KeyValuePair<string, ClassifierQuestion>> Questions(ClassifierContext context) =>
        ModelOperationJson.Ordered(context.Questions.IsDefault ? [] : context.Questions);

    /// <summary><c>Object.fromEntries</c> of answers or probabilities, in JavaScript property order.</summary>
    internal static ImmutableArray<KeyValuePair<string, T>> Entries<T>(IEnumerable<KeyValuePair<string, T>> entries) =>
        ModelOperationJson.Ordered(entries);

    /// <summary>The question as callers wrote it: <c>{ type, instructions, criteria }</c>.</summary>
    internal static JsonObject QuestionJson(ClassifierQuestion question, string? typeOverride = null)
    {
        var node = new JsonObject();
        switch (question)
        {
            case ClassifierChoiceQuestion choice:
                var criteria = new JsonObject();
                foreach (var (label, meaning) in Entries(choice.Criteria)) criteria[label] = meaning;
                node["type"] = typeOverride ?? "choice"; node["instructions"] = choice.Instructions; node["criteria"] = criteria;
                break;
            case ClassifierScoreQuestion score:
                node["type"] = typeOverride ?? "score"; node["instructions"] = score.Instructions;
                node["criteria"] = new JsonArray([.. score.Criteria.Select(level => (JsonNode)level)]);
                break;
            case ClassifierBoolQuestion boolean:
                node["type"] = typeOverride ?? "bool"; node["instructions"] = boolean.Instructions;
                node["criteria"] = new JsonObject { ["true"] = boolean.True, ["false"] = boolean.False };
                break;
            default: throw new ArgumentException("Unsupported classifier question.");
        }
        return node;
    }

    /// <summary><c>postClassifierRequest</c>: one JSON POST with bearer auth, the payload and response hooks, a fresh
    /// timeout per attempt and provider retries (default 2). <paramref name="noRetryStatuses"/> fail at once.</summary>
    internal static async Task<JsonElement> PostAsync(string label, Uri url, ClassifierModel model, JsonNode body,
        ClassifierOptions? options, CancellationToken signal, IReadOnlyCollection<int>? noRetryStatuses = null)
    {
        if (string.IsNullOrEmpty(options?.ApiKey)) throw new InvalidOperationException($"No API key for provider: {model.Provider}");
        var payload = body;
        if (options.OnPayload is { } onPayload && await onPayload(body.DeepClone(), model, signal).ConfigureAwait(false) is { } replaced)
            payload = replaced;
        var text = ProviderRequest.Stringify(payload);
        var headers = ProviderRequest.MergeHeaders(
            [new("authorization", "Bearer " + options.ApiKey), new("content-type", "application/json")],
            ProviderRequest.Nullable(model.Headers), options.Headers);
        var http = ProviderRequest.Http(options);
        var (response, json) = await ProviderRequest.RetryAsync(async () =>
        {
            var (info, responseText) = await ProviderRequest.PostAsync(http, url, headers, text, options, signal).ConfigureAwait(false);
            if (!ProviderRequest.IsSuccess(info.Status))
                throw new ProviderRequestException($"{label} returned {info.Status}", info.Status, info.Headers, responseText);
            return (info, ProviderRequest.ParseJson(label, responseText));
        }, options.MaxRetries ?? 2, options, noRetryStatuses, signal).ConfigureAwait(false);
        if (options.OnResponse is { } onResponse) await onResponse(response, model, signal).ConfigureAwait(false);
        return json;
    }

    private static long TokenCount(JsonElement? value) =>
        value is { } element && ProviderRequest.FiniteNumber(element, out var number) && number > 0 ? (long)Math.Min(number, long.MaxValue) : 0;

    /// <summary><c>parseClassifierUsage</c>: usage from <c>{ input_tokens, output_tokens }</c> priced from the catalog. A
    /// missing or malformed usage object leaves the result without usage.</summary>
    internal static TokenUsage? ParseUsage(JsonElement? value, ClassifierModel model)
    {
        if (value is not { ValueKind: JsonValueKind.Object } usage) return null;
        var hasInput = usage.TryGetProperty("input_tokens", out var inputTokens);
        var hasOutput = usage.TryGetProperty("output_tokens", out var outputTokens);
        if (!hasInput && !hasOutput) return null;
        var input = TokenCount(hasInput ? inputTokens : null); var output = TokenCount(hasOutput ? outputTokens : null);
        return ProviderRequest.Usage(input, output, 0, 0, input + output, model.Cost.Calculate(input, output, 0, 0));
    }

    internal static JsonElement? Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : null;
}
