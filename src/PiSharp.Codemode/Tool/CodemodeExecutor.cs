// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/codemode/execute.ts.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions.Mcp.Discovery;

namespace PiSharp.Codemode;

/// <summary>Runs one codemode script and builds the tool result. Without a host scripts cannot call tools, <c>store()</c>
/// starts empty and writes are dropped.</summary>
public static class CodemodeExecutor
{
    private const int ArgsPreviewChars = 200;
    private const int ErrorPreviewChars = 500;
    /// <summary><c>models.classify()</c>/<c>models.generateImages()</c> calls one script may have in flight.</summary>
    public const int MaxConcurrentModelCalls = 4;
    /// <summary>Default token budget for script output.</summary>
    public const int DefaultMaxOutputTokens = 10_000;
    private const int CharsPerToken = 4;
    private static readonly ImmutableDictionary<string, string> ImageExtensions = new Dictionary<string, string>
        { ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/gif"] = ".gif", ["image/webp"] = ".webp" }.ToImmutableDictionary();
    private static readonly string[] ModelTypes = ["chat", "image", "classifier"];

    /// <summary>Test seam for output files (temp files by default).</summary>
    internal static Func<string, string, byte[], CancellationToken, Task<string>> WriteOutputFile { get; set; } = CodemodeStore.WriteOutputFileAsync;

    private abstract record Item;
    private sealed record TextItem(string Text) : Item;
    private sealed record ImageItem(string Data, string MimeType) : Item;

    /// <summary>A nested call row, shown by the renderer as it runs.</summary>
    private sealed class NestedCall(string id, string name, string args)
    {
        public string Id { get; set; } = id;
        public string Name { get; } = name;
        public string Args { get; } = args;
        public string Status { get; set; } = "running";
        public double? DurationMs { get; set; }
        public string? Error { get; set; }
        public double? Cost { get; set; }
    }

    private static string TruncateText(string text, int max) => text.Length > max ? text[..(max - 3)] + "..." : text;

    private static string PreviewArgs(JsonData? args) => args is null ? "" : TruncateText(Js.Stringify(args.Value), ArgsPreviewChars);

    private static string TextOf(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Join("\n", content.EnumerateArray().Where(block => block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
                block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String).Select(block => block.GetProperty("text").GetString()))
            : "";

    /// <summary>Run one script: <paramref name="input"/> is the tool's <c>code</c> argument. Returns the tool result JSON
    /// (<c>{ content, details, usage?, isError? }</c>). Invalid <c>// @options:</c> throw <see cref="CodemodeSourceException"/>.</summary>
    public static async Task<JsonData> ExecuteAsync(string toolCallId, string input, ICodemodeHost? host,
        Func<JsonData, ValueTask>? onUpdate = null, bool models = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCallId); ArgumentNullException.ThrowIfNull(input);
        var started = Stopwatch.StartNew();
        var parsed = CodemodeSource.Parse(input);
        var calls = new List<NestedCall>(); var gate = new object();
        JsonObject? modelUsage = null; var generatedImages = 0;
        JsonData Snapshot(string? fullOutputPath = null)
        {
            var details = new JsonObject();
            var rows = new JsonArray();
            lock (gate)
                foreach (var call in calls)
                {
                    var row = new JsonObject { ["id"] = call.Id, ["name"] = call.Name, ["args"] = call.Args, ["status"] = call.Status };
                    if (call.DurationMs is { } duration) row["durationMs"] = duration;
                    if (call.Error is { } error) row["error"] = error;
                    if (call.Cost is { } cost) row["cost"] = cost;
                    rows.Add(row);
                }
            details["calls"] = rows;
            if (fullOutputPath is not null) details["fullOutputPath"] = fullOutputPath;
            return JsonData.Parse(details.ToJsonString(Relaxed));
        }
        async ValueTask Publish()
        {
            if (onUpdate is null) return;
            var partial = new JsonObject { ["content"] = new JsonArray(), ["details"] = JsonNode.Parse(Snapshot().ToString()) };
            try { await onUpdate(JsonData.Parse(partial.ToJsonString(Relaxed))).ConfigureAwait(false); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { /* A failed progress update does not fail the script. */ }
        }

        var callable = host is null ? [] : CodemodeToolDefinition.Callable(host.Tools);
        var samples = callable.ToDictionary(tool => tool.Name,
            tool => CodemodeDeclarations.RenderToolSample(CodemodeToolDefinition.ToDeclaration(tool, tool.PromptGuidelines.IsDefault ? [] : tool.PromptGuidelines)), StringComparer.Ordinal);
        var sandboxTools = callable.Select(tool => new CodemodeTool(tool.Name, async (args, callToken) =>
        {
            var record = new NestedCall(toolCallId + "/?", tool.Name, PreviewArgs(args));
            lock (gate) calls.Add(record);
            await Publish().ConfigureAwait(false);
            var callStarted = Stopwatch.StartNew();
            var outcome = await host!.ExecuteToolAsync(tool.Name, args, callToken).ConfigureAwait(false);
            lock (gate)
            {
                if (outcome.ToolCallId is { } id) record.Id = id;
                record.DurationMs = callStarted.Elapsed.TotalMilliseconds;
                if (outcome.IsError)
                {
                    record.Status = callToken.IsCancellationRequested ? "cancelled" : "error";
                    var text = TextOf(outcome.Result.Value);
                    record.Error = TruncateText(text.Length > 0 ? text : $"Tool \"{tool.Name}\" failed", ErrorPreviewChars);
                }
                else record.Status = "ok";
            }
            await Publish().ConfigureAwait(false);
            return ToScriptValue(tool, outcome);
        }) { Description = samples[tool.Name] }).ToImmutableArray();

        var globals = DiscoveryGlobals(callable, samples).ToList();
        if (models && host?.Models is { } runtime)
            globals.AddRange(ModelGlobals(runtime, toolCallId, calls, gate, Publish,
                usage => { lock (gate) modelUsage = CombineUsage(modelUsage, usage); },
                count => Interlocked.Add(ref generatedImages, count)));

        CodemodeResult result;
        await using (var sandbox = new CodemodeSandbox(new()
        {
            Tools = sandboxTools, Globals = [.. globals],
            TimeoutMs = parsed.Options.TimeoutMs is { } timeout ? timeout : null,
            MemoryLimitBytes = CodemodeLimits.DefaultMemoryLimitBytes
        }))
        {
            var store = host?.ReadStore() ?? [];
            result = await sandbox.ExecuteAsync(parsed.Code, new() { Store = store }, cancellationToken).ConfigureAwait(false);
        }
        lock (gate) foreach (var call in calls) if (call.Status == "running") call.Status = "cancelled";

        var scriptOutput = result.Output.ToList();
        if (result.Ok)
        {
            if (!result.StoreWrites.IsEmpty && host is not null)
                await host.AppendStoreEntryAsync(CodemodeStore.EntryData(result.StoreWrites), CancellationToken.None).ConfigureAwait(false);
            // pi extension: a returned value is appended like text().
            if (result.Value is { } value) scriptOutput.Add(new CodemodeTextOutput(value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : Js.Stringify(value.Value)));
        }
        var items = FormatOutput(scriptOutput);
        if (!result.Ok)
        {
            NestedCall[] snapshot; lock (gate) snapshot = [.. calls];
            items.Add(new TextItem("Script error:\n" + FormatError(result.Error!, snapshot)));
        }
        var images = Volatile.Read(ref generatedImages);
        if (images > 0 && !items.Any(item => item is ImageItem))
            items.Add(new TextItem($"Note: models.generateImages() returned {images} image{(images == 1 ? "" : "s")} that the script did not show. Show each image block of result.output with image(block)."));
        var (truncated, fullOutputPath) = await TruncateOutputAsync(JoinAdjacentText(items), parsed.Options.MaxOutputTokens ?? DefaultMaxOutputTokens).ConfigureAwait(false);
        // After truncation, which joins the text items and moves images after them, so each path stays next to its image.
        var output = JoinAdjacentText(await SaveImagesAsync(truncated).ConfigureAwait(false));
        var wallTime = (started.Elapsed.TotalMilliseconds / 1000).ToString("F1", CultureInfo.InvariantCulture);
        var header = $"{(result.Ok ? "Script completed" : "Script failed")}\nWall time {wallTime} seconds\nOutput:\n";
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = header } };
        foreach (var item in output)
            content.Add(item is TextItem text ? new JsonObject { ["type"] = "text", ["text"] = text.Text }
                : new JsonObject { ["type"] = "image", ["data"] = ((ImageItem)item).Data, ["mimeType"] = ((ImageItem)item).MimeType });
        var final = new JsonObject { ["content"] = content, ["details"] = JsonNode.Parse(Snapshot(fullOutputPath).ToString()) };
        lock (gate) if (modelUsage is not null) final["usage"] = modelUsage.DeepClone();
        if (!result.Ok) final["isError"] = true;
        return JsonData.Parse(final.ToJsonString(Relaxed));
    }

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The value a script receives: a tool that declares an output schema resolves to its structured content, also
    /// for error results that carry one; any other tool to its text. Other failures reject with the tool's error text.</summary>
    private static JsonData? ToScriptValue(CodemodeNestedTool tool, CodemodeNestedOutcome outcome)
    {
        if (tool.OutputSchema is not null)
        {
            if (outcome.ScriptValue is { } scripted) return scripted;
            if (outcome.Result.Value.ValueKind == JsonValueKind.Object && outcome.Result.Value.TryGetProperty("structuredContent", out var structured))
                return JsonData.FromElement(structured.Clone());
        }
        var text = TextOf(outcome.Result.Value);
        if (outcome.IsError) throw new InvalidOperationException(text.Length > 0 ? text : $"Tool \"{tool.Name}\" failed");
        return JsonData.Parse(Js.Stringify(text));
    }

