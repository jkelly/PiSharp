using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

/// <summary>Version one of PiSharp's namespaced extension-state envelope, carried by a pinned custom record.
/// Data remains owned opaque JSON; interpretation and migration belong to the extension.</summary>
public sealed record SessionExtensionEntryDraft(string ExtensionId, string EntryKind, int SchemaVersion, JsonData Data);

/// <summary>The actual acknowledged store result and selected branch after that commit. No queued admission is a receipt.</summary>
public sealed record SessionExtensionEntryReceipt(SessionEntry Entry, SessionLogAppendResult Append,
    SessionContextProjection Context);

public static class SessionExtensionEntryLimits
{
    public const string CustomType = "pisharp.extension-state";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumSessionIdCharacters = 4_096;
    public const int MaximumDataCharacters = 65_536;
    public const int MaximumDataUtf8Bytes = 262_144;
}
