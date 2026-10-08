// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/image-resize-core.ts (DEFAULT_MAX_BYTES:
// 4.5MB of base64 per image) and packages/ai/src/api/*.ts, which bound neither requests nor streams.
namespace PiSharp.AI;

/// <summary>
/// Default request and stream bounds of the provider transports. Pi has no such limits (owner decision 0004: budgets follow
/// Pi), so these are explicit memory bounds sized for a request that replays several 4.5MB images plus a large context,
/// replacing the earlier 1 MiB defaults. Options may still configure smaller values.
/// </summary>
public static class PiRequestBudget
{
    /// <summary>One serialized request body.</summary>
    public const int RequestPayloadBytes = 64 * 1024 * 1024;
    /// <summary>One projected request message, such as a tool result or user message carrying an image.</summary>
    public const int RequestEntryCharacters = 16 * 1024 * 1024;
    /// <summary>One streamed event, content block or accumulated assistant message.</summary>
    public const int StreamCharacters = 16 * 1024 * 1024;
    /// <summary>All streamed data of one response.</summary>
    public const int StreamTotalCharacters = 64 * 1024 * 1024;
    /// <summary>The largest value an options record admits for these bounds.</summary>
    public const int MaximumBound = 256 * 1024 * 1024;
}