    /// <summary>With more than one text item, each starts with <c>==&gt; text N/M &lt;==</c>; <c>console.*</c> lines follow
    /// the other output in one <c>&lt;console_output&gt;</c> block.</summary>
    private static List<Item> FormatOutput(IReadOnlyList<CodemodeOutputItem> output)
    {
        var total = output.Count(item => item is CodemodeTextOutput { Console: false });
        var items = new List<Item>(); var consoleLines = new List<string>(); var index = 0;
        foreach (var item in output)
            switch (item)
            {
                case CodemodeImageOutput image: items.Add(new ImageItem(image.Data, image.MimeType)); break;
                case CodemodeTextOutput { Console: true } line: consoleLines.Add(line.Text); break;
                case CodemodeTextOutput text:
                    index++;
                    items.Add(new TextItem(total > 1 ? $"==> text {index}/{total} <==\n{text.Text}" : text.Text));
                    break;
            }
        if (consoleLines.Count > 0) items.Add(new TextItem($"<console_output>\n{string.Join("\n", consoleLines)}\n</console_output>"));
        return items;
    }

    /// <summary>Join adjacent text items into one, each part starting on its own line.</summary>
    private static List<Item> JoinAdjacentText(IEnumerable<Item> items)
    {
        var joined = new List<Item>();
        foreach (var item in items)
        {
            if (item is TextItem text && joined.Count > 0 && joined[^1] is TextItem last)
            {
                var separator = last.Text.Length == 0 || last.Text.EndsWith('\n') ? "" : "\n";
                joined[^1] = new TextItem(last.Text + separator + text.Text);
            }
            else joined.Add(item);
        }
        return joined;
    }

