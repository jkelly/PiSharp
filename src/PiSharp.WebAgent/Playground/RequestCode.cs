// Decision 0011 (run W4, R1): View code (_ViewCodeModal), generated from the request a run would really send: the run engine's plan
// (RunEngine.Plan: the normalized spec, the Pi wire messages, the body patch and the headers it adds). Three tabs:
//   - C# (PiSharp): the same call through PiSharp.Durable's IDurableModels (what the run engine streams through), with the messages as Pi
//     wire JSON and the options the engine passes; the body patch and headers the Web Agent's request hook adds are spelled out.
//   - cURL: the run through this Web Agent (POST /api/runs with run.json, then its event stream), which the Spend Guard checks and runs.db
//     records, and below it what the provider request carries that this run added.
//   - JSON: run.json (the body of POST /api/runs), then the provider part (API, base URL, patch, headers).
// No secret is ever in it: a run names its account by id only, the spec refuses credential-named headers and provider options, and the
// provider's credential is shown as a placeholder (••••): the key stays on the server (W2 rule 3; brief rule 6). Image data is abbreviated.
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Playground;

/// <summary>Generates <see cref="ViewCodeView"/>.</summary>
public static class RequestCode
{
    /// <summary>What a credential is shown as.</summary>
    public const string Placeholder = "••••";

    /// <summary>How many characters of image data are kept.</summary>
    public const int ImageChars = 24;

    /// <summary>The code of <paramref name="plan"/>.</summary>
    /// <param name="plan">The run engine's plan of the request (RunEngine.Plan).</param>
    /// <param name="origin">This app's origin (<c>http://127.0.0.1:5180</c>), for the cURL tab.</param>
    /// <param name="baseUrl">The model's base URL (public catalog data), or null.</param>
    public static ViewCodeView Generate(RunRequestPlan plan, string origin, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(origin);
        var spec = plan.Spec;
        var model = plan.Model;
        var offline = model.Api == "faux";
        var run = RunJson(spec);
        var json = RequestJson.Indented(run);
        var provider = ProviderJson(plan, baseUrl);
        var notes = new List<string>();
        if (!plan.Allowed) notes.Add(plan.RefusalMessage!);
        notes.AddRange(plan.Notes);
        if (spec.Messages.Any(message => message.Images is { Count: > 0 })) notes.Add("Image data is abbreviated here; the run sends it whole.");
        var subtitle = $"This request as code · {model.Provider}/{model.Id} · {model.Api}" + (offline ? " · offline" : "");
        return new ViewCodeView(subtitle, CSharp(plan), Curl(plan, origin.TrimEnd('/'), baseUrl), json, provider is null ? null : RequestJson.Indented(provider))
        {
            Notes = notes,
        };
    }

    /// <summary>The body of <c>POST /api/runs</c> for <paramref name="spec"/>: the spec without nulls, image data abbreviated.</summary>
    public static JsonObject RunJson(RunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(spec, PiSharp.WebAgent.Data.SettingsStore.JsonOptions)!.AsObject();
        json.Remove("spendSource");
        json.Remove("thinkingLevel");
        foreach (var key in json.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList()) json.Remove(key);
        if (json["messages"] is JsonArray messages)
            foreach (var message in messages.OfType<JsonObject>())
            {
                foreach (var key in message.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList()) message.Remove(key);
                if (message["images"] is JsonArray images)
                    foreach (var image in images.OfType<JsonObject>())
                        if (image["data"] is JsonValue data && data.TryGetValue<string>(out var text) && text.Length > ImageChars)
                            image["data"] = $"{text[..ImageChars]}… ({text.Length * 3 / 4 / 1024:N0} KB, abbreviated)";
            }
        return json;
    }

