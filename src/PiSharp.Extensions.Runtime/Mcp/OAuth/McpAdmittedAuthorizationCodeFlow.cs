using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.OAuth;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.OAuth;

/// <summary>Required affine cancellation-owner admission. After-await cancellation that can invoke
/// dependency callbacks must use this hook and join the returned original. Bypassing it is outside admission.</summary>
public sealed class McpAuthorizationCodeCancellationAdmission
{
    private readonly object gate = new();
    private McpAdmittedAuthorizationCodeFlow? owner;
    internal void Bind(McpAdmittedAuthorizationCodeFlow value)
    { lock (gate) { if (owner is not null) throw new InvalidOperationException("Cancellation admission is affine."); owner = value; } }
    private McpAdmittedAuthorizationCodeFlow Capture()
    { lock (gate) return owner ?? throw new InvalidOperationException("Cancellation admission is not bound."); }
    public void Cancel(CancellationTokenSource borrowed) => Capture().CancelOwned(borrowed);
    public Task CancelAsync(CancellationTokenSource borrowed) => Capture().CancelOwnedAsync(borrowed);
}

/// <summary>Bounded original startAuthorization/exchangeAuthorizationCode mapping with admitted client.
/// Serial operations and joined close; no discovery, registration, browser acquisition, state checking or invalid-grant retry.</summary>
public sealed class McpAdmittedAuthorizationCodeFlow : IAsyncDisposable
{
    private sealed class Frame(McpAdmittedAuthorizationCodeFlow owner, Frame? parent, Physical? ancestry)
    { internal readonly McpAdmittedAuthorizationCodeFlow Owner = owner; internal readonly Frame? Parent = parent; internal readonly Physical? Ancestry = ancestry; internal volatile bool Active = true; }
    private sealed class Physical(McpAdmittedAuthorizationCodeFlow owner, Frame? logical, Physical? parent)
    { internal readonly McpAdmittedAuthorizationCodeFlow Owner = owner; internal readonly Frame? Logical = logical; internal readonly Physical? Parent = parent; internal volatile bool Active = true; }
    private static readonly AsyncLocal<Frame?> logical = new();
    [ThreadStatic] private static Physical? physical;
    private readonly object gate = new();
    private readonly McpAuthorizationCodeDependencies dependencies;
    private readonly CancellationTokenSource lifetime = new();
    private readonly int maximumBytes;
    private Task? active, close;
    private bool closing;