    private static string FormatCallSummary(IReadOnlyList<NestedCall> calls) => calls.Count == 0 ? "No tool calls were made." :
        "Tool calls made before the failure (they are not undone): " + string.Join(", ", calls.Select(call => $"{call.Name} ({call.Status})"));

    private static string FormatError(CodemodeError error, IReadOnlyList<NestedCall> calls)
    {
        var head = error.Kind switch
        {
            CodemodeErrorKind.Script => error.Stack ?? $"{error.Name ?? "Error"}: {error.Message}",
            CodemodeErrorKind.Timeout => "Script timed out: " + error.Message,
            CodemodeErrorKind.Aborted => "Script aborted: " + error.Message,
            _ => "Script sandbox failed: " + error.Message
        };
        return $"{head}\n\n{FormatCallSummary(calls)}";
    }

    /// <summary>truncate.ts formatSize.</summary>
    internal static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes}B" : bytes < 1024 * 1024
        ? (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + "KB"
        : (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + "MB";

    /// <summary>Save each image to a temp file and put a text item with its path before it; images shown more than once
    /// are saved once. A failed write becomes part of the label.</summary>
    private static async Task<List<Item>> SaveImagesAsync(List<Item> items)
    {
        var labels = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        async Task<string> Label(ImageItem image)
        {
            var bytes = Convert.FromBase64String(image.Data);
            var kind = $"{image.MimeType}, {FormatSize(bytes.Length)}";
            var extension = ImageExtensions.GetValueOrDefault(image.MimeType) ?? throw new InvalidOperationException($"No file extension for image type {image.MimeType}");
            try { return $"[Image saved to {await WriteOutputFile("pi-codemode", extension, bytes, CancellationToken.None).ConfigureAwait(false)} ({kind})]"; }
            catch (Exception error) { return $"[Image ({kind}) could not be saved: {error.Message}]"; }
        }
        var result = new List<Item>();
        foreach (var item in items)
        {
            if (item is not ImageItem image) { result.Add(item); continue; }
            if (!labels.TryGetValue(image.Data, out var pending)) labels[image.Data] = pending = Label(image);
            result.Add(new TextItem(await pending.ConfigureAwait(false))); result.Add(image);
        }
        return result;
    }

    /// <summary>The token budget: when the combined text exceeds it, the text items become one item that keeps the start and
    /// end, images follow it, and the full text is written to a temp file.</summary>
    private static async Task<(List<Item> Items, string? FullOutputPath)> TruncateOutputAsync(List<Item> items, long maxTokens)
    {
        var texts = items.OfType<TextItem>().Select(item => item.Text).ToList();
        var combined = string.Join("\n", texts);
        var budget = maxTokens * CharsPerToken;
        if (texts.Count == 0 || combined.Length <= budget) return (items, null);
        var headChars = (int)(budget / 2); var tailChars = (int)(budget - headChars);
        var removed = combined.Length - headChars - tailChars;
        var head = combined[..headChars]; var tail = tailChars > 0 ? combined[^tailChars..] : "";
        var text = $"Warning: truncated output (original token count: {Ceiling(combined.Length)})\nTotal output lines: {combined.Split('\n').Length}\n\n{head}…{Ceiling(removed)} tokens truncated…{tail}";
        string? path = null;
        try
        {
            path = await WriteOutputFile("pi-codemode", ".txt", Encoding.UTF8.GetBytes(combined), CancellationToken.None).ConfigureAwait(false);
            text += $"\n\n[Full output: {path} (read with offset/limit)]";
        }
        catch (Exception error) { text += $"\n\n[Could not save the full output: {error.Message}]"; }
        return ([new TextItem(text), .. items.OfType<ImageItem>()], path);
        static long Ceiling(long characters) => (characters + CharsPerToken - 1) / CharsPerToken;
    }

    private static JsonObject CombineUsage(JsonObject? first, JsonData second)
    {
        var next = (JsonObject)JsonNode.Parse(second.ToString())!;
        if (first is null) return next;
        return Sum(first, next);
        static JsonObject Sum(JsonObject left, JsonObject right)
        {
            var result = new JsonObject();
            foreach (var key in left.Select(pair => pair.Key).Concat(right.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal))
            {
                var a = left[key]; var b = right[key];
                if (a is JsonObject objectA || b is JsonObject) result[key] = Sum(a as JsonObject ?? [], b as JsonObject ?? []);
                else result[key] = (a?.GetValue<double>() ?? 0) + (b?.GetValue<double>() ?? 0);
            }
            return result;
        }
    }

    // --- searchTools(), describeTool(), describeNamespace() -------------------------------------------------------------

    /// <summary>Whether <paramref name="query"/> names the namespace: its name, its identifier, or the part after its last
    /// <c>__</c> in either form.</summary>
    private static bool IsNamespaceName(string ns, string query)
    {
        var id = CodemodeIdentifier.ToIdentifier(ns); var queryId = CodemodeIdentifier.ToIdentifier(query);
        static string? Suffix(string name) => name.Contains("__", StringComparison.Ordinal) ? name[(name.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : null;
        return ns == query || id == queryId || Suffix(ns) == query || Suffix(id) == queryId;
    }

    private static JsonElement[] Spread(JsonData? arguments) =>
        arguments?.Value is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray()] : [];
    private static JsonElement At(JsonElement[] arguments, int index) => index < arguments.Length ? arguments[index] : default;
    private static bool Missing(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
    private static JsonData Json(object value) => JsonData.Parse(JsonSerializer.Serialize(value, Relaxed));

    private static IEnumerable<CodemodeTool> DiscoveryGlobals(ImmutableArray<CodemodeNestedTool> tools, IReadOnlyDictionary<string, string> samples)
    {
        var ranker = new Bm25Ranker();
        object Entry(string name) => new { name = CodemodeIdentifier.ToIdentifier(name), description = samples.GetValueOrDefault(name) ?? "" };
        yield return new("searchTools", (arguments, _) =>
        {
            var args = Spread(arguments); var query = At(args, 0); var options = At(args, 1);
            if (query.ValueKind != JsonValueKind.String) throw new InvalidOperationException("searchTools() expects a query string");
            var limitValue = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("limit", out var supplied) && !Missing(supplied) ? supplied : default;
            var limit = ToolSearch.DefaultLimit;
            if (limitValue.ValueKind != JsonValueKind.Undefined)
            {
                if (limitValue.ValueKind != JsonValueKind.Number || limitValue.GetDouble() is var number && (Math.Floor(number) != number || number <= 0))
                    throw new InvalidOperationException("searchTools() limit must be a positive integer");
                limit = limitValue.GetDouble() >= int.MaxValue ? int.MaxValue : (int)limitValue.GetDouble();
            }
            var nsValue = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("namespace", out var given) ? given : default;
            if (!Missing(nsValue) && nsValue.ValueKind != JsonValueKind.String) throw new InvalidOperationException("searchTools() namespace must be a string");
            var ns = nsValue.ValueKind == JsonValueKind.String ? nsValue.GetString()! : "";
            var documents = tools.Where(tool => ns.Length == 0 || tool.Namespace is { } toolNamespace && IsNamespaceName(toolNamespace.Name, ns))
                .Select(tool => ToolSearch.CreateDocument(tool.Name, tool.Description, tool.Parameters.Value, tool.Namespace)).ToList();
            return ValueTask.FromResult<JsonData?>(Json(ranker.Rank(query.GetString()!, documents, limit).Select(match => Entry(match.Name)).ToArray()));
        }) { Spread = true };
        yield return new("describeTool", (arguments, _) =>
        {
            var name = At(Spread(arguments), 0);
            if (name.ValueKind != JsonValueKind.String) throw new InvalidOperationException("describeTool() expects a tool name");
            var tool = tools.FirstOrDefault(candidate => candidate.Name == name.GetString() || CodemodeIdentifier.ToIdentifier(candidate.Name) == name.GetString());
            return ValueTask.FromResult(tool is null ? null : JsonData.Parse(Js.Stringify(samples[tool.Name])));
        }) { Spread = true };
        yield return new("describeNamespace", (arguments, _) =>
        {
            var name = At(Spread(arguments), 0);
            if (name.ValueKind != JsonValueKind.String) throw new InvalidOperationException("describeNamespace() expects a namespace name");
            PiSharp.Contracts.ToolNamespace? found = null; var names = new List<string>();
            foreach (var tool in tools)
            {
                if (tool.Namespace is not { } ns || !IsNamespaceName(ns.Name, name.GetString()!)) continue;
                found ??= ns; names.Add(CodemodeIdentifier.ToIdentifier(tool.Name));
            }
            if (found is null) return ValueTask.FromResult<JsonData?>(null);
            var result = new JsonObject { ["name"] = found.Name };
            if (!string.IsNullOrEmpty(found.Description)) result["description"] = found.Description;
            if (!string.IsNullOrEmpty(found.Instructions)) result["instructions"] = found.Instructions;
            result["tools"] = new JsonArray([.. names.Select(item => (JsonNode)item)]);
            return ValueTask.FromResult<JsonData?>(JsonData.Parse(result.ToJsonString(Relaxed)));
        }) { Spread = true };
    }

    // --- models.* -----------------------------------------------------------------------------------------------------

    private static string WithArticle(string word) => (word.Length > 0 && "aeiou".Contains(word[0]) ? "an " : "a ") + word;

    /// <summary>A script value in an error message: <c>undefined</c>, <c>a string</c>, <c>an array</c>, or its keys.</summary>
    private static string DescribeValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined => "undefined",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => value.GetArrayLength() == 0 ? "an empty array" : "an array",
        JsonValueKind.Object => ModelOperationJson.ObjectEntries(value) is var keys && keys.Length == 0 ? "{}"
            : $"{{ {string.Join(", ", keys.Take(6).Select(key => key.Key))}{(keys.Length > 6 ? ", ..." : "")} }}",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        _ => "a boolean"
    };

    private static ModelType ToModelType(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Array.IndexOf(ModelTypes, value.GetString()) is var index and >= 0
            ? (ModelType)index : throw new InvalidOperationException($"Unknown model type {(value.ValueKind == JsonValueKind.Undefined ? "undefined" : Js.Stringify(value))}. Use \"chat\", \"image\", or \"classifier\".");

    private static string? ToProvider(JsonElement value)
    {
        if (Missing(value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("provider must be a string");
        return value.GetString();
    }

    private const string ClassifierContextShape = "{ state: { ... }, images?: [{ type: \"image\", data: <base64>, mimeType }], questions: { <id>: { type: \"choice\", instructions, criteria: { <label>: <meaning> } } | { type: \"score\", instructions, criteria: [<lowest level>, ..., <highest level>] } | { type: \"bool\", instructions, criteria: { true: <meaning>, false: <meaning> } } } }";

    private static bool IsRecord(JsonElement value) => value.ValueKind == JsonValueKind.Object;
    private static bool Has(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property);
    }
    private static bool StringField(JsonElement value, string name) => Has(value, name, out var field) && field.ValueKind == JsonValueKind.String;
    private static bool IsImageBlock(JsonElement value) => IsRecord(value) && Has(value, "type", out var type) && type.ValueKind == JsonValueKind.String &&
        type.GetString() == "image" && StringField(value, "data") && StringField(value, "mimeType");

    /// <summary>Checks a script's classifier context, so mistakes fail with the expected shape instead of a provider error.</summary>
    private static ClassifierContext CheckClassifierContext(JsonElement context)
    {
        Exception Fail(string problem) => new InvalidOperationException(
            $"models.classify() {problem}. Expected context: {ClassifierContextShape}. See \"Classify\" in {CodemodeToolDefinition.DocsPath}.");
        if (!IsRecord(context)) throw Fail($"expects a context object as its second argument, got {DescribeValue(context)}");
        Has(context, "state", out var state);
        if (!IsRecord(state)) throw Fail($"context.state must be an object, got {DescribeValue(state)}");
        if (Has(context, "images", out var images))
        {
            if (images.ValueKind != JsonValueKind.Array) throw Fail($"context.images must be an array, got {DescribeValue(images)}");
            var index = 0;
            foreach (var image in images.EnumerateArray())
            {
                if (!IsImageBlock(image)) throw Fail($"context.images[{index}] must be an image block, got {DescribeValue(image)}");
                index++;
            }
        }
        Has(context, "questions", out var questions);
        if (!IsRecord(questions) || !questions.EnumerateObject().Any())
            throw Fail($"context.questions must map question IDs to questions, got {DescribeValue(questions)}");
        static bool Strings(IEnumerable<JsonElement> values) { var list = values.ToList(); return list.Count > 0 && list.All(value => value.ValueKind == JsonValueKind.String); }
        foreach (var (id, question) in ModelOperationJson.ObjectEntries(questions))
        {
            var at = "context.questions." + id;
            if (!IsRecord(question)) throw Fail($"{at} must be a question object, got {DescribeValue(question)}");
            if (!StringField(question, "instructions")) throw Fail($"{at}.instructions must be a string");
            Has(question, "criteria", out var criteria); Has(question, "type", out var type);
            var kind = type.ValueKind == JsonValueKind.String ? type.GetString() : null;
            if (kind == "choice")
            {
                if (!IsRecord(criteria) || !Strings(ModelOperationJson.ObjectEntries(criteria).Select(entry => entry.Value)))
                    throw Fail($"{at} is a \"choice\" question, so criteria must map each label to its meaning");
            }
            else if (kind == "score")
            {
                if (criteria.ValueKind != JsonValueKind.Array || !Strings(criteria.EnumerateArray()))
                    throw Fail($"{at} is a \"score\" question, so criteria must list the levels as strings, lowest first");
            }
            else if (kind == "bool")
            {
                if (!IsRecord(criteria) || !StringField(criteria, "true") || !StringField(criteria, "false"))
                    throw Fail($"{at} is a \"bool\" question, so criteria must be {{ true: string, false: string }}");
            }
            else throw Fail($"{at}.type must be \"choice\", \"score\", or \"bool\", got {(type.ValueKind == JsonValueKind.Undefined ? "undefined" : Js.Stringify(type))}");
        }
        try { return ModelOperationJson.ParseClassifierContext(context); }
        catch (FormatException error) { throw Fail(error.Message.TrimEnd('.')); }
    }

    /// <summary>Checks a script's image context, so mistakes such as <c>{ prompt }</c> fail with the expected shape.</summary>
    private static ImagesContext CheckImagesContext(JsonElement context)
    {
        Exception Fail(string problem) => new InvalidOperationException(
            $"models.generateImages() {problem}. Expected context: {{ input: [{{ type: \"text\", text: <prompt> }}, ...optional {{ type: \"image\", data: <base64>, mimeType }} references] }}. See \"Generate images\" in {CodemodeToolDefinition.DocsPath}.");
        if (!IsRecord(context)) throw Fail($"expects a context object as its second argument, got {DescribeValue(context)}");
        Has(context, "input", out var input);
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0)
            throw Fail($"context.input must be a non-empty array of blocks, got {DescribeValue(input)}");
        var index = 0;
        foreach (var block in input.EnumerateArray())
        {
            var text = IsRecord(block) && Has(block, "type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text" && StringField(block, "text");
            if (!text && !IsImageBlock(block)) throw Fail($"context.input[{index}] must be a text or image block, got {DescribeValue(block)}");
            index++;
        }
        try { return ModelOperationJson.ParseImagesContext(context); }
        catch (FormatException error) { throw Fail(error.Message.TrimEnd('.')); }
    }

    /// <summary>Runs at most <paramref name="limit"/> calls at once, in call order.</summary>
    private sealed class Limiter(int limit)
    {
        private readonly object gate = new();
        private readonly Queue<TaskCompletionSource> waiting = new();
        private int active;
        public async Task<T> Run<T>(Func<Task<T>> run)
        {
            TaskCompletionSource? turn = null;
            lock (gate) { if (active >= limit) waiting.Enqueue(turn = new(TaskCreationOptions.RunContinuationsAsynchronously)); else active++; }
            if (turn is not null) await turn.Task.ConfigureAwait(false);
            try { return await run().ConfigureAwait(false); }
            finally
            {
                lock (gate) { if (waiting.TryDequeue(out var next)) next.SetResult(); else active--; }
            }
        }
    }

    private static IEnumerable<CodemodeTool> ModelGlobals(ICodemodeModelRuntime models, string toolCallId, List<NestedCall> calls, object gate,
        Func<ValueTask> publish, Action<JsonData> addUsage, Action<int> addGeneratedImages)
    {
        var limiter = new Limiter(MaxConcurrentModelCalls);
        var callCount = 0;
        // Resolve the script's model by provider and id only, check the context, then run the call as a nested call row.
        async Task<JsonData> RunModelCall<TContext>(string name, ModelType type, JsonElement[] args, Func<JsonElement, TContext> check,
            Func<JsonData, TContext, Task<(JsonData Result, ModelOperationStopReason Stop, string? Error, TokenUsage? Usage)>> run)
        {
            var typeName = type == ModelType.Image ? "image" : "classifier";
            var listHint = $"List the {typeName} models you can use with models.getAvailableOfType(\"{typeName}\").";
            var model = At(args, 0);
            if (!IsRecord(model) || !StringField(model, "provider") || !StringField(model, "id"))
            {
                // undefined arrives as null: spread arguments cross the sandbox as a JSON array.
                var undefinedHint = Missing(model) ? " models.getModelOfType() returns undefined for an unknown provider or id." : "";
                throw new InvalidOperationException($"{name}() expects {WithArticle(typeName)} model as its first argument, got {DescribeValue(model)}.{undefinedHint} {listHint}");
            }
            var provider = model.GetProperty("provider").GetString()!; var id = model.GetProperty("id").GetString()!;
            var reference = $"{provider}/{id}";
            if (models.GetModelOfType(type, provider, id) is not { } resolved)
            {
                var actual = new[] { ModelType.Chat, ModelType.Image, ModelType.Classifier }.Where(other => other != type)
                    .Select(other => (ModelType?)other).FirstOrDefault(other => models.GetModelOfType(other!.Value, provider, id) is not null);
                throw new InvalidOperationException(actual is { } found
                    ? $"\"{reference}\" is {WithArticle(ModelTypes[(int)found])} model, not {WithArticle(typeName)} model. {listHint}"
                    : $"Unknown {typeName} model \"{reference}\". {listHint}");
            }
            var context = check(At(args, 1));
            var record = new NestedCall($"{toolCallId}/{name}/{Interlocked.Increment(ref callCount)}", name,
                $"{Field(resolved, "provider") ?? provider}/{Field(resolved, "id") ?? id}");
            lock (gate) calls.Add(record);
            await publish().ConfigureAwait(false);
            var callStarted = Stopwatch.StartNew();
            var outcome = await limiter.Run(() => run(resolved, context)).ConfigureAwait(false);
            lock (gate)
            {
                record.DurationMs = callStarted.Elapsed.TotalMilliseconds;
                record.Status = outcome.Stop == ModelOperationStopReason.Stop ? "ok" : outcome.Stop == ModelOperationStopReason.Aborted ? "cancelled" : "error";
                if (!string.IsNullOrEmpty(outcome.Error)) record.Error = TruncateText(outcome.Error, ErrorPreviewChars);
                if (outcome.Usage is { } usage) record.Cost = (double)usage.Cost.Total;
            }
            if (outcome.Usage is { } reported) addUsage(ModelOperationJson.WriteUsage(reported));
            await publish().ConfigureAwait(false);
            return outcome.Result;
        }
        static string? Field(JsonData model, string name) =>
            model.Value.ValueKind == JsonValueKind.Object && model.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static JsonData List(ImmutableArray<JsonData> entries) => JsonData.Parse("[" + string.Join(",", entries.Select(entry => entry.ToString())) + "]");

        yield return new("models.getModelsOfType", (arguments, _) =>
        {
            var args = Spread(arguments);
            return ValueTask.FromResult<JsonData?>(List(models.GetModelsOfType(ToModelType(At(args, 0)), ToProvider(At(args, 1)))));
        }) { Spread = true };
        yield return new("models.getAvailableOfType", async (arguments, token) =>
        {
            var args = Spread(arguments);
            return List(await models.GetAvailableOfTypeAsync(ToModelType(At(args, 0)), ToProvider(At(args, 1)), token).ConfigureAwait(false));
        }) { Spread = true };
        yield return new("models.getModelOfType", (arguments, _) =>
        {
            var args = Spread(arguments);
            var provider = At(args, 1); var id = At(args, 2);
            if (provider.ValueKind != JsonValueKind.String || id.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"models.getModelOfType(type, provider, id) expects three strings, got ({string.Join(", ", args.Select(DescribeValue))}). The provider and the id are separate arguments, for example models.getModelOfType(\"classifier\", \"typesafe\", \"jev-latest\").");
            return ValueTask.FromResult(models.GetModelOfType(ToModelType(At(args, 0)), provider.GetString()!, id.GetString()!));
        }) { Spread = true };
        yield return new("models.classify", async (arguments, token) => await RunModelCall("models.classify", ModelType.Classifier, Spread(arguments),
            CheckClassifierContext, async (resolved, context) =>
            {
                var result = await models.ClassifyAsync(resolved, context, token).ConfigureAwait(false);
                return (ModelOperationJson.WriteClassifierResult(result), result.StopReason, result.ErrorMessage, result.Usage);
            }).ConfigureAwait(false)) { Spread = true };
        yield return new("models.generateImages", async (arguments, token) => await RunModelCall("models.generateImages", ModelType.Image, Spread(arguments),
            CheckImagesContext, async (resolved, context) =>
            {
                var result = await models.GenerateImagesAsync(resolved, context, token).ConfigureAwait(false);
                addGeneratedImages(result.Output.IsDefault ? 0 : result.Output.Count(block => block is ImageContent));
                return (ModelOperationJson.WriteAssistantImages(result), result.StopReason, result.ErrorMessage, result.Usage);
            }).ConfigureAwait(false)) { Spread = true };
    }
}
