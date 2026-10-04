using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

/// <summary>Prospective immutable state for trusted policy/output admission. It acknowledges no write.</summary>
public sealed record SessionContextEditPreview(SessionEntry Entry, SessionContextProjection Context,
    SessionLogStoreSnapshot PreviousLog);

/// <summary>One actual append checkpoint and the future model context after the acknowledged edit.</summary>
public sealed record SessionContextEditReceipt(SessionEntry Entry, SessionLogAppendResult Append,
    SessionContextProjection Context);
