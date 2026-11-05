// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/identity.ts and packages/ai/src/utils/version.ts.
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace PiSharp.AI;

/// <summary>
/// identity.ts <c>UserAgentMode</c>, the <c>User-Agent</c> sent to providers: <see cref="Detailed"/> is
/// <c>pi/&lt;version&gt; (&lt;platform&gt;; dotnet/&lt;runtime version&gt;; &lt;arch&gt;)</c>, <see cref="Short"/> is <c>pi</c>,
/// and <see cref="Custom(string)"/> is the given value.
/// </summary>
[Experimental("PISHARP_IDENTITY001")]
public sealed record ClientUserAgent
{
    private ClientUserAgent(string mode, string? value) { Mode = mode; Value = value; }

    /// <summary><c>"detailed"</c>, the default.</summary>
    public static ClientUserAgent Detailed { get; } = new("detailed", null);
    /// <summary><c>"short"</c>: <c>pi</c>.</summary>
    public static ClientUserAgent Short { get; } = new("short", null);
    /// <summary><c>{ custom }</c>: the value as given.</summary>
    public static ClientUserAgent Custom(string value) => new("custom", value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary><c>detailed</c>, <c>short</c> or <c>custom</c>.</summary>
    public string Mode { get; }
    /// <summary>The custom value; null for the other modes.</summary>
    public string? Value { get; }
}

/// <summary>
/// identity.ts <c>ClientAttribution</c>: who requests are attributed to. <see cref="ClientIdentities.GetAttributionHeaders"/> maps
/// the fields to each provider's headers. PiSharp defines no default attribution.
/// </summary>
/// <param name="Name">App name: OpenRouter app title, NVIDIA invoke origin (first letter upper-cased), OpenCode client.</param>
/// <param name="Url">App URL: OpenRouter <c>HTTP-Referer</c>.</param>
/// <param name="Categories">App categories, e.g. <c>cli-agent</c>: OpenRouter <c>X-OpenRouter-Categories</c>, comma-separated.</param>
[Experimental("PISHARP_IDENTITY001")]
public sealed record ClientAttribution(string Name, string Url, ImmutableArray<string> Categories = default)
{
    public bool Equals(ClientAttribution? other) => other is not null && Name == other.Name && Url == other.Url &&
        (Categories.IsDefault ? [] : Categories).SequenceEqual(other.Categories.IsDefault ? [] : other.Categories, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Name, Url, Categories.IsDefault ? 0 : Categories.Length);
}

/// <summary>
/// identity.ts <c>ClientIdentity</c>: how requests identify the calling app to providers. Every field is optional; absent
/// fields use the defaults: a detailed <c>User-Agent</c> with the Pi version PiSharp tracks and no attribution headers.
/// </summary>
[Experimental("PISHARP_IDENTITY001")]
public sealed record ClientIdentity
{
    /// <summary>Null: <see cref="ClientUserAgent.Detailed"/>.</summary>
    public ClientUserAgent? UserAgent { get; init; }
    /// <summary>Provider attribution headers. Null: none, unless a merged identity sets it.</summary>
    public ClientAttribution? Attribution { get; init; }
    /// <summary>Upstream <c>attribution: null</c>: no attribution, also turning off attribution set by a merged identity.</summary>
    public bool AttributionOff { get; init; }
    /// <summary>Version in the detailed <c>User-Agent</c>. Null: <see cref="ClientIdentities.PiVersion"/>.</summary>
    public string? Version { get; init; }
}

/// <summary>identity.ts helpers: merging, the <c>User-Agent</c> value and the headers an identity adds to a request.</summary>
[Experimental("PISHARP_IDENTITY001")]
public static class ClientIdentities
{
    /// <summary>version.ts <c>PI_AI_VERSION</c>: decision 0013 (Identity) has PiSharp identify as the Pi release it tracks.</summary>
    public const string PiVersion = "1.5.0";

    private const string NvidiaNimHost = "integrate.api.nvidia.com";
    private const string OpenCodeHost = "opencode.ai";

    /// <summary><c>mergeClientIdentity</c>: field by field; later sources win and unset fields are skipped. Null when every source is.</summary>
    public static ClientIdentity? Merge(params ClientIdentity?[] sources)
    {
        ClientIdentity? merged = null;
        foreach (var source in sources)
        {
            if (source is null) continue;
            merged ??= new();
            if (source.UserAgent is not null) merged = merged with { UserAgent = source.UserAgent };
            if (source.AttributionOff) merged = merged with { Attribution = null, AttributionOff = true };
            else if (source.Attribution is not null) merged = merged with { Attribution = source.Attribution, AttributionOff = false };
            if (source.Version is not null) merged = merged with { Version = source.Version };
        }
        return merged;
    }

