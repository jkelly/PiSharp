using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Mcp.Runtime;

namespace PiSharp.Extensions.Runtime.Mcp.Resources;

/// <summary>Semantic leaf only. Hosts register prepared callbacks through their existing registry;
/// these public names do not confer genuine discovery identity or grant transport admission.</summary>
public sealed class McpResourceTools
{
    public const string ListResources = "list_mcp_resources", ListTemplates = "list_mcp_resource_templates", ReadResource = "read_mcp_resource";
    private readonly Func<IReadOnlyList<McpResourceServer>> servers;
    private readonly McpResourceOutputSaver save;
    private readonly McpResourceLimits limits;
    private readonly AsyncLocal<bool> saving = new();
    private readonly AsyncLocal<CallbackEvidence?> evidence = new();
    private sealed class CallbackEvidence
    {
        private readonly object gate = new(); private readonly List<McpResourceCallbackException> failures = [];
        internal void Add(McpResourceCallbackException error) { lock (gate) failures.Add(error); }
        internal ImmutableArray<McpResourceCallbackException> Snapshot() { lock (gate) return failures.ToImmutableArray(); }
    }
    private sealed class RetentionBudget(McpResourceLimits limits)
    {
        private readonly object gate = new(); private long bytes; private long count;
        internal void Charge(List<Dictionary<string, object?>> items)
        {
            var added = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(items));
            lock (gate)
            {
                bytes = checked(bytes + added); count = checked(count + items.Count);
                if (bytes > limits.MaximumRetainedBytes || count > limits.MaximumItems)
                    throw new McpRuntimeProtocolException("MCP aggregate resource retention limit exceeded");
            }
        }
    }
    public McpResourceTools(Func<IReadOnlyList<McpResourceServer>> servers, McpResourceOutputSaver save,
        McpResourceLimits? limits = null)
    {
        this.servers = servers ?? throw new ArgumentNullException(nameof(servers));
        this.save = save ?? throw new ArgumentNullException(nameof(save)); this.limits = limits ?? new();
        if (save.GetInvocationList().Length != 1) throw new ArgumentException("Exactly one admitted output saver required.", nameof(save));
        if (this.limits is not { MaximumPages: > 0 and <= 1000, MaximumItems: > 0,
            MaximumResponseBytes: > 0, MaximumRetainedBytes: > 0, MaximumModelTextBytes: >= 4 })
            throw new ArgumentException("Finite positive resource limits required.", nameof(limits));
    }
    public async Task<McpResourceResult> ExecuteAsync(string tool, JsonData arguments,
        IExtensionToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        if (saving.Value) throw new InvalidOperationException("Resource output callback cannot reenter its owning resource operation.");
        var previous = evidence.Value; var current = new CallbackEvidence(); evidence.Value = current;
        try
        {
            var result = await ExecuteCoreAsync(tool, arguments, context, cancellationToken).ConfigureAwait(false);
            return result with { OriginalCallbackFailures = current.Snapshot() };
        }
        catch (OperationCanceledException cancellation)
        {
            var failures = current.Snapshot();
            if (failures.Length != 0) throw new AggregateException(failures.Cast<Exception>().Append(cancellation));
            throw;
        }
        catch (Exception failure)
        {
            var retained = current.Snapshot().Where(error => !ReferenceEquals(error, failure)).Cast<Exception>().ToArray();
            if (retained.Length != 0) throw new AggregateException(retained.Append(failure));
            throw;
        }
        finally { evidence.Value = previous; }
    }
    private async Task<McpResourceResult> ExecuteCoreAsync(string tool, JsonData arguments,
        IExtensionToolInvocationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(context);
        if (saving.Value) throw new InvalidOperationException("Resource output callback cannot reenter its owning resource operation.");
        if (tool is not (ListResources or ListTemplates or ReadResource)) throw new ArgumentException("Unknown resource tool.", nameof(tool));
        if (arguments.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Resource arguments object required.", nameof(arguments));
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.OperationCancellationToken,
            context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken);
        var token = owned.Token; token.ThrowIfCancellationRequested();
        var identity = new McpInvocationIdentity(context.OwnerId, context.OwnerGeneration, context.SessionGeneration, context.ToolCallId, context.ParentToolCallId);
        // Server generation is a separate domain from extension owner/session generation. The request owner validates it.
        var captured = servers().ToArray();
        if (captured.Length > limits.MaximumItems || captured.Any(s => string.IsNullOrEmpty(s.Name) || s.Generation <= 0 || !double.IsFinite(s.TimeoutMilliseconds) || s.TimeoutMilliseconds <= 0 || s.Request is null || s.Request.GetInvocationList().Length != 1) ||
            captured.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != captured.Length)
            throw new InvalidOperationException("Unique admitted resource server snapshots required.");
        var name = Argument(arguments.Value, "server");
        McpResourceServer Find() => captured.FirstOrDefault(s => s.Name == name) ?? throw new InvalidOperationException(
            $"MCP server \"{name}\" has no resources" + (captured.Length == 0 ? "" : ". Servers with resources: " + string.Join(", ", captured.Select(s => s.Name))));
        if (tool == ReadResource)
        {
            var uri = Argument(arguments.Value, "uri");
            if (name is null || uri is null) throw new ArgumentException("server and uri must be provided");
            var response = await Request(Find(), "resources/read", Json(new { uri }), identity, token).ConfigureAwait(false);
            if (!response.Value.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
                throw new McpRuntimeProtocolException("Invalid MCP resources/read contents");
            if (contents.GetArrayLength() > limits.MaximumItems) throw new McpRuntimeProtocolException("Resource content item limit exceeded");
            var model = ImmutableArray.CreateBuilder<JsonData>(); var script = new List<Dictionary<string, object?>>();
            foreach (var item in contents.EnumerateArray())
            {
                token.ThrowIfCancellationRequested(); ValidateContent(item);
                script.Add(Fields(item, "_meta"));
                if (contents.GetArrayLength() > 1) model.Add(Text(item.GetProperty("uri").GetString() + ":"));
                model.Add(await ConvertContent(item, identity, token).ConfigureAwait(false));
            }
            if (model.Count == 0) model.Add(Text($"Resource {uri} is empty."));
            return await Result(name, tool, Json(new { server = name, uri, contents = script }), model.ToImmutable(), identity, token).ConfigureAwait(false);
        }
        var key = tool == ListResources ? "resources" : "resourceTemplates";
        var method = tool == ListResources ? "resources/list" : "resources/templates/list";
        var cursor = Argument(arguments.Value, "cursor");
        if (name is null && cursor is not null) throw new ArgumentException("cursor can only be used when a server is specified");
        Dictionary<string, object?> payload;
        if (name is not null)
        {
            var page = await Page(Find(), method, key, cursor, identity, token).ConfigureAwait(false);
            payload = new() { ["server"] = name, [key] = page.Items };
            if (page.Next is not null) payload["nextCursor"] = page.Next;
        }
        else
        {
            // Start all originals and await every settlement before propagating owner cancellation.
            var ordered = captured.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
            var retention = new RetentionBudget(limits);
            var originals = ordered.Select(async server =>
            {
                try { return (Items: await All(server, method, key, identity, token, retention).ConfigureAwait(false), Error: (Exception?)null); }
                catch (Exception error) { return (Items: new List<Dictionary<string, object?>>(), Error: error); }
            }).ToArray();
            var results = await Task.WhenAll(originals).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            var items = results.SelectMany(r => r.Items).ToList();
            CheckRetained(items);
            payload = new() { [key] = items };
            var errors = results.Select((r, i) => (r.Error, ordered[i].Name)).Where(r => r.Error is not null)
                .Select(r => new { server = r.Name, error = r.Error!.Message }).ToArray();
            if (errors.Length > 0) payload["errors"] = errors;
        }
        var structured = Json(payload);
        return await Result(name ?? "", tool, structured, [Text(structured.ToString())], identity, token).ConfigureAwait(false);
    }
    private async Task<JsonData> Request(McpResourceServer server, string method, JsonData? parameters,
        McpInvocationIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var response = await JoinCallback("request", () => server.Request(server.Generation, method, parameters,
            new(server.TimeoutMilliseconds) { MaximumResponseBytes = limits.MaximumResponseBytes, InvocationIdentity = identity }, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (response is null || Encoding.UTF8.GetByteCount(response.ToString()) > limits.MaximumResponseBytes || response.Value.ValueKind != JsonValueKind.Object)
            throw new McpRuntimeProtocolException("Invalid or oversized MCP resource response");
        return response;
    }
    private async Task<(List<Dictionary<string, object?>> Items, string? Next)> Page(McpResourceServer server,
        string method, string key, string? cursor, McpInvocationIdentity identity, CancellationToken token)
    {
        var response = await Request(server, method, cursor is null ? null : Json(new { cursor }), identity, token).ConfigureAwait(false);
        if (!response.Value.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new McpRuntimeProtocolException("Invalid MCP resource listing");
        if (array.GetArrayLength() > limits.MaximumItems) throw new McpRuntimeProtocolException("Resource listing item limit exceeded");
        var items = new List<Dictionary<string, object?>>();
        foreach (var item in array.EnumerateArray())
        {
            var uriKey = key == "resources" ? "uri" : "uriTemplate";
            if (item.ValueKind != JsonValueKind.Object || !String(item, uriKey, out var uri) || !String(item, "name", out _))
                throw new McpRuntimeProtocolException("Invalid MCP resource entry");
            String(item, "mimeType", out var mime);
            if (uri!.StartsWith("ui://", StringComparison.Ordinal) || Regex.IsMatch(mime ?? "", ";\\s*profile\\s*=\\s*\"?mcp-app\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            var fields = new Dictionary<string, object?> { ["server"] = server.Name };
            foreach (var field in Fields(item, "_meta", "icons")) fields[field.Key] = field.Value;
            items.Add(fields);
        }
        CheckRetained(items);
        string? next = null;
        if (response.Value.TryGetProperty("nextCursor", out var nextValue))
        {
            if (nextValue.ValueKind != JsonValueKind.String) throw new McpRuntimeProtocolException("Invalid resource cursor");
            next = nextValue.GetString();
        }
        return (items, next);
    }
    private async Task<List<Dictionary<string, object?>>> All(McpResourceServer server, string method,
        string key, McpInvocationIdentity identity, CancellationToken token, RetentionBudget retention)
    {
        var items = new List<Dictionary<string, object?>>(); var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        for (var page = 0; page < limits.MaximumPages; page++)
        {
            var result = await Page(server, method, key, cursor, identity, token).ConfigureAwait(false);
            retention.Charge(result.Items);
            items.AddRange(result.Items); CheckRetained(items);
            if (result.Next is null) return items;
            cursor = result.Next;
            if (!cursors.Add(cursor)) throw new McpRuntimeProtocolException("MCP resource listing returned duplicate cursor");
        }
        throw new McpRuntimeProtocolException("MCP resource listing exceeded page limit");
    }
    private void CheckRetained(List<Dictionary<string, object?>> items)
    {
        if (items.Count > limits.MaximumItems || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(items)) > limits.MaximumRetainedBytes)
            throw new McpRuntimeProtocolException("MCP retained resource catalog limit exceeded");
    }
    private static void ValidateContent(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !String(item, "uri", out _) ||
            (!String(item, "text", out _) && !String(item, "blob", out _)) ||
            item.TryGetProperty("mimeType", out var mime) && mime.ValueKind != JsonValueKind.String)
            throw new McpRuntimeProtocolException("Invalid MCP resource content");
    }
    private async Task<JsonData> ConvertContent(JsonElement item, McpInvocationIdentity identity, CancellationToken token)
    {
        if (String(item, "text", out var text)) return Text(text!);
        var blob = item.GetProperty("blob").GetString()!; String(item, "mimeType", out var mime);
        if (mime?.StartsWith("image/", StringComparison.Ordinal) == true) return Json(new { type = "image", data = blob, mimeType = mime });
        byte[] data;
        try { data = Convert.FromBase64String(blob); } catch (FormatException error) { throw new McpRuntimeProtocolException("Invalid resource base64: " + error.Message); }
        var type = mime?.Split(';')[0].Trim().ToLowerInvariant();
        if (type is not null && (type.StartsWith("text/", StringComparison.Ordinal) || type == "application/json" || type.EndsWith("+json", StringComparison.Ordinal) || type.EndsWith("+xml", StringComparison.Ordinal)))
            return Text(Encoding.UTF8.GetString(data));
        var uri = item.GetProperty("uri").GetString()!;
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : uri;
        var match = Regex.Match(path, "\\.[A-Za-z0-9]{1,8}$", RegexOptions.CultureInvariant);
        try { return Text($"[Binary resource {uri} ({mime ?? "unknown type"}, {Size(data.Length)}) saved to {await Save(data, match.Success ? match.Value : ".bin", identity, token).ConfigureAwait(false)}]"); }
        catch (Exception error) when (error is not OperationCanceledException) { token.ThrowIfCancellationRequested(); return Text($"[Binary resource {uri} ({mime ?? "unknown type"}, {Size(data.Length)}) could not be saved: {error.Message}]"); }
    }
    private async Task<string> Save(ReadOnlyMemory<byte> data, string extension, McpInvocationIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); saving.Value = true;
        try { var path = await JoinCallback("output saver", () => save(data, extension, identity, token), token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return path; }
        finally { saving.Value = false; }
    }
    private async Task<T> JoinCallback<T>(string callback, Func<ValueTask<T>> invoke, CancellationToken token)
    {
        Task<T> original;
        try { original = invoke().AsTask(); }
        catch (Exception failure)
        {
            var retained = new McpResourceCallbackException(callback, null, failure);
            evidence.Value!.Add(retained); throw retained;
        }
        try { return await original.ConfigureAwait(false); }
        catch (Exception failure)
        {
            if (original.IsCanceled && token.IsCancellationRequested && failure is OperationCanceledException cancellation && cancellation.CancellationToken == token)
                throw;
            var retained = new McpResourceCallbackException(callback, original, (Exception?)original.Exception ?? failure);
            evidence.Value!.Add(retained); throw retained;
        }
    }
    private async Task<McpResourceResult> Result(string server, string tool, JsonData structured,
        ImmutableArray<JsonData> content, McpInvocationIdentity identity, CancellationToken token)
    {
        var text = string.Join("\n", content.Where(c => String(c.Value, "text", out _)).Select(c => c.Value.GetProperty("text").GetString()));
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= limits.MaximumModelTextBytes) return new(content, structured, server, tool);
        string? path = null; string where;
        try { path = await Save(bytes, ".txt", identity, token).ConfigureAwait(false); where = $"[Full output: {path} (read it with offset/limit)]"; }
        catch (Exception error) when (error is not OperationCanceledException) { token.ThrowIfCancellationRequested(); where = $"[Could not save the full output: {error.Message}]"; }
        var half = limits.MaximumModelTextBytes / 2;
        var start = Utf8Prefix(bytes, half); var end = Utf8Suffix(bytes, limits.MaximumModelTextBytes - half);
        var removed = 0;
        foreach (var rune in text[start.Length..(text.Length - end.Length)].EnumerateRunes()) removed++;
        var totalLines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
        var shown = $"Warning: truncated output (original token count: {(bytes.Length + 3) / 4})\nTotal output lines: {totalLines}\n\n{start}.{removed} chars truncated.{end}\n\n{where}";
        return new([Text(shown), .. content.Where(c => String(c.Value, "type", out var type) && type == "image")], structured, server, tool, path);
    }
    private static string Utf8Prefix(byte[] bytes, int length)
    { while (length > 0 && (bytes[length] & 0xc0) == 0x80) length--; return Encoding.UTF8.GetString(bytes, 0, length); }
    private static string Utf8Suffix(byte[] bytes, int length)
    { var start = bytes.Length - length; while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80) start++; return Encoding.UTF8.GetString(bytes, start, bytes.Length - start); }
    private static string Size(int bytes) => bytes < 1024 ? $"{bytes}B" : bytes < 1024 * 1024 ?
        (bytes / 1024d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "KB" :
        (bytes / (1024d * 1024)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "MB";
    private static Dictionary<string, object?> Fields(JsonElement value, params string[] omitted) => value.EnumerateObject()
        .Where(p => !omitted.Contains(p.Name, StringComparer.Ordinal)).ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    private static string? Argument(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw new ArgumentException(name + " must be a string");
        var text = property.GetString()!.Trim(); return text.Length == 0 ? null : text;
    }
    private static bool String(JsonElement value, string name, out string? text)
    { text = null; if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false; text = property.GetString(); return true; }
    private static JsonData Json(object value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private static JsonData Text(string text) => Json(new { type = "text", text });
}