    private static JsonObject? ProviderJson(RunRequestPlan plan, string? baseUrl)
    {
        var model = plan.Model;
        if (model.Api == "faux") return null;
        var json = new JsonObject
        {
            ["api"] = model.Api,
            ["baseUrl"] = string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
            ["path"] = plan.PatchPath,
            ["credential"] = Placeholder + " (the account's; it stays on the server)",
        };
        if (plan.Headers is { Count: > 0 } headers)
            json["headers"] = new JsonObject([.. headers.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value)))]);
        if (plan.Patch is { } patch) json["bodyAdds"] = patch.DeepClone();
        if (plan.Thinking is { } level) json["thinkingLevel"] = level;
        if (plan.Spec.MaxTokens is { } max) json["maxTokens"] = max;
        return json;
    }

    private static string CSharp(RunRequestPlan plan)
    {
        var spec = plan.Spec;
        var model = plan.Model;
        var code = new StringBuilder();
        code.AppendLine("// Generated from the Playground: the same call through PiSharp.Durable's model access, as the Web Agent's run engine makes it.");
        code.AppendLine("using System.Collections.Immutable;");
        code.AppendLine("using PiSharp.Contracts;");
        code.AppendLine("using PiSharp.Durable;");
        code.AppendLine("using JsonValue = PiSharp.Durable.Chord.JsonValue;");
        code.AppendLine();
        code.AppendLine("IDurableModels models = /* your host's model access: the Web Agent's routes each provider through its account */;");
        code.AppendLine($"var model = models.GetModel({Literal(model.Provider)}, {Literal(model.Id)}) ?? throw new InvalidOperationException(\"Unknown model.\");");
        code.AppendLine();
        code.AppendLine("// The system prompt and tools ride in the first system message (Pi wire JSON).");
        code.AppendLine("ImmutableArray<JsonValue> messages =");
        code.AppendLine("[");
        foreach (var message in plan.Messages)
        {
            var node = JsonNode.Parse(message.ToJson());
            if (node is JsonObject json) Abbreviate(json);
            code.Append("    JsonValue.Parse(\"\"\"\n").Append(Indent(RequestJson.Indented(node), "        ")).Append("\n        \"\"\"),\n");
        }
        code.AppendLine("];");
        code.AppendLine();
        code.AppendLine("var options = new DurableStreamOptions");
        code.AppendLine("{");
        if (spec.MaxTokens is { } max) code.AppendLine($"    MaxTokens = {max},");
        if (plan.Thinking is { } level) code.AppendLine($"    Reasoning = {Literal(level)},");
        code.AppendLine("};");
        if (plan.Patch is { } patch)
        {
            code.AppendLine();
            code.AppendLine($"// The Web Agent merges these fields into the provider's request body (the request to ...{plan.PatchPath}):");
            foreach (var line in RequestJson.Indented(patch).Split('\n')) code.AppendLine("//   " + line.TrimEnd('\r'));
        }
        if (plan.Headers is { Count: > 0 } headers)
        {
            code.AppendLine("// and adds these headers:");
            foreach (var (name, value) in headers) code.AppendLine($"//   {name}: {value}");
        }
        code.AppendLine();
        code.AppendLine("await foreach (var e in models.StreamSimple(model, messages, options, cancellationToken))");
        code.AppendLine("{");
        code.AppendLine("    switch (e)");
        code.AppendLine("    {");
        code.AppendLine("        case ThinkingDelta t: Console.Write(t.Delta); break;");
        code.AppendLine("        case TextDelta t: Console.Write(t.Delta); break;");
        code.AppendLine("        case ToolCallEnded c: Console.WriteLine($\"\\n→ {c.ToolCall.Name} {c.ToolCall.Arguments}\"); break;");
        code.AppendLine("        case StreamDone d: Console.WriteLine($\"\\n[{d.Reason}] {d.Message.Usage.TotalTokens} tokens\"); break;");
        code.AppendLine("        case StreamError x: Console.Error.WriteLine($\"\\n[{x.Reason}] the request failed\"); break;");
        code.AppendLine("    }");
        code.Append('}');
        return code.ToString();
    }

    private static string Curl(RunRequestPlan plan, string origin, string? baseUrl)
    {
        var model = plan.Model;
        var code = new StringBuilder();
        code.AppendLine("# Run it through this Web Agent: the Spend Guard checks it, runs.db records it, and the account's key stays on the server.");
        code.AppendLine($"curl -sS {origin}/api/runs \\");
        code.AppendLine("  -H \"content-type: application/json\" \\");
        code.AppendLine("  --data-binary @run.json        # body: the JSON tab");
        code.AppendLine("# The answer is {\"posted\": true, \"id\": ..., \"events\": ...}, or the Spend Guard's question; then stream the run:");
        code.Append($"curl -N {origin}/api/runs/<id>/events");
        if (model.Api == "faux")
        {
            code.AppendLine().Append("# The faux provider runs offline: no provider request is made.");
            return code.ToString();
        }
        code.AppendLine().AppendLine("#");
        code.AppendLine($"# The provider request PiSharp sends for it ({model.Api}):");
        code.AppendLine($"#   POST {(string.IsNullOrEmpty(baseUrl) ? "the provider's base URL" : baseUrl)}{(plan.PatchPath is { } path ? " … " + path : "")}");
        code.AppendLine($"#   credential: {Placeholder} (the account's, added by the transport; never shown)");
        foreach (var (name, value) in plan.Headers ?? new Dictionary<string, string>()) code.AppendLine($"#   {name}: {value}");
        code.Append(plan.Patch is { } patch ? "#   body fields this run adds: " + RequestJson.Compact(patch) : "#   body: the transport's own (this run adds no fields)");
        return code.ToString();
    }

    private static void Abbreviate(JsonObject message)
    {
        if (message["content"] is not JsonArray parts) return;
        foreach (var part in parts.OfType<JsonObject>())
            if (part["data"] is JsonValue data && data.TryGetValue<string>(out var text) && text.Length > ImageChars)
                part["data"] = text[..ImageChars] + "…";
    }

    private static string Literal(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Indent(string text, string prefix) => string.Join("\n", text.Split('\n').Select(line => prefix + line.TrimEnd('\r')));
}
