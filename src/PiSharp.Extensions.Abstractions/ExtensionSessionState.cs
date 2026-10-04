using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Extensions;

public sealed record ExtensionSessionStateValue(string EntryId, string EntryKind, int SchemaVersion, JsonData Data);
public enum ExtensionSessionStateFailure { InvalidEnvelope, IncompatibleVersion }
public sealed class ExtensionSessionStateException(ExtensionSessionStateFailure failure) : Exception(failure switch
{
    ExtensionSessionStateFailure.IncompatibleVersion => "The selected branch contains incompatible extension-state data; the extension owns explicit migration.",
    _ => "The selected branch contains malformed extension-state data."
})
{
    public ExtensionSessionStateFailure Failure { get; } = failure;
}

/// <summary>Reads only the acknowledged active ancestry supplied by the host. State is namespaced to the
/// callback owner and entry kind. The latest matching record wins; a future version is an explicit error.
/// No record, file or unknown data is rewritten, and no CLR type/delegate is persisted.</summary>
public static class ExtensionSessionState
{
    public const string CustomType = "pisharp.extension-state";
    public static ExtensionSessionStateValue? ReadLatest(this IExtensionSessionContext context, string entryKind,
        int expectedSchemaVersion = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(entryKind) || entryKind.Length > 128 ||
            entryKind.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('.' or '_' or '-')) || expectedSchemaVersion < 1)
            throw new ArgumentException("State requires a bounded entry kind and positive schema version.", nameof(entryKind));
        cancellationToken.ThrowIfCancellationRequested();
        context.OperationCancellationToken.ThrowIfCancellationRequested(); context.SessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        var snapshot = context.SessionSnapshot ?? throw new InvalidOperationException("Extension state requires an active acknowledged branch.");
        JsonData? latest = null;
        foreach (var record in snapshot.BranchEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = record.Value;
            if (entry.ValueKind != JsonValueKind.Object || !String(entry, "type", out var type) || type != "custom" ||
                !String(entry, "customType", out var kind) || kind != CustomType ||
                !entry.TryGetProperty("data", out var envelope) || envelope.ValueKind != JsonValueKind.Object ||
                !String(envelope, "extensionId", out var owner) || owner != context.OwnerId ||
                !String(envelope, "entryKind", out var stateKind) || stateKind != entryKind) continue;
            latest = record;
        }
        if (latest is null) return null;
        var value = latest.Value; var data = value.GetProperty("data");
        if (!String(value, "id", out var id) || string.IsNullOrEmpty(id) ||
            !data.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var schema) || schema < 1 || !data.TryGetProperty("data", out var payload))
            throw new ExtensionSessionStateException(ExtensionSessionStateFailure.InvalidEnvelope);
        if (schema != expectedSchemaVersion) throw new ExtensionSessionStateException(ExtensionSessionStateFailure.IncompatibleVersion);
        cancellationToken.ThrowIfCancellationRequested();
        context.OperationCancellationToken.ThrowIfCancellationRequested(); context.SessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        return new(id!, entryKind, schema, JsonData.FromElement(payload));
    }
    private static bool String(JsonElement value, string name, out string? text)
    {
        text = null;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        text = property.GetString(); return true;
    }
}
