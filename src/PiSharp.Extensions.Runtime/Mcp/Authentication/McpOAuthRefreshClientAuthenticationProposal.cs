using System.Collections.Immutable;
using System.Text;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

internal sealed class McpOAuthRefreshClientAuthenticationProposal : IMcpOAuthRefreshClientAuthentication
    {
        private readonly object gate = new();
        private readonly Uri endpoint;
        private readonly McpOAuthAdmittedClient client;
        private readonly JsonData? metadata;
        private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<KeyValuePair<string, string>> form;
        private readonly int maximumBytes;
        private bool active = true;
        internal McpOAuthRefreshClientAuthenticationProposal(Uri endpoint, McpOAuthAdmittedClient client, JsonData? metadata,
            ImmutableDictionary<string, string> initialHeaders, List<KeyValuePair<string, string>> initialForm, int maximumBytes)
        {
            this.endpoint = endpoint; this.client = client; this.metadata = metadata; this.maximumBytes = maximumBytes;
            foreach (var pair in initialHeaders) headers.Add(HeaderName(pair.Key), HeaderValue(pair.Value));
            form = initialForm.Select(pair => new KeyValuePair<string, string>(UsvString(pair.Key), UsvString(pair.Value))).ToList(); CheckBounds();
        }
        public Uri Endpoint { get { lock (gate) { Ensure(); return endpoint; } } }
        public McpOAuthAdmittedClient Client { get { lock (gate) { Ensure(); return client; } } }
        public JsonData? Metadata { get { lock (gate) { Ensure(); return metadata; } } }
        public string? HeaderGet(string name) { lock (gate) { Ensure(); return headers.GetValueOrDefault(HeaderName(name)); } }
        public void HeaderSet(string name, string value)
        { lock (gate) { Ensure(); headers[HeaderName(name)] = HeaderValue(value); CheckBounds(); } }
        public void HeaderAppend(string name, string value)
        {
            lock (gate)
            {
                Ensure(); name = HeaderName(name); value = HeaderValue(value);
                headers[name] = headers.TryGetValue(name, out var current) ? current + ", " + value : value; CheckBounds();
            }
        }
        public void HeaderDelete(string name) { lock (gate) { Ensure(); headers.Remove(HeaderName(name)); } }
        public string? FormGet(string name)
        { name = UsvString(name); lock (gate) { Ensure(); return form.FirstOrDefault(pair => pair.Key == name).Value; } }
        public ImmutableArray<string> FormGetAll(string name)
        { name = UsvString(name); lock (gate) { Ensure(); return form.Where(pair => pair.Key == name).Select(pair => pair.Value).ToImmutableArray(); } }
        public void FormSet(string name, string value)
        {
            name = UsvString(name); value = UsvString(value);
            lock (gate)
            {
                Ensure(); var index = form.FindIndex(pair => pair.Key == name);
                if (index < 0) form.Add(new(name, value));
                else
                {
                    form[index] = new(name, value);
                    for (var duplicate = form.Count - 1; duplicate > index; duplicate--) if (form[duplicate].Key == name) form.RemoveAt(duplicate);
                }
                CheckBounds();
            }
        }
        public void FormAppend(string name, string value)
        { name = UsvString(name); value = UsvString(value); lock (gate) { Ensure(); form.Add(new(name, value)); CheckBounds(); } }
        public void FormDelete(string name)
        { name = UsvString(name); lock (gate) { Ensure(); form.RemoveAll(pair => pair.Key == name); } }
        private static string UsvString(string value)
        { ArgumentNullException.ThrowIfNull(value); return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value)); }
        internal (ImmutableDictionary<string, string>, List<KeyValuePair<string, string>>) Snapshot()
        {
            lock (gate)
            {
                Ensure(); CheckBounds(); var snapshot = (headers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase), new List<KeyValuePair<string, string>>(form));
                active = false; headers.Clear(); form.Clear(); return snapshot;
            }
        }
        internal void Retire() { lock (gate) { active = false; headers.Clear(); form.Clear(); } }
        private void Ensure() => ObjectDisposedException.ThrowIf(!active, this);
        private void CheckBounds()
        {
            if (headers.Count > 256 || form.Count > 256 ||
                headers.Sum(pair => (long)Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value)) +
                form.Sum(pair => (long)Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value)) > maximumBytes)
                throw new McpOAuthProtocolException("client_auth_limit", "Client authentication proposal exceeds admitted byte/entry bound.");
        }
        private static string HeaderName(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (name.Length == 0 || name.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z') && !"!#$%&'*+-.^_`|~".Contains(c)))
                throw new ArgumentException("HTTP header token name required.", nameof(name));
            return name.ToLowerInvariant();
        }
        private static string HeaderValue(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Any(c => c is '\r' or '\n' or '\0' || c > 255)) throw new ArgumentException("HTTP byte-string header value required.", nameof(value));
            return value.Trim(' ', '\t');
        }
        public override string ToString() => nameof(McpOAuthRefreshClientAuthenticationProposal);
    }