    /// <summary><c>getClientVersion</c>: the version in the detailed <c>User-Agent</c>.</summary>
    public static string GetClientVersion(ClientIdentity? identity) => identity?.Version ?? PiVersion;

    /// <summary>
    /// <c>formatUserAgent</c>. The runtime field names .NET (<c>dotnet/&lt;Environment.Version&gt;</c>) where the source names
    /// <c>node/vX</c> or <c>bun/X</c>; platform and arch use Node's <c>process.platform</c> and <c>process.arch</c> names.
    /// </summary>
    public static string FormatUserAgent(ClientIdentity? identity)
    {
        var mode = identity?.UserAgent ?? ClientUserAgent.Detailed;
        if (mode.Mode == "custom") return mode.Value!;
        if (mode.Mode == "short") return "pi";
        return $"pi/{GetClientVersion(identity)} ({Platform}; dotnet/{Environment.Version}; {Arch})";
    }

    /// <summary>
    /// <c>getAttributionHeaders</c>: an identity's attribution headers for a request target (provider id and base URL). Empty
    /// when attribution is off. OpenRouter (provider <c>openrouter</c> or a base URL containing <c>openrouter.ai</c>) gets
    /// <c>HTTP-Referer</c>, <c>X-OpenRouter-Title</c> and <c>X-OpenRouter-Categories</c>; NVIDIA NIM
    /// <c>X-BILLING-INVOKE-ORIGIN</c>; OpenCode <c>x-opencode-client</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GetAttributionHeaders(string provider, string baseUrl, ClientIdentity? identity)
    {
        if (identity?.Attribution is not { } attribution) return [];
        if (provider == "openrouter" || baseUrl.Contains("openrouter.ai", StringComparison.Ordinal))
        {
            var headers = new List<KeyValuePair<string, string>> { new("HTTP-Referer", attribution.Url), new("X-OpenRouter-Title", attribution.Name) };
            if (!attribution.Categories.IsDefaultOrEmpty) headers.Add(new("X-OpenRouter-Categories", string.Join(',', attribution.Categories)));
            return headers;
        }
        // NVIDIA expects a capitalized origin.
        if (provider == "nvidia" || MatchesHost(baseUrl, NvidiaNimHost))
            return [new("X-BILLING-INVOKE-ORIGIN", attribution.Name.Length == 0 ? "" : char.ToUpperInvariant(attribution.Name[0]) + attribution.Name[1..])];
        if (provider is "opencode" or "opencode-go" || MatchesHost(baseUrl, OpenCodeHost)) return [new("x-opencode-client", attribution.Name)];
        return [];
    }

    /// <summary>
    /// <c>getIdentityHeaders</c>: the default headers an identity adds to a request, <c>User-Agent</c> and attribution. APIs apply
    /// them below model, provider and request headers, so configured headers override them and null request headers remove them.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GetIdentityHeaders(string provider, string baseUrl, ClientIdentity? identity) =>
        [new("User-Agent", FormatUserAgent(identity)), .. GetAttributionHeaders(provider, baseUrl, identity)];

    private static bool MatchesHost(string baseUrl, string host) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Node <c>process.platform</c> for this OS.</summary>
    internal static string Platform => OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsFreeBSD() ? "freebsd" : RuntimeInformation.OSDescription.Split(' ')[0].ToLowerInvariant();

    /// <summary>Node <c>process.arch</c> for the process architecture.</summary>
    internal static string Arch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64", Architecture.Arm64 => "arm64", Architecture.X86 => "ia32", Architecture.Arm => "arm",
        Architecture.S390x => "s390x", Architecture.LoongArch64 => "loong64", Architecture.Ppc64le => "ppc64", Architecture.RiscV64 => "riscv64",
        var other => other.ToString().ToLowerInvariant()
    };

    /// <summary>Whether a header name is one <see cref="GetIdentityHeaders"/> can add.</summary>
    internal static bool IsIdentityHeader(string name) => name.ToLowerInvariant() is "user-agent" or "http-referer" or "x-openrouter-title" or
        "x-openrouter-categories" or "x-billing-invoke-origin" or "x-opencode-client";

    /// <summary>The identity headers of <paramref name="requested"/> merged over <paramref name="configured"/>, for building a request.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> HeadersFor(string provider, string baseUrl, ClientIdentity? configured, ClientIdentity? requested) =>
        GetIdentityHeaders(provider, baseUrl, Merge(configured, requested));
}
