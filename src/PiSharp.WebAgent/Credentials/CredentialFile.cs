// Decision 0011 (run W2, WU5): credentials.json, the only place a secret is kept. Each value is encrypted with ASP.NET Core Data Protection
// (the key ring in keys\, protected with DPAPI on Windows) under one purpose per kind of secret (API keys, OAuth sign-ins) narrowed by the
// account id, so a value copied to another account, or another kind, does not decrypt. The file is never edited in place: a write goes
// to a temporary file in the same folder, is flushed to disk, and replaces the file in one move, so a crash leaves the old file or the new
// one, never half of either. A file that cannot be read is left as it is and every read and write fails, rather than overwriting the
// user's other secrets. One process owns a data folder; the parsed file is kept in memory between writes.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Contracts;

namespace PiSharp.WebAgent.Credentials;

/// <summary>The kinds of secret credentials.json holds, each with its own Data Protection purpose.</summary>
internal enum SecretKind
{
    /// <summary>An API key (<see cref="CredentialFile.ApiKeyPurpose"/>).</summary>
    ApiKey,

    /// <summary>An OAuth credential: access and refresh tokens, expiry and the flow's fields (<see cref="CredentialFile.OAuthPurpose"/>).</summary>
    OAuth,
}

/// <summary>credentials.json: encrypted secret values by account id. Thread-safe; every call is a short synchronous file operation.</summary>
internal sealed class CredentialFile
{
    /// <summary>The Data Protection purpose of API keys (narrowed by the account id).</summary>
    public const string ApiKeyPurpose = "PiSharp.WebAgent.Credentials.ApiKey.v1";

    /// <summary>The Data Protection purpose of OAuth credentials (narrowed by the account id).</summary>
    public const string OAuthPurpose = "PiSharp.WebAgent.Credentials.OAuth.v1";

    private const int Version = 1;
    private readonly Lock gate = new();
    private readonly IDataProtectionProvider protection;
    private readonly ILogger log;
    private readonly TimeProvider time;
    private Dictionary<string, Entry>? entries;

    private sealed record Entry(string Kind, string Value, string UpdatedAt);

    public CredentialFile(string path, IDataProtectionProvider protection, ILogger log, TimeProvider time)
    {
        Path = System.IO.Path.GetFullPath(path);
        this.protection = protection;
        this.log = log;
        this.time = time;
    }

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>The account ids with a stored secret.</summary>
    public IReadOnlyCollection<string> AccountIds()
    {
        lock (gate) return [.. Load().Keys];
    }

    /// <summary>The decrypted secret of <paramref name="accountId"/>, or null when it has none of <paramref name="kind"/>.</summary>
    /// <exception cref="CredentialUnavailableException">The value no longer decrypts (another key ring, a copied value).</exception>
    public string? Read(string accountId, SecretKind kind)
    {
        Entry? entry;
        lock (gate) entry = Load().GetValueOrDefault(accountId);
        if (entry is null || entry.Kind != KindName(kind)) return null;
        try { return Protector(accountId, kind).Unprotect(entry.Value); }
        catch (CryptographicException)
        {
            throw new CredentialUnavailableException(accountId,
                "Its stored credential can no longer be decrypted (the Data Protection key ring changed). Sign in again or replace the key.");
        }
    }

    /// <summary>Encrypts and stores <paramref name="plaintext"/> as the secret of <paramref name="accountId"/>, replacing any other.</summary>
    public void Write(string accountId, SecretKind kind, string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        ArgumentNullException.ThrowIfNull(plaintext);
        var value = Protector(accountId, kind).Protect(plaintext);
        lock (gate)
        {
            var next = new Dictionary<string, Entry>(Load(), StringComparer.Ordinal)
            {
                [accountId] = new(KindName(kind), value, time.GetUtcNow().ToString("O")),
            };
            Save(next);
        }
    }

    /// <summary>Removes the secret of <paramref name="accountId"/>; false when it had none.</summary>
    public bool Remove(string accountId)
    {
        lock (gate)
        {
            var current = Load();
            if (!current.ContainsKey(accountId)) return false;
            var next = new Dictionary<string, Entry>(current, StringComparer.Ordinal);
            next.Remove(accountId);
            Save(next);
            return true;
        }
    }