    public McpAdmittedAuthorizationCodeFlow(McpAuthorizationCodeDependencies explicitlyAdmitted,
        McpAuthorizationCodeCancellationAdmission cancellationAdmission, int maximumBytes = 1_048_576)
    {
        ArgumentNullException.ThrowIfNull(explicitlyAdmitted);
        ArgumentNullException.ThrowIfNull(cancellationAdmission);
        foreach (var callback in new Delegate[] { explicitlyAdmitted.Entropy32, explicitlyAdmitted.SaveVerifier,
            explicitlyAdmitted.ReadVerifier, explicitlyAdmitted.Redirect, explicitlyAdmitted.Exchange, explicitlyAdmitted.SaveTokens })
            if (callback is null || callback.GetInvocationList().Length != 1) throw new ArgumentException("One admitted callback per dependency required.");
        if (explicitlyAdmitted.AddClientAuthentication is { } authentication && authentication.GetInvocationList().Length != 1)
            throw new ArgumentException("One explicitly admitted client-authentication callback required.");
        if (maximumBytes is < 1024 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        dependencies = explicitlyAdmitted; this.maximumBytes = maximumBytes;
        cancellationAdmission.Bind(this); // Last: rejected constructor cannot bind an affine owner.
    }

    public Task<Uri> BeginAsync(McpAuthorizationCodeProfile profile, CancellationToken token = default) =>
        Start(ct => BeginCore(profile, ct), token);
    public Task<McpOAuthTokens> CompleteAsync(McpAuthorizationCodeProfile profile, string explicitlyAdmittedCode,
        CancellationToken token = default) => Start(ct => CompleteCore(profile, explicitlyAdmittedCode, ct), token);

    private Task<T> Start<T>(Func<CancellationToken, Task<T>> body, CancellationToken token)
    {
        RejectReentry(); token.ThrowIfCancellationRequested();
        var logicalParent = logical.Value; var physicalParent = physical;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            if (active is { IsCompleted: false }) throw new InvalidOperationException("One authorization flow operation at a time.");
            // Scheduling creates the actual returned operation before any callback can publish. Both ancestors captured above.
            var original = Task.Run(() => Run(body, token, logicalParent, physicalParent));
            active = original; return original;
        }
    }
    private Task<T> Run<T>(Func<CancellationToken, Task<T>> body, CancellationToken token, Frame? parent, Physical? ancestry)
    {
        var previous = physical; var entry = new Physical(this, parent, ancestry); physical = entry;
        try { return RunLogical(body, token, parent, ancestry); }
        finally { entry.Active = false; physical = previous; }
    }
    private async Task<T> RunLogical<T>(Func<CancellationToken, Task<T>> body, CancellationToken token, Frame? parent, Physical? ancestry)
    {
        var previous = logical.Value; var frame = new Frame(this, parent, ancestry); logical.Value = frame;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try { return await body(linked.Token).ConfigureAwait(false); }
        finally { frame.Active = false; logical.Value = previous; }
    }
    private T InvokePhysical<T>(Func<T> invoke)
    {
        var previous = physical; var entry = new Physical(this, logical.Value, previous); physical = entry;
        try { return invoke(); } finally { entry.Active = false; physical = previous; }
    }
    internal void CancelOwned(CancellationTokenSource borrowed)
    { ArgumentNullException.ThrowIfNull(borrowed); InvokePhysical(() => { borrowed.Cancel(); return true; }); }
    internal Task CancelOwnedAsync(CancellationTokenSource borrowed)
    {
        ArgumentNullException.ThrowIfNull(borrowed);
        var parent = logical.Value; var ancestry = physical;
        return Task.Run(() =>
        {
            var previous = physical; var entry = new Physical(this, parent, ancestry); physical = entry;
            try { borrowed.Cancel(); } finally { entry.Active = false; physical = previous; }
        }); // Caller owns and must directly join this exact original, including every cancellation sibling fault.
    }
    private void RejectReentry()
    {
        var pending = new Stack<object>(); var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (logical.Value is { } current) pending.Push(current);
        if (physical is { } entry) pending.Push(entry);
        while (pending.Count != 0)
        {
            var item = pending.Pop(); if (!visited.Add(item)) continue;
            if (item is Frame frame)
            {
                if (frame.Active && ReferenceEquals(frame.Owner, this)) throw new InvalidOperationException("OAuth flow cannot join its active ancestor original.");
                if (frame.Parent is { } parent) pending.Push(parent);
                if (frame.Ancestry is { } captured) pending.Push(captured);
            }
            else if (item is Physical physicalFrame)
            {
                if (physicalFrame.Active && ReferenceEquals(physicalFrame.Owner, this)) throw new InvalidOperationException("OAuth physical callback cannot join its owner.");
                if (physicalFrame.Parent is { } parent) pending.Push(parent);
                if (physicalFrame.Logical is { } captured) pending.Push(captured);
            }
        }
    }
    private Task<T> Work<T>(string phase, Func<ValueTask<T>> invoke, CancellationToken token)
    { Fence(token); return McpOAuthAdmittedWork.Invoke(phase, () => InvokePhysical(invoke), token); }
    private Task Work(string phase, Func<ValueTask> invoke, CancellationToken token)
    { Fence(token); return McpOAuthAdmittedWork.Invoke(phase, () => InvokePhysical(invoke), token); }
    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) ObjectDisposedException.ThrowIf(closing, this);
    }

    private sealed class ClientAuthenticationProposal : IMcpAuthorizationCodeClientAuthentication
    {
        private readonly object gate = new();
        private readonly Uri endpoint;
        private readonly McpOAuthAdmittedClient client;
        private readonly JsonData? metadata;
        private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<KeyValuePair<string, string>> form;
        private readonly int maximumBytes;
        private bool active = true;
        internal ClientAuthenticationProposal(Uri endpoint, McpAuthorizationCodeProfile profile,
            ImmutableDictionary<string, string> initialHeaders, List<KeyValuePair<string, string>> initialForm, int maximumBytes)
        {
            this.endpoint = endpoint; client = profile.Client; metadata = profile.Metadata; this.maximumBytes = maximumBytes;
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
        public override string ToString() => nameof(ClientAuthenticationProposal);
    }

    private async Task<Uri> BeginCore(McpAuthorizationCodeProfile profile, CancellationToken token)
    {
        Validate(profile);
        var metadata = profile.Metadata?.Value;
        if (metadata is { } m && !Strings(m, "response_types_supported", true).Contains("code"))
            throw new McpOAuthProtocolException("response_type", "Authorization server does not support codes.");
        if (metadata is { } pk && pk.TryGetProperty("code_challenge_methods_supported", out _) &&
            !Strings(pk, "code_challenge_methods_supported", false).Contains("S256"))
            throw new McpOAuthProtocolException("pkce_method", "Authorization server does not support S256.");
        var endpoint = Endpoint(profile, "authorization_endpoint", "/authorize", secure: false);
        var bytes = await Work("entropy", () => dependencies.Entropy32(token), token).ConfigureAwait(false);
        if (bytes is null || bytes.Length != 32) throw new McpOAuthProtocolException("entropy", "Exactly32 explicitly admitted random bytes required.");
        token.ThrowIfCancellationRequested();
        var verifier = Base64Url(bytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var fields = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"), new("client_id", profile.Client.ClientId), new("code_challenge", challenge),
            new("code_challenge_method", "S256"), new("redirect_uri", profile.RedirectUrl.OriginalString)
        };
        Add(fields, "state", profile.State); Add(fields, "scope", profile.Scope);
        if (profile.Scope is { } scope && HasScope(scope, "offline_access"))
            fields.Add(new("prompt", "consent"));
        Add(fields, "resource", profile.Resource);
        var url = SetQuery(endpoint, fields);
        if (Encoding.UTF8.GetByteCount(url.AbsoluteUri) > maximumBytes) throw new McpOAuthProtocolException("request_limit", "Authorization query exceeds admitted bound.");
        await Work("save-verifier", () => dependencies.SaveVerifier(verifier, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); // Cancel/close after durable verifier write prevents late redirect publication.
        await Work("redirect", () => dependencies.Redirect(url, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); return url;
    }
    private async Task<McpOAuthTokens> CompleteCore(McpAuthorizationCodeProfile profile, string code, CancellationToken token)
    {
        Validate(profile); Nonempty(code, "code");
        if (Encoding.UTF8.GetByteCount(code) > maximumBytes) throw new McpOAuthProtocolException("code_limit", "Admitted code exceeds finite byte bound.");
        var endpoint = Endpoint(profile, "token_endpoint", "/token", secure: true);
        var verifier = await Work("read-verifier", () => dependencies.ReadVerifier(token), token).ConfigureAwait(false);
        Nonempty(verifier, "verifier"); token.ThrowIfCancellationRequested();
        var fields = new List<KeyValuePair<string, string>>
        { new("grant_type", "authorization_code"), new("code", code), new("code_verifier", verifier), new("redirect_uri", profile.RedirectUrl.OriginalString) };
        Add(fields, "resource", profile.Resource);
        var headers = ImmutableDictionary<string, string>.Empty.Add("Accept", "application/json").Add("content-type", "application/x-www-form-urlencoded");
        if (dependencies.AddClientAuthentication is { } authentication)
        {
            var proposal = new ClientAuthenticationProposal(endpoint, profile, headers, fields, maximumBytes);
            try
            {
                await Work("add-client-authentication", () => authentication(proposal, token), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                (headers, fields) = proposal.Snapshot();
            }
            finally { proposal.Retire(); }
        }
        else
        {
            var supported = profile.Metadata is { } metadata ? Strings(metadata.Value, "token_endpoint_auth_methods_supported", false) : [];
            var method = SelectMethod(profile.Client, supported);
            if (method == "client_secret_basic")
            {
                Nonempty(profile.Client.ClientSecret, "client_secret_basic");
                headers = headers.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(profile.Client.ClientId + ":" + profile.Client.ClientSecret)));
            }
            else { fields.Add(new("client_id", profile.Client.ClientId)); if (method == "client_secret_post") Add(fields, "client_secret", profile.Client.ClientSecret); }
        }
        var request = new McpAuthorizationCodeRequest(endpoint, headers, Form(fields));
        if (Encoding.UTF8.GetByteCount(request.Body) > maximumBytes) throw new McpOAuthProtocolException("request_limit", "Code form exceeds admitted bound.");
        var response = await Work("authorization-code-exchange", () => dependencies.Exchange(request, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var tokens = ParseTokens(response);
        await Work("save-tokens", () => dependencies.SaveTokens(tokens, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); return tokens;
    }
    private void Validate(McpAuthorizationCodeProfile p)
    {
        ArgumentNullException.ThrowIfNull(p); ArgumentNullException.ThrowIfNull(p.Client);
        Url(p.AuthorizationServer); Url(p.RedirectUrl); Nonempty(p.Client.ClientId, "client_id");
        if (p.Client.ClientSecret is not null) Nonempty(p.Client.ClientSecret, "client_secret");
        if (p.Metadata is { } metadata && metadata.Value.ValueKind != JsonValueKind.Object) throw new McpOAuthProtocolException("metadata", "Object metadata required.");
        var values = new[] { p.AuthorizationServer.OriginalString, p.RedirectUrl.OriginalString, p.Client.ClientId,
            p.Client.ClientSecret ?? "", p.Scope ?? "", p.State ?? "", p.Resource ?? "", p.Metadata?.ToString() ?? "" };
        if (values.Sum(value => (long)Encoding.UTF8.GetByteCount(value)) > maximumBytes)
            throw new McpOAuthProtocolException("profile_limit", "Admitted profile exceeds finite byte bound.");
    }
    private static void Url(Uri value)
    { if (value is null || !value.IsAbsoluteUri || value.Scheme is not ("http" or "https")) throw new McpOAuthProtocolException("url", "Absolute HTTP(S) URL required."); }
    private Uri Endpoint(McpAuthorizationCodeProfile p, string property, string fallback, bool secure)
    {
        var raw = p.Metadata is { } metadata ? Text(metadata.Value, property, false) : null;
        Uri endpoint;
        try { endpoint = raw is null ? new Uri(p.AuthorizationServer, fallback) : new Uri(raw, UriKind.Absolute); }
        catch (UriFormatException error) { throw new McpOAuthProtocolException("metadata_url", "Invalid endpoint.", original: error); }
        Url(endpoint);
        endpoint = new UriBuilder(endpoint) { Host = endpoint.IdnHost }.Uri;
        if (secure && endpoint.Scheme != "https" && endpoint.Host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1")) throw new McpOAuthProtocolException("insecure_endpoint", "Token endpoint requires HTTPS or original exact loopback host.");
        return endpoint;
    }
    private McpOAuthTokens ParseTokens(McpAuthorizationCodeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (Encoding.UTF8.GetByteCount(response.Body) > maximumBytes) throw new McpOAuthProtocolException("token_limit", "Token response exceeds admitted byte bound.");
        JsonData? parsed = null;
        try { parsed = JsonData.Parse(response.Body); } catch (JsonException) { }
        if (parsed is { } json && json.Value.ValueKind == JsonValueKind.Object && json.Value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            var description = json.Value.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String ? JsonString(d) : JsonString(error);
            var errorUri = json.Value.TryGetProperty("error_uri", out var u) && u.ValueKind == JsonValueKind.String ? JsonString(u) : null;
            throw new McpOAuthProtocolException(JsonString(error), description, response.Status, errorUri);
        }
        if (response.Status is < 200 or > 299) throw new McpOAuthProtocolException("server_error", "Admitted token exchange rejected.", response.Status);
        if (parsed is not { } value || value.Value.ValueKind != JsonValueKind.Object) throw new McpOAuthProtocolException("token_invalid", "Object token response required.");
        double? expiry = null;
        if (value.Value.TryGetProperty("expires_in", out var e))
        { var number = Number(e, arrayElement: false); if (!double.IsFinite(number)) throw new McpOAuthProtocolException("token_invalid", "Finite expires_in required."); expiry = number; }
        return new(Text(value.Value, "access_token", true)!, Text(value.Value, "token_type", true)!, expiry,
            Text(value.Value, "scope", false), Text(value.Value, "refresh_token", false), Text(value.Value, "id_token", false));
    }
    private static string? Text(JsonElement value, string property, bool required)
    {
        if (!value.TryGetProperty(property, out var element))
        { if (!required) return null; throw new McpOAuthProtocolException("token_invalid", "Required string missing."); }
        if (element.ValueKind != JsonValueKind.String || JsonString(element) is not { Length: > 0 } text)
            throw new McpOAuthProtocolException("token_invalid", "Present string must be nonempty.");
        return text;
    }
    private static string[] Strings(JsonElement value, string property, bool required)
    {
        if (!value.TryGetProperty(property, out var list))
        { if (!required) return []; throw new McpOAuthProtocolException("metadata", "Required string array missing."); }
        if (list.ValueKind != JsonValueKind.Array) throw new McpOAuthProtocolException("metadata", "String array required.");
        return list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? JsonString(e) :
            throw new McpOAuthProtocolException("metadata", "String array required.")).ToArray();
    }
    private static string JsonString(JsonElement value)
    {
        // JsonDocument validated syntax; decode escape code units without rejecting JS lone UTF16 surrogates.
        var raw = value.GetRawText(); var result = new StringBuilder(raw.Length - 2);
        for (var index = 1; index < raw.Length - 1; index++)
        {
            if (raw[index] != '\\') { result.Append(raw[index]); continue; }
            var escape = raw[++index];
            if (escape == 'u')
            {
                var codeUnit = 0;
                for (var digit = 0; digit < 4; digit++) codeUnit = (codeUnit << 4) | Hex((byte)raw[++index]);
                result.Append((char)codeUnit);
            }
            else result.Append(escape switch { 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escape });
        }
        return result.ToString();
    }
    private static bool HasScope(string scope, string expected)
    {
        var start = 0;
        for (var index = 0; index <= scope.Length; index++)
            if (index == scope.Length || JsWhite(scope[index]))
            { if (scope.AsSpan(start, index - start).SequenceEqual(expected.AsSpan())) return true; start = index + 1; }
        return false;
    }
    private static double Number(JsonElement value, bool arrayElement)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() == 0) return 0;
            return value.GetArrayLength() == 1 ? Number(value[0], arrayElement: true) : double.NaN;
        }
        if (value.ValueKind == JsonValueKind.Null) return 0;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return arrayElement ? double.NaN : value.ValueKind == JsonValueKind.True ? 1 : 0;
        if (value.ValueKind != JsonValueKind.String) return double.NaN;
        var text = JsonString(value); var start = 0; var end = text.Length;
        while (start < end && JsWhite(text[start])) start++;
        while (end > start && JsWhite(text[end - 1])) end--;
        text = text[start..end]; if (text.Length == 0) return 0;
        if (text.Length > 2 && text[0] == '0' && (char.ToLowerInvariant(text[1]) is 'x' or 'b' or 'o'))
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'b' => 2, _ => 8 }; double result = 0;
            for (var index = 2; index < text.Length; index++)
            {
                var digit = text[index] is >= '0' and <= '9' ? text[index] - '0' : char.ToLowerInvariant(text[index]) is >= 'a' and <= 'f' ? char.ToLowerInvariant(text[index]) - 'a' + 10 : -1;
                if (digit < 0 || digit >= radix) return double.NaN; result = result * radix + digit;
            }
            return result;
        }
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        return double.TryParse(text, style, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
    }
    private static bool JsWhite(char value) => value is '\u0009' or '\u000B' or '\u000C' or '\u0020' or '\u00A0' or '\uFEFF' or '\u000A' or '\u000D' or '\u2028' or '\u2029' or '\u1680' or '\u202F' or '\u205F' or '\u3000' or >= '\u2000' and <= '\u200A';
    private static string SelectMethod(McpOAuthAdmittedClient client, string[] supported)
    {
        if ((client.AuthenticationMethod is "client_secret_basic" or "client_secret_post" or "none") &&
            (supported.Length == 0 || supported.Contains(client.AuthenticationMethod))) return client.AuthenticationMethod;
        if (supported.Length == 0) return client.ClientSecret is not null ? "client_secret_basic" : "none";
        if (client.ClientSecret is not null && supported.Contains("client_secret_basic")) return "client_secret_basic";
        if (client.ClientSecret is not null && supported.Contains("client_secret_post")) return "client_secret_post";
        return supported.Contains("none") || client.ClientSecret is null ? "none" : "client_secret_post";
    }
    private static void Nonempty(string? value, string name)
    { if (string.IsNullOrEmpty(value)) throw new McpOAuthProtocolException("invalid_" + name, "Nonempty admitted string required."); }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void Add(List<KeyValuePair<string, string>> fields, string key, string? value)
    { if (!string.IsNullOrEmpty(value)) fields.Add(new(key, value)); }
    private static string Escape(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal).Replace("~", "%7E", StringComparison.Ordinal).Replace("%2A", "*", StringComparison.Ordinal);
    private static string Form(IEnumerable<KeyValuePair<string, string>> fields) => string.Join('&', fields.Select(p =>
        Escape(p.Key) + "=" + Escape(p.Value)));
    private static string DecodeFormComponent(string value)
    {
        // WHATWG form parsing: plus to space, percent-decode bytes, then forgiving UTF-8 decode.
        var encoded = Encoding.UTF8.GetBytes(value.Replace('+', ' ')); var decoded = new List<byte>(encoded.Length);
        for (var index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] == (byte)'%' && index + 2 < encoded.Length &&
                Hex(encoded[index + 1]) is var high && high >= 0 && Hex(encoded[index + 2]) is var low && low >= 0)
            { decoded.Add((byte)((high << 4) | low)); index += 2; }
            else decoded.Add(encoded[index]);
        }
        return Encoding.UTF8.GetString(decoded.ToArray());
    }
    private static int Hex(byte value) => value is >= (byte)'0' and <= (byte)'9' ? value - '0' :
        value is >= (byte)'A' and <= (byte)'F' ? value - 'A' + 10 : value is >= (byte)'a' and <= (byte)'f' ? value - 'a' + 10 : -1;
    private static Uri SetQuery(Uri endpoint, List<KeyValuePair<string, string>> fields)
    {
        var query = endpoint.Query; if (query.StartsWith('?')) query = query[1..];
        var retained = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var separator = part.IndexOf('=');
            return new KeyValuePair<string, string>(DecodeFormComponent(separator < 0 ? part : part[..separator]),
                DecodeFormComponent(separator < 0 ? "" : part[(separator + 1)..]));
        }).ToList();
        foreach (var field in fields)
        {
            var index = retained.FindIndex(pair => pair.Key == field.Key);
            if (index < 0) retained.Add(field);
            else
            {
                retained[index] = field;
                for (var duplicate = retained.Count - 1; duplicate > index; duplicate--)
                    if (retained[duplicate].Key == field.Key) retained.RemoveAt(duplicate);
            }
        }
        var builder = new UriBuilder(endpoint) { Query = Form(retained) };
        return builder.Uri;
    }
    public ValueTask DisposeAsync()
    {
        RejectReentry();
        var parent = logical.Value; var ancestry = physical;
        lock (gate)
        {
            if (close is not null) return new(close);
            closing = true; var pending = active;
            close = Task.Run(() => RunClose(pending, parent, ancestry)); return new(close);
        }
    }
    private Task RunClose(Task? pending, Frame? parent, Physical? ancestry)
    {
        var previous = physical; var entry = new Physical(this, parent, ancestry); physical = entry;
        try { return CloseCore(pending); } finally { entry.Active = false; physical = previous; }
    }
    private async Task CloseCore(Task? pending)
    {
        var failures = new List<Exception>();
        Task? cancellation = null;
        try { cancellation = CancelOwnedAsync(lifetime); await cancellation.ConfigureAwait(false); }
        catch (Exception direct) { failures.Add(new McpOAuthFlowOriginalException("close-cancellation", cancellation, cancellation?.Exception ?? direct, direct)); }
        if (pending is not null)
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException direct) when (pending.IsCanceled)
            { failures.Add(new McpOAuthFlowCanceledException(pending, direct)); }
            catch (Exception direct) { failures.Add(new McpOAuthFlowOriginalException("close-active", pending, pending.Exception ?? direct, direct)); }
        try { lifetime.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("OAuth active operation/cancellation/cleanup originals.", failures);
    }
}
