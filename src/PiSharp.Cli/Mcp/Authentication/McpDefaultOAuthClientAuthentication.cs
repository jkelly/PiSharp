using System.Collections.Immutable;
using System.Net.Http;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Cli.Mcp.Authentication;

/// <summary>Finite callback-lifetime proposal. The optional hook replaces default authentication;
/// retained proposal references refuse mutation/read after that actual callback settles.</summary>
public sealed class McpDefaultOAuthClientAuthentication
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, string>> form;
    private readonly int maximumBytes;
    private bool retired;
    public Uri Endpoint { get; }
    public McpOAuthAdmittedClient Client { get; }
    public JsonData? Metadata { get; }
    internal McpDefaultOAuthClientAuthentication(Uri endpoint, McpOAuthAdmittedClient client,
        JsonData? metadata, IEnumerable<KeyValuePair<string, string>> fields, int bound)
    {
        Endpoint = endpoint; Client = client; Metadata = metadata; maximumBytes = bound;
        form = fields.ToList(); headers.Add("Accept", "application/json");
        headers.Add("Content-Type", "application/x-www-form-urlencoded");
    }
    private void Active() => ObjectDisposedException.ThrowIf(retired, this);
    public string? HeaderGet(string name) { lock (gate) { Active(); return headers.GetValueOrDefault(name); } }
    public void HeaderSet(string name, string value)
    {
        lock (gate)
        {
            Active(); ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(value);
            if (value.Any(character => character > 255 || character is '\r' or '\n' or '\0')) throw new ArgumentException("HTTP byte-string header value required.");
            using var probe = new HttpRequestMessage();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            { using var content = new ByteArrayContent([]); content.Headers.Add(name, value); }
            else probe.Headers.Add(name, value);
            var previous = headers.GetValueOrDefault(name); headers[name] = value;
            try { Bound(); } catch { if (previous is null) headers.Remove(name); else headers[name] = previous; throw; }
        }
    }
    public void HeaderDelete(string name) { lock (gate) { Active(); headers.Remove(name); } }
    public void HeaderAppend(string name, string value)
    { lock (gate) { Active(); var current = headers.GetValueOrDefault(name); HeaderSet(name, current is null ? value : current + ", " + value); } }
    public string? FormGet(string name) { lock (gate) { Active(); name = Usv(name); return form.FirstOrDefault(pair => pair.Key == name).Value; } }
    public ImmutableArray<string> FormGetAll(string name)
    { lock (gate) { Active(); name = Usv(name); return form.Where(pair => pair.Key == name).Select(pair => pair.Value).ToImmutableArray(); } }
    public void FormSet(string name, string value)
    {
        lock (gate)
        {
            Active(); ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(value);
            name = Usv(name); value = Usv(value);
            var saved = form.ToArray(); var first = form.FindIndex(pair => pair.Key == name);
            form.RemoveAll(pair => pair.Key == name); form.Insert(first < 0 ? form.Count : first, new(name, value));
            try { Bound(); } catch { form.Clear(); form.AddRange(saved); throw; }
        }
    }
    public void FormDelete(string name) { lock (gate) { Active(); name = Usv(name); form.RemoveAll(pair => pair.Key == name); } }
    public void FormAppend(string name, string value)
    {
        lock (gate)
        {
            Active(); ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(value);
            form.Add(new(Usv(name), Usv(value))); try { Bound(); } catch { form.RemoveAt(form.Count - 1); throw; }
        }
    }
    private static string Usv(string value) => System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(value));
    private void Bound()
    {
        long count = 0;
        foreach (var pair in headers.Concat(form))
            count += System.Text.Encoding.UTF8.GetByteCount(pair.Key) + (long)System.Text.Encoding.UTF8.GetByteCount(pair.Value);
        if (count > maximumBytes) throw new ArgumentException("OAuth client authentication proposal exceeds its admitted bound.");
    }
    internal (ImmutableDictionary<string, string>, KeyValuePair<string, string>[]) Snapshot()
    { lock (gate) { Active(); Bound(); return (headers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase), form.ToArray()); } }
    internal void Retire() { lock (gate) retired = true; }
}
