using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.AI.Authentication.OAuth;

/// <summary>An immutable, already admitted credential. Construction acquires no credentials.</summary>
public sealed class OAuthCredentialSnapshot
{
    public OAuthCredentialSnapshot(string access, string refresh, long expiresUnixMilliseconds,
        IReadOnlyDictionary<string, string>? providerData = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(refresh);
        Access = access; Refresh = refresh; ExpiresUnixMilliseconds = expiresUnixMilliseconds;
        ProviderData = (providerData ?? new Dictionary<string, string>()).ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>A credential whose provider fields also hold non-string JSON values (auth.json keeps every field a flow returns,
    /// e.g. GitHub Copilot's availableModelIds array or Sign in with ChatGPT's scopes).</summary>
    public OAuthCredentialSnapshot(string access, string refresh, long expiresUnixMilliseconds,
        IReadOnlyDictionary<string, string>? providerData, IReadOnlyDictionary<string, PiSharp.Contracts.JsonData>? providerJson)
        : this(access, refresh, expiresUnixMilliseconds, providerData)
    {
        ProviderJson = (providerJson ?? new Dictionary<string, PiSharp.Contracts.JsonData>()).ToImmutableDictionary(StringComparer.Ordinal);
    }

    [JsonIgnore] public string Access { get; }
    [JsonIgnore] public string Refresh { get; }
    public long ExpiresUnixMilliseconds { get; }
    [JsonIgnore] public System.Collections.Immutable.ImmutableDictionary<string, string> ProviderData { get; }
    /// <summary>Non-string provider fields, as owned JSON values. Empty for flows that store only strings.</summary>
    [JsonIgnore] public System.Collections.Immutable.ImmutableDictionary<string, PiSharp.Contracts.JsonData> ProviderJson { get; } =
        System.Collections.Immutable.ImmutableDictionary<string, PiSharp.Contracts.JsonData>.Empty;
    public override string ToString() => "OAuth credential snapshot (material withheld)";
}

/// <summary>
/// Explicit injected dependency only. An implementation must serialize ModifyAsync per provider,
/// re-read the authoritative snapshot inside that serialization, and publish a returned replacement
/// only after the callback completes successfully and the supplied token remains active.
/// A null callback result means no change; return the authoritative current snapshot in that case.
/// Cancellation must not release serialization until original callback work settles.
/// StoredOAuthLifecycle supplies a token that cancels only the serialization wait: it stops
/// forwarding caller cancellation once the callback starts, so a started refresh is persisted.
/// This leaf supplies no persistent implementation and must be exercised with synthetic inputs only.
/// </summary>
public interface IAdmittedOAuthCredentialSource
{
    Task<OAuthCredentialSnapshot?> ReadAsync(string provider, CancellationToken cancellationToken);
    Task<OAuthCredentialSnapshot?> ModifyAsync(string provider,
        Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation,
        CancellationToken cancellationToken);
}

/// <summary>Explicit synthetic refresh admission. No built-in provider request or acquisition.</summary>
public interface IAdmittedOAuthRefresh
{
    Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current,
        CancellationToken cancellationToken);
}

public enum OAuthLifecycleFailure { Read, Modify, Refresh, MinimumValidity, Reauthentication, CallbackReentry }

/// <summary>Redacted diagnostic retaining the exact original fault for its owner.</summary>
[JsonConverter(typeof(OAuthLifecycleExceptionJsonConverter))]
public sealed class OAuthLifecycleException : Exception
{
    public OAuthLifecycleException(OAuthLifecycleFailure failure, Exception? originalException = null,
        AggregateException? originalTaskException = null)
        : base("Stored OAuth lifecycle failed.")
    {
        Failure = failure; OriginalException = originalException; OriginalTaskException = originalTaskException;
        OriginalTaskExceptions = originalTaskException is null ? [] : [originalTaskException];
    }

    public OAuthLifecycleFailure Failure { get; }
    [JsonIgnore] public Exception? OriginalException { get; }
    [JsonIgnore] public AggregateException? OriginalTaskException { get; }
    /// <summary>Complete task-fault witnesses at every original dependency boundary, without flattening identities.</summary>
    [JsonIgnore] public ImmutableArray<AggregateException> OriginalTaskExceptions { get; private init; }
    [JsonIgnore] public OAuthLifecycleException? OriginalLifecycleException { get; private init; }
    internal OAuthLifecycleException RetainOriginalTask(AggregateException? additional) => new(Failure, OriginalException, OriginalTaskException)
    {
        OriginalLifecycleException = this,
        OriginalTaskExceptions = additional is null ? OriginalTaskExceptions : OriginalTaskExceptions.Add(additional),
    };
    public override string ToString() => $"Stored OAuth lifecycle failed ({Failure}).";
}

/// <summary>Exception base properties can expose reflection/fault material; serialize only the diagnostic.</summary>
public sealed class OAuthLifecycleExceptionJsonConverter : JsonConverter<OAuthLifecycleException>
{
    public override OAuthLifecycleException Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("OAuth lifecycle faults cannot be reconstructed from diagnostics.");
    public override void Write(Utf8JsonWriter writer, OAuthLifecycleException value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("failure", value.Failure.ToString()); writer.WriteEndObject();
    }
}
