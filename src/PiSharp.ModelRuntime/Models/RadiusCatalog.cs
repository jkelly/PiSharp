// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/ai/src/providers/radius-config.ts.
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;

namespace PiSharp.Cli.Models;

internal static class RadiusCatalog
{
    internal static async Task<JsonArray> FetchAsync(HttpClient client, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RadiusOAuth.DefaultGateway + "/v1/config");
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(JsonNode.Parse(body)) ?? throw new InvalidDataException("Invalid Radius gateway config");
    }

    internal static JsonArray? Parse(JsonNode? node, bool chatOnly = false)
    {
        if (node is not JsonObject config || JsonTree.String(config, "baseUrl") is not { } baseUrl || config["models"] is not JsonArray chat)
            return null;
        var models = new JsonArray();
        Add(chat, "chat", "pi-messages");
        if (chatOnly) return models;
        Add(config["decision"] as JsonArray, "classifier", "pi-decision");
        Add(config["images"] as JsonArray, "image", "pi-image");
        return models;

        void Add(JsonArray? rows, string type, string api)
        {
            if (rows is null) return;
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (JsonTree.String(row, "id") is null || JsonTree.String(row, "name") is null ||
                    row["input"] is not JsonArray input || row["cost"] is not JsonObject) continue;
                if (type != "image" && JsonTree.Number(row, "contextWindow") is null) continue;
                if (type == "chat" && (JsonTree.Boolean(row, "reasoning") is null || JsonTree.Number(row, "maxTokens") is null)) continue;
                var model = row.DeepClone().AsObject();
                if (type == "image")
                {
                    if (row["output"] is not JsonArray output || !output.Any(value => value is JsonValue v && v.TryGetValue<string>(out var m) && m == "image")) continue;
                    model["input"] = Modalities(input);
                    model["output"] = Modalities(output);
                }
                if (type != "chat") model["type"] = type;
                model["provider"] = "radius";
                model["api"] = api;
                model["baseUrl"] = baseUrl;
                models.Add(model);
            }
        }
    }

    private static JsonArray Modalities(JsonArray values) => new([.. values.OfType<JsonValue>()
        .Where(value => value.TryGetValue<string>(out var text) && text is "text" or "image").Select(value => value.DeepClone())]);
}
