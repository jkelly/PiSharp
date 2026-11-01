using System.Security.Cryptography;
using System.Text;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>Public metadata for a revocable credential. Its only permission is ticking existing schedules.</summary>
public sealed record TriggerCredential(string Id, string Label, string? ScheduleId, long CreatedAt, long ExpiresAt)
{
    /// <summary>Metadata only; never includes a secret or its hash.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object([new("id", Id), new("label", Label),
        new("scheduleId", ScheduleId is { } schedule ? schedule : JsonValue.Null), new("createdAt", CreatedAt), new("expiresAt", ExpiresAt),
        new("scopes", JsonValue.Array("schedule:trigger"))]);
}

public sealed partial class DurableDeviceHost
{
    /// <summary>Credential metadata, including expired credentials so administrators can remove them.</summary>
    public IReadOnlyList<TriggerCredential> TriggerCredentials()
    { ThrowIfDisposed(); return Catalog.TriggerCredentials().Select(item => item.Metadata).ToArray(); }

    /// <summary>Issues a trigger-only token, returned once. The catalog stores only its hash. Lifetime is one hour to one year.</summary>
    public async Task<(TriggerCredential Credential, string Token)> IssueTriggerCredentialAsync(string label, string? scheduleId = null,
        int expiresInHours = 720, CancellationToken cancellationToken = default)
    {
        label = label?.Trim() ?? "";
        if (label.Length is < 1 or > 100 || label.Any(char.IsControl)) throw new HostInvalidException("Use a label of 1 to 100 characters.");
        if (expiresInHours is < 1 or > 8760) throw new HostInvalidException("Token lifetime must be 1 to 8760 hours.");
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var restricted = scheduleId is null ? null : RequireSchedule(scheduleId).Id;
            var now = Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var credential = new TriggerCredential(Guid.NewGuid().ToString("D"), label, restricted, now, now + expiresInHours * 3_600_000L);
            var token = "dpi_trigger_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Catalog.AddTriggerCredential(credential, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            return (credential, token);
        }
        finally { mutations.Release(); }
    }

    /// <summary>Revokes a credential immediately; repeating a revocation is harmless.</summary>
    public void RevokeTriggerCredential(string id) { ThrowIfDisposed(); Catalog.RevokeTriggerCredential(id); }

    /// <summary>Authenticates a live trigger credential. Authorization to a particular schedule remains the server's responsibility.</summary>
    public TriggerCredential? AuthenticateTriggerCredential(string? token)
    {
        ThrowIfDisposed();
        if (token is null || !token.StartsWith("dpi_trigger_", StringComparison.Ordinal) || token.Length > 256) return null;
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var now = Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return Catalog.TriggerCredentials().FirstOrDefault(item => item.Metadata.ExpiresAt > now &&
            CryptographicOperations.FixedTimeEquals(actual, item.Hash)).Metadata;
    }
}
