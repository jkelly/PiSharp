// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/image-resize-core.ts (DEFAULT_MAX_BYTES).
using PiSharp.Agent;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

namespace PiSharp.Cli.Commands;

/// <summary>
/// Request, tool-result and session budgets that follow Pi (owner decision 0004): a read image of up to Pi's 4.5MB of
/// base64 reaches the model, is persisted and is replayed, instead of stopping at the earlier 1 MiB profile caps.
/// Pi itself has no such limits; these remain memory bounds sized for several such images per request.
/// </summary>
internal static class PiPayloadBudget
{
    /// <summary>Pi's DEFAULT_MAX_BYTES: 4.5MB of base64 per image.</summary>
    public const int ImageBase64Characters = 4_718_592;
    /// <summary>One tool result: an image at the limit plus its note, or a truncated text result.</summary>
    public const int ToolResultCharacters = 8 * 1024 * 1024;
    /// <summary>One projected request message (a tool result or user message carrying an image).</summary>
    public const int RequestEntryCharacters = 16 * 1024 * 1024;
    /// <summary>One provider request body, which replays every image of the context.</summary>
    public const int RequestPayloadBytes = 64 * 1024 * 1024;
    /// <summary>One persisted session record and one JSONL line.</summary>
    public const int SessionRecordCharacters = 16 * 1024 * 1024;
    public const int SessionLineBytes = 64 * 1024 * 1024;
    /// <summary>A whole session file and its projected context (the session branch planner admits at most 64 MiB).</summary>
    public const int SessionFileBytes = 64 * 1024 * 1024;
    /// <summary>One RPC or JSON-mode output record (a tool_execution_end carrying an image).</summary>
    public const int OutputRecordBytes = 32 * 1024 * 1024;

    /// <summary>Tool result admission for the agent loop and its canonical retained values.</summary>
    public static ToolResultValueOptions ToolResults { get; } = ToolResultValueOptions.ExecutionBoundary with
    {
        MaximumCharacters = ToolResultCharacters, MaximumStructuredContentCharacters = ToolResultCharacters + 65_536,
        MaximumRawCharacters = 2 * ToolResultCharacters + 1024 * 1024, MaximumRawBytes = 64 * 1024 * 1024
    };

    /// <summary>The agent options with Pi-sized tool results.</summary>
    public static AgentOptions Agent(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with { Loop = (options.Loop ?? new()) with { CanonicalToolResultLimits = ToolResults }, ResultValues = ToolResults };
    }

    /// <summary>The session context projection: several Pi-sized images per branch.</summary>
    public static SessionContextProjectionOptions Context { get; } =
        new(MaximumInputCharacters: SessionFileBytes, MaximumOutputCharacters: SessionFileBytes);

    /// <summary>Session reader/writer bounds with Pi-sized records, keeping the given line and record counts.</summary>
    public static SessionLogReaderOptions SessionReader(SessionLogReaderOptions bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        return bounds with
        {
            MaximumInputBytes = SessionFileBytes, MaximumLineBytes = SessionLineBytes,
            CodecOptions = (bounds.CodecOptions ?? new()) with { MaximumRecordCharacters = SessionRecordCharacters, MaximumUtf8Bytes = SessionLineBytes }
        };
    }
}
