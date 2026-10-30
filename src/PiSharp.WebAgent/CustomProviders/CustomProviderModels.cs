// Decision 0011 (run W3, CP): what the custom providers area knows about the data folder's models.json, as immutable snapshots (the store
// replaces the whole snapshot on every change), and the editor's draft, which is what the browser sends and receives. Nothing here holds a
// secret: an API key is either a reference to a variable ($VAR or ${VAR}) or a vault account's masked hint; a key typed in the editor goes
// in once (CustomProviderKeyDraft.ApiKey, write-only) and never comes back out.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.CustomProviders;

/// <summary>Where a custom provider's API key comes from.</summary>
public enum CustomKeySource
{
    /// <summary>No key: <c>apiKey</c> is left out and nothing is stored (a local server). Requests carry a placeholder key.</summary>
    None,

    /// <summary>A key stored encrypted in the credential vault, as an API key account of the provider (never in the file).</summary>
    Stored,

    /// <summary>An environment reference in the file: <c>"apiKey": "$VAR"</c> or <c>"${VAR}"</c>, read from the web host's environment.</summary>
    Environment,

    /// <summary>A key the web app does not use: a literal or a <c>!command</c> written into the file by hand, or a mixed template. The
    /// provider is listed with a warning, but it is not routed and not in the model picker until it is saved with another source.</summary>
    Unsupported,
}

/// <summary>The data folder's models.json as last read (or written).</summary>
public sealed record CustomProviderSnapshot
{
    /// <summary>Increases with every change of the file (a save here, or an edit in another editor); 0 before the first read.</summary>
    public long Version { get; init; }

    /// <summary>The file's full path.</summary>
    public required string Path { get; init; }

    /// <summary>Whether the file exists.</summary>
    public bool Exists { get; init; }

    /// <summary>Why the file cannot be used (unreadable, not JSON, against the schema, or a provider that does not compose), or null when it
    /// is valid (or missing). An invalid file lists nothing and refuses writes until it is fixed.</summary>
    public string? Error { get; init; }

    /// <summary>Whether the file is valid (or missing).</summary>
    public bool Valid => Error is null;

    /// <summary>The custom providers (entries whose id is not a built-in provider), in file order.</summary>
    public IReadOnlyList<CustomProviderInfo> Providers { get; init; } = [];

    /// <summary>The model overrides of built-in providers (<c>providers.&lt;id&gt;.modelOverrides</c>), in file order.</summary>
    public IReadOnlyList<ModelOverrideInfo> Overrides { get; init; } = [];

    /// <summary>When the file was last written, or null when it does not exist.</summary>
    public DateTimeOffset? Saved { get; init; }

    /// <summary>The file's size in bytes (0 when it does not exist).</summary>
    public long Size { get; init; }

    /// <summary>The sanitized copy the catalog and the routes read (keys that are not references and <c>!command</c> values removed), or
    /// null when the file defines nothing to route.</summary>
    public string? RoutesPath { get; init; }

    /// <summary>The parsed document (an empty <c>{"providers":{}}</c> when the file is missing or invalid). Callers clone it before editing.</summary>
    internal JsonObject Document { get; init; } = new() { ["providers"] = new JsonObject() };

    /// <summary>The custom provider <paramref name="id"/>, or null.</summary>
    public CustomProviderInfo? Find(string? id) => id is null ? null : Providers.FirstOrDefault(provider => provider.Id == id);

    /// <summary>The number of models of every custom provider.</summary>
    public int ModelCount => Providers.Sum(provider => provider.Models.Count);
}

/// <summary>A custom provider of models.json (metadata only: never a key).</summary>
/// <param name="Id">The provider id (models are referenced as <c>id/model</c>).</param>
/// <param name="Name">Its display name (the id when the file names none).</param>
/// <param name="BaseUrl">Its base URL (empty when the file has none).</param>
/// <param name="Api">Its API type (empty when the file has none).</param>
public sealed record CustomProviderInfo(string Id, string Name, string BaseUrl, string Api)
{
    /// <summary>How the file configures the key: <see cref="CustomKeySource.None"/> (no <c>apiKey</c>: nothing, or a vault account),
    /// <see cref="CustomKeySource.Environment"/> or <see cref="CustomKeySource.Unsupported"/>.</summary>
    public CustomKeySource FileKey { get; init; }