    /// <summary>Removes every secret whose account <paramref name="keep"/> rejects (left behind by a crash between the two stores), and
    /// returns how many went.</summary>
    public int Prune(Func<string, bool> keep)
    {
        lock (gate)
        {
            var current = Load();
            var orphans = current.Keys.Where(id => !keep(id)).ToList();
            if (orphans.Count == 0) return 0;
            var next = new Dictionary<string, Entry>(current, StringComparer.Ordinal);
            foreach (var id in orphans) next.Remove(id);
            Save(next);
            return orphans.Count;
        }
    }

    /// <summary>An OAuth credential as the JSON stored (encrypted) in the file.</summary>
    public static string SerializeOAuth(OAuthCredentialSnapshot credential)
    {
        var data = new JsonObject();
        foreach (var (key, value) in credential.ProviderData) data[key] = value;
        var json = new JsonObject();
        foreach (var (key, value) in credential.ProviderJson) json[key] = JsonNode.Parse(value.ToString());
        return new JsonObject
        {
            ["access"] = credential.Access, ["refresh"] = credential.Refresh, ["expires"] = credential.ExpiresUnixMilliseconds,
            ["data"] = data, ["json"] = json,
        }.ToJsonString();
    }

    /// <summary>The OAuth credential of <see cref="SerializeOAuth"/>.</summary>
    /// <exception cref="InvalidDataException">The JSON is not one.</exception>
    public static OAuthCredentialSnapshot DeserializeOAuth(string text)
    {
        try
        {
            var node = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("A stored sign-in is not an object.");
            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in node["data"] as JsonObject ?? []) if (value is JsonValue text1 && text1.TryGetValue<string>(out var s)) data[key] = s;
            var json = new Dictionary<string, JsonData>(StringComparer.Ordinal);
            foreach (var (key, value) in node["json"] as JsonObject ?? []) if (value is not null) json[key] = JsonData.Parse(value.ToJsonString());
            return new OAuthCredentialSnapshot(node["access"]!.GetValue<string>(), node["refresh"]!.GetValue<string>(), node["expires"]!.GetValue<long>(),
                data, json);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            throw new InvalidDataException("A stored sign-in could not be read.");
        }
    }

    private IDataProtector Protector(string accountId, SecretKind kind) =>
        protection.CreateProtector(kind == SecretKind.ApiKey ? ApiKeyPurpose : OAuthPurpose).CreateProtector(accountId);

    private static string KindName(SecretKind kind) => kind == SecretKind.ApiKey ? "apiKey" : "oauth";

    private Dictionary<string, Entry> Load()
    {
        if (entries is not null) return entries;
        if (!File.Exists(Path)) return entries = new(StringComparer.Ordinal);
        try
        {
            var root = JsonNode.Parse(File.ReadAllBytes(Path)) as JsonObject ?? throw new InvalidDataException("expected an object");
            if (root["version"]?.GetValue<int>() is not Version) throw new InvalidDataException("unknown version");
            var loaded = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var (id, value) in root["secrets"] as JsonObject ?? throw new InvalidDataException("no secrets object"))
            {
                if (value is not JsonObject entry) throw new InvalidDataException("an entry is not an object");
                loaded[id] = new(entry["kind"]!.GetValue<string>(), entry["value"]!.GetValue<string>(), entry["updatedAt"]?.GetValue<string>() ?? "");
            }
            return entries = loaded;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException or FormatException or InvalidDataException)
        {
            log.LogError("credentials.json could not be read ({Kind}); it is left as it is and no secret is read or written.", error.GetType().Name);
            throw new InvalidDataException(
                $"{Path} could not be read, so no secret is read or written. Move it away to start with no stored keys and sign-ins.", error);
        }
    }

    private void Save(Dictionary<string, Entry> next)
    {
        var secrets = new JsonObject();
        foreach (var (id, entry) in next.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            secrets[id] = new JsonObject { ["kind"] = entry.Kind, ["value"] = entry.Value, ["updatedAt"] = entry.UpdatedAt };
        var document = new JsonObject
        {
            ["version"] = Version,
            ["about"] = "PiSharp Web Agent secrets. Each value is encrypted with ASP.NET Core Data Protection (keys\\); edit nothing here.",
            ["secrets"] = secrets,
        };
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Join(directory, $".credentials.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        entries = next;
    }
}

/// <summary>An account exists but its credential cannot be used now (a variable that is not set, a value that no longer decrypts, a sign-in
/// that was never stored). Routing shows it as the chat's <c>expired</c> state; it never falls back to another account.</summary>
public sealed class CredentialUnavailableException(string accountId, string reason) : Exception(reason)
{
    /// <summary>The account.</summary>
    public string AccountId { get; } = accountId;
}
