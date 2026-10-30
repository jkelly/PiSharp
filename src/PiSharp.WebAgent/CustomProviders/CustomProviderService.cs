// Decision 0011 (run W3, CP): the custom providers area's operations, for its JSON API and page: the editor's draft of a provider, save
// (add, edit, rename), remove, duplicate, import and export of a models.json, and built-in model overrides. Every write goes through the
// store (validated as Pi validates models.json, written atomically with a .bak). Keys never enter the file: a key the user types becomes
// (or replaces) the provider's API key account in the credential vault, labelled "<name> key"; an environment key is written as its
// reference; a provider with no key has neither. Switching a provider away from a stored key, renaming it or removing it moves or removes
// its vault accounts, after the file is written. Import moves a literal key into the vault, drops a !command key or header (the web app
// does not run commands) and a key of a built-in provider (those are accounts), and says so in its warnings. Nothing returned here holds
// a secret.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Models;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Routing;

namespace PiSharp.WebAgent.CustomProviders;

/// <summary>The custom providers area's operations. A singleton; writes are serialized by the store.</summary>
public sealed class CustomProviderService(CustomProviderStore store, IAccountStore accounts, ICredentialVault vault, CredentialsOptions credentials,
    ILogger<CustomProviderService>? log = null)
{
    private readonly ConcurrentDictionary<string, CustomProviderTest> tests = new(StringComparer.Ordinal);

    /// <summary>The store.</summary>
    public CustomProviderStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>The current snapshot.</summary>
    public CustomProviderSnapshot Current => Store.Current;

    /// <summary>The last connection test of <paramref name="id"/> since the app started, or null.</summary>
    public CustomProviderTest? LastTest(string id) => tests.TryGetValue(id, out var test) ? test : null;

    /// <summary>Records <paramref name="result"/> as <paramref name="id"/>'s last test.</summary>
    internal void RecordTest(string id, CustomProviderTest result) => tests[id] = result;

    /// <summary>The vault account whose key <paramref name="id"/> uses (its active API key account), or null.</summary>
    public AccountInfo? StoredKey(string id) => accounts.Active(id) is { Kind: AccountKind.ApiKey } active ? active : null;

    /// <summary>How <paramref name="provider"/>'s key is set up now: the file's reference, a vault account, none, or unsupported.</summary>
    public CustomKeySource KeySource(CustomProviderInfo provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.FileKey == CustomKeySource.None && StoredKey(provider.Id) is not null ? CustomKeySource.Stored : provider.FileKey;
    }

    /// <summary>Whether the environment variable <paramref name="name"/> is set on the web host (never its value).</summary>
    public bool IsSet(string? name) => CustomProviderJson.IsVariable(name) && !string.IsNullOrEmpty(credentials.Environment(name!));

    // ─── Drafts ─────────────────────────────────────────────────────────────

    /// <summary>The editor's draft of the custom provider <paramref name="id"/>, or null when there is none.</summary>
    public CustomProviderDraft? Draft(string id)
    {
        var snapshot = Current;
        if (snapshot.Find(id) is not { } info || Entry(snapshot.Document, id) is not { } entry) return null;
        var stored = StoredKey(id);
        var key = info.FileKey switch
        {
            CustomKeySource.Environment => new CustomProviderKeyDraft { Source = "env", Variable = info.KeyVariable, Set = IsSet(info.KeyVariable) },
            CustomKeySource.Unsupported => new CustomProviderKeyDraft { Source = stored is null ? "none" : "stored", Hint = stored?.Hint, Problem = info.Problem },
            _ => stored is null ? new CustomProviderKeyDraft { Source = "none" } : new CustomProviderKeyDraft { Source = "stored", Hint = stored.Hint },
        };
        var models = new List<CustomModelDraft>();
        if (entry["models"] is JsonArray array)
        {
            foreach (var model in array.OfType<JsonObject>())
            {
                var cost = model["cost"] as JsonObject;
                models.Add(new CustomModelDraft
                {
                    Id = Text(model, "id"),
                    Name = Text(model, "name"),
                    Reasoning = Flag(model, "reasoning"),
                    Images = model["input"] is JsonArray input && input.Any(kind => kind?.GetValueKind() == JsonValueKind.String && kind.GetValue<string>() == "image"),
                    ContextWindow = Number(model, "contextWindow"),
                    MaxTokens = Number(model, "maxTokens"),
                    CostInput = Number(cost, "input") ?? 0,
                    CostOutput = Number(cost, "output") ?? 0,
                    Compat = EditableCompat(model),
                    ThinkingLevelMap = model["thinkingLevelMap"] is JsonObject map
                        ? map.Where(pair => CustomProviderJson.ThinkingLevels.Contains(pair.Key))
                            .ToDictionary(pair => pair.Key, pair => pair.Value?.GetValueKind() == JsonValueKind.String ? pair.Value.GetValue<string>() : null)
                        : null,
                });
            }
        }
        return new CustomProviderDraft
        {
            OriginalId = id,
            Id = id,
            Name = Text(entry, "name") ?? "",
            BaseUrl = Text(entry, "baseUrl") ?? "",
            Api = Text(entry, "api") ?? "",
            AuthHeader = Flag(entry, "authHeader"),
            Key = key,
            Headers = [.. CustomProviderJson.StringMap(entry, "headers").Select(pair => CustomProviderJson.IsShownHeader(pair.Key, pair.Value)
                ? new CustomHeaderDraft(pair.Key, pair.Value) : new CustomHeaderDraft(pair.Key, null, Hidden: true))],
            Compat = EditableCompat(entry),
            Models = models,
        };
    }

    private static Dictionary<string, JsonElement?>? EditableCompat(JsonObject value)
    {
        if (value["compat"] is not JsonObject compat) return null;
        var fields = compat.Where(pair => CustomProviderJson.EditableCompat.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => (JsonElement?)JsonSerializer.SerializeToElement(pair.Value));
        return fields.Count == 0 ? null : fields;
    }

    /// <summary>The models.json fragment (<c>providers.&lt;id&gt;</c>) <paramref name="draft"/> would save, whether the whole file would be
    /// valid with it, and why not.</summary>
    public CustomProviderPreview Preview(CustomProviderDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var snapshot = Current;
        try
        {
            var (id, entry, _) = Build(draft, snapshot);
            var fragment = CustomProviderJson.Redacted(new JsonObject { ["providers"] = new JsonObject { [id] = entry.DeepClone() } });
            var document = (JsonObject)snapshot.Document.DeepClone();
            Place(document, draft.OriginalId, id, entry);
            var error = snapshot.Valid ? Store.Validate(CustomProviderJson.Format(document, DateTimeOffset.UtcNow)) : snapshot.Error;
            return new CustomProviderPreview(CustomProviderJson.Pretty(fragment), error is null, error);
        }
        catch (ArgumentException error) { return new CustomProviderPreview(null, false, error.Message); }
    }

    // ─── Writes ─────────────────────────────────────────────────────────────

    /// <summary>Saves <paramref name="draft"/> (adds it, or replaces <see cref="CustomProviderDraft.OriginalId"/>, renaming it when the id
    /// changed) and sets up its key; the saved provider.</summary>
    /// <exception cref="ArgumentException">The draft is not valid (the message says why).</exception>
    /// <exception cref="KeyNotFoundException">The provider being edited no longer exists.</exception>
    /// <exception cref="InvalidOperationException">models.json is invalid now.</exception>
    public async Task<CustomProviderInfo> SaveAsync(CustomProviderDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var before = Current;
        if (draft.OriginalId is { Length: > 0 } original && before.Find(original) is null) throw new KeyNotFoundException($"There is no custom provider {original}.");
        var (id, entry, typedKey) = Build(draft, before);
        var source = draft.Key?.Source?.Trim() ?? "none";
        var oldId = draft.OriginalId is { Length: > 0 } previous ? previous : null;
        var keep = source == "stored" ? StoredKey(id) ?? (oldId is null ? null : StoredKey(oldId)) : null;
        if (source == "stored" && typedKey is null && keep is null) throw new ArgumentException("Paste the API key to store, or choose another key source.");
        var name = Text(entry, "name") ?? id;
        var snapshot = await Store.UpdateAsync(document => Place(document, oldId, id, entry), async () =>
        {
            if (source != "stored") return;
            if (typedKey is not null)
            {
                if (StoredKey(id) is { } current) await accounts.ReplaceApiKeyAsync(current.Id, typedKey, null, cancellationToken).ConfigureAwait(false);
                else await accounts.AddApiKeyAsync(new ApiKeyAccountRequest(id, typedKey) { Label = name + " key" }, cancellationToken).ConfigureAwait(false);
            }
            else if (keep is not null && keep.Provider != id)
            {
                // A rename keeps the stored key: it is copied, on the server, to an account of the new id (the old one is removed below).
                var credential = await vault.ReadAsync(keep.Id, cancellationToken).ConfigureAwait(false);
                if (credential?.ApiKey is not { Length: > 0 } moved) throw new InvalidOperationException("The stored key could not be read, so the provider was not renamed.");
                await accounts.AddApiKeyAsync(new ApiKeyAccountRequest(id, moved) { Label = name + " key" }, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        if (oldId is not null && oldId != id)
        {
            await RemoveKeysAsync(oldId).ConfigureAwait(false);
            if (tests.TryRemove(oldId, out var test)) tests[id] = test;
        }
        if (source != "stored") await RemoveKeysAsync(id).ConfigureAwait(false);
        log?.LogInformation("Custom provider {Provider} saved ({Models} models, key {Source}).", id, snapshot.Find(id)?.Models.Count ?? 0, source);
        return snapshot.Find(id) ?? throw new InvalidOperationException("The provider was saved but could not be read back.");
    }

    /// <summary>Removes the custom provider <paramref name="id"/> and its stored keys; what was removed.</summary>
    public async Task<CustomProviderInfo> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var info = Current.Find(id) ?? throw new KeyNotFoundException($"There is no custom provider {id}.");
        await Store.UpdateAsync(document => CustomProviderJson.Providers(document).Remove(id), null, cancellationToken).ConfigureAwait(false);
        await RemoveKeysAsync(id).ConfigureAwait(false);
        tests.TryRemove(id, out _);
        return info;
    }

    /// <summary>Copies the custom provider <paramref name="id"/> as <c>&lt;id&gt;-2</c> (or the next free id) named "… (copy)", with its
    /// settings, headers and models; a stored key is not copied (the copy has none until one is entered).</summary>
    public async Task<CustomProviderInfo> DuplicateAsync(string id, CancellationToken cancellationToken = default)
    {
        var snapshot = Current;
        if (snapshot.Find(id) is not { } info || Entry(snapshot.Document, id) is not { } entry) throw new KeyNotFoundException($"There is no custom provider {id}.");
        var stem = CustomProviderJson.IsSlug(id) ? id : "custom";
        string copyId;
        var n = 2;
        do
        {
            var suffix = "-" + n++;
            copyId = (stem.Length + suffix.Length > 64 ? stem[..(64 - suffix.Length)] : stem) + suffix;
        }
        while (CustomProviderJson.Providers(snapshot.Document).ContainsKey(copyId) || CustomProviderJson.IsBuiltin(copyId));
        var copy = (JsonObject)entry.DeepClone();
        if (info.FileKey == CustomKeySource.Unsupported) copy.Remove("apiKey");
        CustomProviderJson.RedactHeaders(copy);
        copy["name"] = (Text(entry, "name") ?? id) + " (copy)";
        var saved = await Store.UpdateAsync(document => CustomProviderJson.Providers(document)[copyId] = copy, null, cancellationToken).ConfigureAwait(false);
        return saved.Find(copyId) ?? throw new InvalidOperationException("The copy was saved but could not be read back.");
    }

    /// <summary>Merges a models.json text into the file: its custom providers replace ones with the same id, its built-in entries'
    /// overrides are merged by model. Literal keys move into the vault; what was left out is listed in the result's warnings.</summary>
    public async Task<CustomProviderImport> ImportAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 1_000_000) throw new ArgumentException("That file is too large for a models.json (1 MB at most).");
        var secrets = CustomProviderJson.ApiKeysIn(text).ToArray();
        if (ModelsJsonConfig.Parse(text, "the imported file").Error is { } schemaError) throw new ArgumentException(SecretText.Scrub(Flatten(schemaError), secrets));
        var imported = CustomProviderJson.ParseObject(text, out var parseError) ?? throw new ArgumentException(SecretText.Scrub(parseError, secrets));
        var warnings = new List<string>();
        var keys = new List<(string Id, string Key, string Name)>();
        var entries = new List<(string Id, JsonObject Entry)>();
        int providers = 0, overrides = 0;
        foreach (var (id, node) in CustomProviderJson.Providers(imported))
        {
            if (node is not JsonObject source) continue;
            var entry = (JsonObject)source.DeepClone();
            if (CustomProviderJson.IsBuiltin(id))
            {
                if (entry.Remove("apiKey")) warnings.Add($"The apiKey of {id} was not imported: built-in providers use their accounts (Settings › Providers).");
                overrides += (entry["modelOverrides"] as JsonObject)?.Count ?? 0;
            }
            else
            {
                if (!CustomProviderJson.IsSlug(id)) warnings.Add($"{id} is not a lower-case id, so it cannot keep a stored key; rename it in its editor.");
                var (keySource, _, _) = CustomProviderJson.KeyOf(entry);
                if (keySource == CustomKeySource.Unsupported && entry["apiKey"]?.GetValue<string>() is { } key)
                {
                    entry.Remove("apiKey");
                    if (ConfigValueResolver.IsCommand(key)) warnings.Add($"The !command key of {id} was not imported: the web app does not run commands. Choose a key source in its editor.");
                    else if (CustomProviderJson.IsSlug(id) && key.Length <= 4096 && !key.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
                        keys.Add((id, key, Text(entry, "name") ?? id));
                    else warnings.Add($"The apiKey of {id} was not imported; enter it in its editor.");
                }
                providers++;
            }
            DropUnsafeHeaders(id, entry, warnings);
            entries.Add((id, entry));
        }
        if (entries.Count == 0) throw new ArgumentException("The file has no providers to import.");
        await Store.UpdateAsync(document =>
        {
            var target = CustomProviderJson.Providers(document);
            foreach (var (id, entry) in entries)
            {
                if (CustomProviderJson.IsBuiltin(id) && target[id] is JsonObject existing)
                {
                    foreach (var (field, value) in entry)
                    {
                        if (field == "modelOverrides" && value is JsonObject incoming && existing["modelOverrides"] is JsonObject current)
                            foreach (var (model, fields) in incoming) current[model] = fields?.DeepClone();
                        else existing[field] = value?.DeepClone();
                    }
                }
                else target[id] = entry.DeepClone();
            }
        }, async () =>
        {
            foreach (var (id, key, name) in keys)
            {
                if (StoredKey(id) is { } current) await accounts.ReplaceApiKeyAsync(current.Id, key, null, cancellationToken).ConfigureAwait(false);
                else await accounts.AddApiKeyAsync(new ApiKeyAccountRequest(id, key) { Label = name + " key" }, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        if (keys.Count > 0) warnings.Insert(0, $"{keys.Count} literal key{(keys.Count == 1 ? " was" : "s were")} moved into the encrypted vault and left out of models.json.");
        return new CustomProviderImport(providers, overrides, keys.Count, warnings);
    }

    private static void DropUnsafeHeaders(string id, JsonObject entry, List<string> warnings)
    {
        void Drop(JsonObject value, string where)
        {
            if (value["headers"] is not JsonObject headers) return;
            foreach (var (name, node) in headers.ToList())
            {
                var text = node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
                if (text is not null && CustomProviderJson.IsShownHeader(name, text)) continue;
                headers.Remove(name);
                warnings.Add(text is not null && ConfigValueResolver.IsCommand(text)
                    ? $"The header {name} of {where} runs a !command, so it was not imported."
                    : $"The header {name} of {where} holds a credential, so it was not imported; write it as a $VAR reference.");
            }
        }
        Drop(entry, id);
        if (entry["models"] is JsonArray models) foreach (var model in models.OfType<JsonObject>()) Drop(model, $"{id}/{Text(model, "id")}");
        if (entry["modelOverrides"] is JsonObject overrides) foreach (var (model, value) in overrides) if (value is JsonObject fields) Drop(fields, $"{id}/{model}");
    }

    /// <summary>The file as it may leave the server (a download): keys that are not references and secret-like header values removed
    /// (the app never writes either), with a header comment.</summary>
    public string Export()
    {
        var snapshot = Current;
        if (!snapshot.Valid) throw new InvalidOperationException("models.json has an error, so it cannot be exported. Fix the file first.");
        return CustomProviderJson.Format(CustomProviderJson.Redacted(snapshot.Document), snapshot.Saved ?? DateTimeOffset.UtcNow);
    }

    /// <summary>The whole file as the page previews it (redacted like <see cref="Export"/>), or null when there is none.</summary>
    public string? PreviewFile()
    {
        var snapshot = Current;
        return snapshot.Exists && snapshot.Valid ? CustomProviderJson.Format(CustomProviderJson.Redacted(snapshot.Document), snapshot.Saved ?? DateTimeOffset.UtcNow) : null;
    }

    /// <summary>Sets the override of the built-in model <paramref name="provider"/>/<paramref name="model"/>: all of it (<paramref name="fields"/>),
    /// or one field (<paramref name="field"/> set to <paramref name="value"/>, merged into the override). An empty override is removed.</summary>
    public async Task<ModelOverrideInfo?> SaveOverrideAsync(string provider, string model, JsonObject? fields, string? field, string? value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Choose a provider and a model.");
        provider = provider.Trim(); model = model.Trim();
        if (!CustomProviderJson.IsBuiltin(provider) || RuntimeModelCatalog.BuiltInOnly.Find(provider, model) is null)
            throw new ArgumentException("Choose a built-in model to override (custom providers are edited in their own editor).");
        var current = Current.Overrides.FirstOrDefault(item => item.Provider == provider && item.Model == model);
        JsonObject next;
        if (fields is not null) next = (JsonObject)fields.DeepClone();
        else
        {
            if (string.IsNullOrWhiteSpace(field) || !CustomProviderJson.OverrideFields.Contains(field.Trim()))
                throw new ArgumentException("Choose a field: " + string.Join(", ", CustomProviderJson.OverrideFields) + ".");
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Enter a value.");
            next = current is null ? new JsonObject() : (JsonObject)current.Fields.DeepClone();
            next[field.Trim()] = CustomProviderJson.ParseValue(value);
        }
        foreach (var (name, _) in next)
            if (!CustomProviderJson.OverrideFields.Contains(name)) throw new ArgumentException($"{name} cannot be overridden. Allowed fields: {string.Join(", ", CustomProviderJson.OverrideFields)}.");
        if (next["headers"] is JsonObject headers)
            foreach (var (name, node) in headers)
                if (node?.GetValueKind() != JsonValueKind.String || !CustomProviderJson.IsShownHeader(name, node.GetValue<string>()))
                    throw new ArgumentException($"The header {name} must be text, never a !command, and a $VAR reference when it carries a credential.");
        var keepHidden = current is not null && Entry(Current.Document, provider)?["modelOverrides"]?[model]?["headers"] is JsonObject fileHeaders ? fileHeaders : null;
        var saved = await Store.UpdateAsync(document =>
        {
            var providers = CustomProviderJson.Providers(document);
            if (providers[provider] is not JsonObject entry) providers[provider] = entry = new JsonObject();
            if (entry["modelOverrides"] is not JsonObject overrides) entry["modelOverrides"] = overrides = new JsonObject();
            if (next.Count == 0) overrides.Remove(model);
            else
            {
                // A header the page could not show (a secret-like value written by hand) is kept unless the new override names it.
                var written = (JsonObject)next.DeepClone();
                if (keepHidden is not null)
                    foreach (var (name, node) in keepHidden)
                        if (node?.GetValueKind() == JsonValueKind.String && !CustomProviderJson.IsShownHeader(name, node.GetValue<string>()) &&
                            (written["headers"] as JsonObject)?.ContainsKey(name) != true)
                        {
                            if (written["headers"] is not JsonObject target) written["headers"] = target = new JsonObject();
                            target[name] = node.DeepClone();
                        }
                overrides[model] = written;
            }
            Prune(providers, provider);
        }, null, cancellationToken).ConfigureAwait(false);
        return saved.Overrides.FirstOrDefault(item => item.Provider == provider && item.Model == model);
    }

    /// <summary>Removes the override of <paramref name="provider"/>/<paramref name="model"/>; false when there was none.</summary>
    public async Task<bool> RemoveOverrideAsync(string provider, string model, CancellationToken cancellationToken = default)
    {
        if (!Current.Overrides.Any(item => item.Provider == provider && item.Model == model)) return false;
        await Store.UpdateAsync(document =>
        {
            var providers = CustomProviderJson.Providers(document);
            if (providers[provider] is JsonObject entry && entry["modelOverrides"] is JsonObject overrides) overrides.Remove(model);
            Prune(providers, provider);
        }, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Drops an empty <c>modelOverrides</c> and then an empty entry of the built-in <paramref name="provider"/> (Pi refuses an
    /// entry that sets nothing).</summary>
    private static void Prune(JsonObject providers, string provider)
    {
        if (providers[provider] is not JsonObject entry) return;
        if (entry["modelOverrides"] is JsonObject { Count: 0 }) entry.Remove("modelOverrides");
        if (entry.Count == 0) providers.Remove(provider);
    }

    private async Task RemoveKeysAsync(string id)
    {
        foreach (var account in accounts.List(id).Where(account => account.Kind == AccountKind.ApiKey).ToList())
        {
            try { await accounts.RemoveAsync(account.Id, CancellationToken.None).ConfigureAwait(false); }
            catch (KeyNotFoundException) { /* Removed meanwhile. */ }
        }
    }

    // ─── Building an entry ──────────────────────────────────────────────────

    /// <summary>The provider entry <paramref name="draft"/> describes (over the existing one, so fields the editor does not manage are
    /// kept), its id, and the key typed for the vault (or null).</summary>
    internal (string Id, JsonObject Entry, string? Key) Build(CustomProviderDraft draft, CustomProviderSnapshot snapshot)
    {
        var id = draft.Id?.Trim() ?? "";
        if (!CustomProviderJson.IsSlug(id)) throw new ArgumentException("Enter a provider id: lower-case letters, digits and dashes (at most 64), starting with a letter or digit.");
        if (CustomProviderJson.IsBuiltin(id)) throw new ArgumentException($"{id} is a built-in provider. Override its models under Model overrides, or pick another id.");
        if (id == "export") throw new ArgumentException("export is reserved by the app. Pick another id.");
        var originalId = draft.OriginalId is { Length: > 0 } original ? original : null;
        if (id != originalId && CustomProviderJson.Providers(snapshot.Document).ContainsKey(id)) throw new ArgumentException($"A provider {id} already exists. Pick another id.");
        var existing = originalId is null ? null : Entry(snapshot.Document, originalId);
        var name = draft.Name?.Trim() ?? "";
        if (name.Length > 100) throw new ArgumentException("Keep the display name under 100 characters.");
        var baseUrl = draft.BaseUrl?.Trim().TrimEnd('/') ?? "";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new ArgumentException("Enter the base URL: an http or https address, such as http://localhost:1234/v1.");
        if (url.UserInfo.Length > 0) throw new ArgumentException("Leave credentials out of the base URL; use the API key or a header instead.");
        var api = draft.Api?.Trim() ?? "";
        if (!CustomProviderJson.Apis.Contains(api)) throw new ArgumentException("Choose an API type: " + string.Join(", ", CustomProviderJson.Apis) + ".");

        var entry = new JsonObject();
        if (name.Length > 0) entry["name"] = name;
        entry["baseUrl"] = baseUrl;
        entry["api"] = api;

        string? typedKey = null;
        switch (draft.Key?.Source?.Trim() ?? "none")
        {
            case "none":
                break;
            case "stored":
                if (draft.Key!.ApiKey is { } key && key.Trim().Length > 0)
                {
                    typedKey = key.Trim();
                    if (typedKey.Length > 4096 || typedKey.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
                        throw new ArgumentException("The API key contains spaces or line breaks; paste the key on its own.");
                }
                break;
            case "env":
                var variable = draft.Key!.Variable?.Trim().TrimStart('$').Trim('{', '}') ?? "";
                if (!CustomProviderJson.IsVariable(variable)) throw new ArgumentException("Enter the environment variable's name, such as LMSTUDIO_API_KEY.");
                entry["apiKey"] = "$" + variable;
                break;
            case "command":
                throw new ArgumentException("!command keys are not offered in the web app: running commands from a web form is out of scope. Store the key, or use a $VAR reference.");
            default:
                throw new ArgumentException("Choose a key source: none, stored or env.");
        }
        if (draft.AuthHeader) entry["authHeader"] = true;

        var fileHeaders = existing is null ? [] : CustomProviderJson.StringMap(existing, "headers").ToDictionary(StringComparer.Ordinal);
        var headers = new JsonObject();
        foreach (var header in draft.Headers ?? [])
        {
            var headerName = header.Name?.Trim() ?? "";
            if (headerName.Length == 0 && string.IsNullOrWhiteSpace(header.Value)) continue;
            if (!CustomProviderJson.IsHeaderName(headerName)) throw new ArgumentException($"\"{headerName}\" is not a header name.");
            if (headers.Any(pair => string.Equals(pair.Key, headerName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"The header {headerName} is listed twice.");
            string value;
            if (header.Value is null && header.Hidden)
            {
                if (!fileHeaders.TryGetValue(headerName, out var kept)) continue;
                if (ConfigValueResolver.IsCommand(kept)) throw new ArgumentException($"The header {headerName} runs a !command, which the web app does not run. Enter a value.");
                value = kept;
            }
            else
            {
                value = header.Value ?? "";
                if (value.Any(character => character is '\r' or '\n')) throw new ArgumentException($"The header {headerName} has a line break.");
                if (ConfigValueResolver.IsCommand(value)) throw new ArgumentException($"The header {headerName} starts with \"!\": commands are not run by the web app.");
                if (!CustomProviderJson.IsShownHeader(headerName, value))
                    throw new ArgumentException($"The header {headerName} carries a credential: write it as a $VAR reference (for example Bearer $MY_TOKEN), so it never sits in models.json.");
            }
            headers[headerName] = value;
        }
        if (headers.Count > 0) entry["headers"] = headers;
        if (MergeCompat(existing?["compat"] as JsonObject, draft.Compat, "the provider") is { } compat) entry["compat"] = compat;

        var models = new JsonArray();
        var oldModels = (existing?["models"] as JsonArray)?.OfType<JsonObject>().Where(model => Text(model, "id") is not null)
            .GroupBy(model => Text(model, "id")!).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal) ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in draft.Models ?? [])
        {
            var modelId = model.Id?.Trim() ?? "";
            if (modelId.Length == 0 && string.IsNullOrWhiteSpace(model.Name)) continue;
            if (modelId.Length == 0 || modelId.Length > 200 || modelId.Any(char.IsControl)) throw new ArgumentException("Every model needs an id (at most 200 characters).");
            if (!seen.Add(modelId)) throw new ArgumentException($"The model {modelId} is listed twice.");
            models.Add(BuildModel(modelId, model, oldModels.GetValueOrDefault(modelId)));
        }
        if (models.Count == 0) throw new ArgumentException("Add at least one model.");
        entry["models"] = models;

        if (existing is not null)
            foreach (var (field, value) in existing)
                if (field is not ("name" or "baseUrl" or "api" or "apiKey" or "authHeader" or "headers" or "compat" or "models")) entry[field] = value?.DeepClone();
        return (id, entry, typedKey);
    }

    private static JsonObject BuildModel(string id, CustomModelDraft draft, JsonObject? existing)
    {
        var model = new JsonObject { ["id"] = id };
        if (draft.Name?.Trim() is { Length: > 0 } name)
        {
            if (name.Length > 200) throw new ArgumentException($"Keep the name of {id} under 200 characters.");
            model["name"] = name;
        }
        model["reasoning"] = draft.Reasoning;
        model["input"] = draft.Images ? new JsonArray("text", "image") : new JsonArray("text");
        model["contextWindow"] = Tokens(draft.ContextWindow ?? 128000, id, "context window");
        model["maxTokens"] = Tokens(draft.MaxTokens ?? 16384, id, "max tokens");
        var oldCost = existing?["cost"] as JsonObject;
        model["cost"] = new JsonObject
        {
            ["input"] = Price(draft.CostInput ?? 0, id), ["output"] = Price(draft.CostOutput ?? 0, id),
            ["cacheRead"] = Number(oldCost, "cacheRead") ?? 0, ["cacheWrite"] = Number(oldCost, "cacheWrite") ?? 0,
        };
        if (draft.ThinkingLevelMap is { Count: > 0 } map)
        {
            var levels = new JsonObject();
            foreach (var level in CustomProviderJson.ThinkingLevels)
            {
                if (!map.TryGetValue(level, out var sent)) continue;
                if (sent is not null && (sent.Trim().Length == 0 || sent.Length > 64)) throw new ArgumentException($"Enter what {id} sends for the {level} level, or mark it unsupported.");
                levels[level] = sent?.Trim();
            }
            if (map.Keys.FirstOrDefault(level => !CustomProviderJson.ThinkingLevels.Contains(level)) is { } unknown) throw new ArgumentException($"{unknown} is not a thinking level.");
            if (levels.Count > 0) model["thinkingLevelMap"] = levels;
        }
        else if (draft.ThinkingLevelMap is null && existing?["thinkingLevelMap"] is { } kept) model["thinkingLevelMap"] = kept.DeepClone();
        if (MergeCompat(existing?["compat"] as JsonObject, draft.Compat, id) is { } compat) model["compat"] = compat;
        if (existing is not null)
            foreach (var (field, value) in existing)
                if (field is not ("id" or "name" or "reasoning" or "input" or "contextWindow" or "maxTokens" or "cost" or "thinkingLevelMap" or "compat"))
                    model[field] = value?.DeepClone();
        return model;
    }

    private static double Tokens(double value, string id, string what) =>
        double.IsFinite(value) && value >= 1 && value <= 100_000_000 && Math.Floor(value) == value ? value
            : throw new ArgumentException($"The {what} of {id} must be a whole number of tokens, at least 1.");

    private static double Price(double value, string id) =>
        double.IsFinite(value) && value >= 0 && value <= 100_000 ? value : throw new ArgumentException($"The prices of {id} must be 0 or more (US dollars per million tokens).");

    /// <summary>The compat object: <paramref name="existing"/> with the editor's fields of <paramref name="sent"/> set (or removed by a null);
    /// null when nothing is left.</summary>
    private static JsonObject? MergeCompat(JsonObject? existing, Dictionary<string, JsonElement?>? sent, string owner)
    {
        var compat = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        foreach (var (field, value) in sent ?? [])
        {
            if (!CustomProviderJson.EditableCompat.TryGetValue(field, out var allowed)) throw new ArgumentException($"{field} is not a compat field the editor sets.");
            if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { compat.Remove(field); continue; }
            if (allowed is null)
            {
                if (value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"{field} of {owner} is true or false.");
                compat[field] = value.Value.GetBoolean();
            }
            else
            {
                if (value.Value.ValueKind != JsonValueKind.String || !allowed.Contains(value.Value.GetString())) throw new ArgumentException($"{field} of {owner} is one of {string.Join(", ", allowed)}.");
                compat[field] = value.Value.GetString();
            }
        }
        return compat.Count == 0 ? null : compat;
    }

    /// <summary>Puts <paramref name="entry"/> at <paramref name="id"/>, in place of <paramref name="originalId"/> (same position) when given.</summary>
    private static void Place(JsonObject document, string? originalId, string id, JsonObject entry)
    {
        var providers = CustomProviderJson.Providers(document);
        if (originalId is null || originalId == id || !providers.ContainsKey(originalId))
        {
            providers[id] = entry.DeepClone();
            return;
        }
        var ordered = providers.Select(pair => KeyValuePair.Create(pair.Key == originalId ? id : pair.Key, pair.Key == originalId ? entry.DeepClone() : pair.Value?.DeepClone())).ToList();
        providers.Clear();
        foreach (var (key, value) in ordered) providers[key] = value;
    }

    private static JsonObject? Entry(JsonObject document, string id) => CustomProviderJson.Providers(document)[id] as JsonObject;

    private static string? Text(JsonObject value, string name) => value[name]?.GetValueKind() == JsonValueKind.String ? value[name]!.GetValue<string>() : null;

    private static bool Flag(JsonObject value, string name) => value[name]?.GetValueKind() == JsonValueKind.True;

    private static double? Number(JsonObject? value, string name) => value?[name]?.GetValueKind() == JsonValueKind.Number ? value[name]!.GetValue<double>() : null;

    private static string Flatten(string error) => error.Replace("\n\n", " ").Replace('\n', ' ');
}

/// <summary>The editor's preview of one provider.</summary>
/// <param name="Json">The <c>providers.&lt;id&gt;</c> fragment, or null when the draft is not valid.</param>
/// <param name="Valid">Whether models.json would be valid with it.</param>
/// <param name="Error">Why not, or null.</param>
public sealed record CustomProviderPreview(string? Json, bool Valid, string? Error);

/// <summary>What an import changed.</summary>
/// <param name="Providers">Custom providers added or replaced.</param>
/// <param name="Overrides">Model overrides merged.</param>
/// <param name="Keys">Literal keys moved into the vault.</param>
/// <param name="Warnings">What was left out, and why.</param>
public sealed record CustomProviderImport(int Providers, int Overrides, int Keys, IReadOnlyList<string> Warnings);