    /// <summary>The variable of an environment key, or null.</summary>
    public string? KeyVariable { get; init; }

    /// <summary>Why the key or a header is not used, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>Whether the key is sent as <c>Authorization: Bearer</c> whatever the API (<c>authHeader</c>).</summary>
    public bool AuthHeader { get; init; }

    /// <summary>The names of the provider's headers.</summary>
    public IReadOnlyList<string> HeaderNames { get; init; } = [];

    /// <summary>The variables its key and headers reference, which its routes may read from the host's environment.</summary>
    public IReadOnlyList<string> Variables { get; init; } = [];

    /// <summary>Its models, in file order.</summary>
    public IReadOnlyList<CustomModelInfo> Models { get; init; } = [];

    /// <summary>Whether the base URL is on this machine (<c>localhost</c>, <c>127.x</c>, <c>::1</c>, <c>*.localhost</c>): free to use.</summary>
    public bool Loopback { get; init; }

    /// <summary>Whether it is routed: its key is usable here and its API has a live route.</summary>
    public bool Routed { get; init; }
}

/// <summary>A model of a custom provider.</summary>
/// <param name="Id">The model id.</param>
/// <param name="Name">The display name (the id when none).</param>
public sealed record CustomModelInfo(string Id, string Name)
{
    /// <summary>Whether it reasons.</summary>
    public bool Reasoning { get; init; }

    /// <summary>Whether it reads images.</summary>
    public bool Images { get; init; }

    /// <summary>The context window, or null when the file leaves the default.</summary>
    public double? ContextWindow { get; init; }

    /// <summary>The output cap, or null when the file leaves the default.</summary>
    public double? MaxTokens { get; init; }

    /// <summary>The input price per million tokens (0 when none).</summary>
    public double CostInput { get; init; }

    /// <summary>The output price per million tokens (0 when none).</summary>
    public double CostOutput { get; init; }

    /// <summary>Whether a non-zero price is entered.</summary>
    public bool Priced { get; init; }
}

/// <summary>One built-in model's override (<c>providers.&lt;provider&gt;.modelOverrides.&lt;model&gt;</c>).</summary>
/// <param name="Provider">The built-in provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Fields">The override's fields, in file order (a clone; never a secret: secret-like literal headers are dropped).</param>
public sealed record ModelOverrideInfo(string Provider, string Model, JsonObject Fields);

/// <summary>A custom provider as the editor shows and sends it. On a save, fields the editor does not manage (other compat fields,
/// <c>inputLimits</c>, <c>promptCache</c>, ...) are kept from the file.</summary>
public sealed record CustomProviderDraft
{
    /// <summary>The id the provider had when the editor opened (null when adding): a different <see cref="Id"/> renames it.</summary>
    public string? OriginalId { get; init; }

    /// <summary>The provider id: lower-case letters, digits and dashes, not a built-in provider's id.</summary>
    public string? Id { get; init; }

    /// <summary>The display name.</summary>
    public string? Name { get; init; }

    /// <summary>The base URL (http or https).</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The API type.</summary>
    public string? Api { get; init; }

    /// <summary>Whether the key is sent as <c>Authorization: Bearer</c> (<c>authHeader</c>).</summary>
    public bool AuthHeader { get; init; }

    /// <summary>The key.</summary>
    public CustomProviderKeyDraft? Key { get; init; }

    /// <summary>The provider's headers.</summary>
    public IReadOnlyList<CustomHeaderDraft>? Headers { get; init; }

    /// <summary>Provider-level compat fields the editor manages (<see cref="CustomProviderJson.EditableCompat"/>); a null value removes one.</summary>
    public Dictionary<string, JsonElement?>? Compat { get; init; }

    /// <summary>The models.</summary>
    public IReadOnlyList<CustomModelDraft>? Models { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"CustomProviderDraft ({Id}) [redacted]";
}

/// <summary>The key part of a <see cref="CustomProviderDraft"/>. <see cref="ToString"/> never shows the key.</summary>
public sealed record CustomProviderKeyDraft
{
    /// <summary><c>none</c>, <c>stored</c> or <c>env</c> (<c>command</c> is not offered in the web app).</summary>
    public string? Source { get; init; }

    /// <summary>The variable of an <c>env</c> key, without <c>$</c>.</summary>
    public string? Variable { get; init; }

    /// <summary>A key to store (write-only: never sent back); null keeps the stored one.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Read only: the stored key's masked hint.</summary>
    public string? Hint { get; init; }

    /// <summary>Read only: whether the variable of an <c>env</c> key is set on the web host.</summary>
    public bool? Set { get; init; }

    /// <summary>Read only: why the file's key is not used (a literal or <c>!command</c> written by hand), or null.</summary>
    public string? Problem { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"CustomProviderKeyDraft ({Source}) [redacted]";
}

/// <summary>A header of a <see cref="CustomProviderDraft"/>.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">The value (a literal, or a <c>$VAR</c> reference); null with <paramref name="Hidden"/> keeps the file's value.</param>
/// <param name="Hidden">The file's value looks like a secret and is not shown; it is kept unless a new value is sent.</param>
public sealed record CustomHeaderDraft(string? Name, string? Value, bool Hidden = false);

/// <summary>A model of a <see cref="CustomProviderDraft"/>.</summary>
public sealed record CustomModelDraft
{
    /// <summary>The model id.</summary>
    public string? Id { get; init; }

    /// <summary>The display name.</summary>
    public string? Name { get; init; }

    /// <summary>Whether it reasons.</summary>
    public bool Reasoning { get; init; }

    /// <summary>Whether it reads images (<c>input: ["text", "image"]</c>).</summary>
    public bool Images { get; init; }

    /// <summary>The context window in tokens.</summary>
    public double? ContextWindow { get; init; }

    /// <summary>The output cap in tokens.</summary>
    public double? MaxTokens { get; init; }

    /// <summary>The input price per million tokens.</summary>
    public double? CostInput { get; init; }

    /// <summary>The output price per million tokens.</summary>
    public double? CostOutput { get; init; }

    /// <summary>Model-level compat fields the editor manages; a null value removes one.</summary>
    public Dictionary<string, JsonElement?>? Compat { get; init; }

    /// <summary>The thinking level map: a level mapped to a value, or to null (unsupported); levels left out keep their default.</summary>
    public Dictionary<string, string?>? ThinkingLevelMap { get; init; }
}

/// <summary>The answer of a connection test or a model discovery (never a key).</summary>
/// <param name="Ok">Whether the provider answered with a model list.</param>
/// <param name="Message">What happened, scrubbed.</param>
public sealed record CustomProviderTest(bool Ok, string Message)
{
    /// <summary>False when no test ran (offline, nothing to test).</summary>
    public bool Supported { get; init; } = true;

    /// <summary>The HTTP status, or null.</summary>
    public int? StatusCode { get; init; }

    /// <summary>How long the answer took.</summary>
    public long? LatencyMs { get; init; }

    /// <summary>What was called: <c>GET &lt;url&gt;</c> (no query string).</summary>
    public string? Endpoint { get; init; }

    /// <summary>The model ids the provider listed, in its order.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>When the test ran.</summary>
    public DateTimeOffset TestedAt { get; init; }

    /// <summary>The ids of <see cref="Models"/> that look like embedding models (not chat models).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyList<string> Embeddings => [.. Models.Where(CustomProviderJson.LooksLikeEmbedding)];
}
