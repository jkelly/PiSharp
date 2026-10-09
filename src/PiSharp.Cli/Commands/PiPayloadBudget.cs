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
    /// <summary>One persisted session record (one JSONL line).</summary>
    public const int SessionLineBytes = 64 * 1024 * 1024;
    /// <summary>A whole session file and its projected context (the session branch planner admits at most 64 MiB).</summary>
    public const int SessionFileBytes = 64 * 1024 * 1024;
    /// <summary>One RPC or JSON-mode output record (a tool_execution_end carrying an image).</summary>
    public const int OutputRecordBytes = 32 * 1024 * 1024;
    /// <summary>One RPC input command (a prompt carrying Pi-sized images).</summary>
    public const int RpcCommandBytes = 32 * 1024 * 1024;

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

    /// <summary>Pi entry tool results: agent-loop.ts keeps every content block of a result (the 128-block profile bound is lifted);
    /// the character and byte bounds stay the memory bounds above.</summary>
    public static ToolResultValueOptions PiToolResults { get; } = ToolResults with { MaximumContentBlocks = int.MaxValue };

    /// <summary>agent.ts steer/followUp push onto plain arrays: the Pi entry's queues have no message-count or size bound.</summary>
    public static AgentPendingInputQueueOptions PiQueue { get; } = new(MaximumMessagesPerQueue: int.MaxValue,
        MaximumMessageCharacters: int.MaxValue, MaximumCharactersPerQueue: long.MaxValue, MaximumJsonDepth: 64);

    /// <summary>agent-loop.ts emits every tool_execution_update an onUpdate reports: no count bound per batch, per pending delivery or
    /// per update's content blocks. The retained characters of not-yet-delivered updates stay a memory bound.</summary>
    public static ToolProgressDeliveryOptions PiProgress { get; } = new(ToolProgressDeliveryMode.SourceCompatible,
        MaximumUpdates: int.MaxValue, MaximumPendingUpdates: int.MaxValue, MaximumContentBlocks: int.MaxValue);

    /// <summary>agent-session.ts prompt/sendUserMessage: an extension's user message has no text or image-count bound; the message keeps
    /// the request-entry and payload memory bounds every prompt has (the record's other defaults).</summary>
    public static PromptInputAdmissionOptions PiExtensionInput { get; } = new(
        MaximumTextCharacters: PiSharp.AI.PiRequestBudget.RequestEntryCharacters, MaximumImages: int.MaxValue, MaximumJsonDepth: 64);

    /// <summary>The agent options of the Pi entry: no tool, subscriber, queue, progress or result-block count bound (agent.ts has none).</summary>
    public static AgentOptions PiAgent(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            Loop = (options.Loop ?? new()) with { CanonicalToolResultLimits = PiToolResults }, ResultValues = PiToolResults,
            MaximumTools = int.MaxValue, MaximumSubscribers = int.MaxValue, Queue = PiQueue, ProgressDelivery = PiProgress
        };
    }

    /// <summary>The Pi entry's tool invoker: every registered tool, every transform, every assistant and result content block.</summary>
    public static ToolInvokerOptions PiInvoker(ToolInvokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            MaximumTools = int.MaxValue, MaximumTransforms = int.MaxValue, MaximumAssistantContentBlocks = int.MaxValue,
            MaximumResultContentBlocks = int.MaxValue
        };
    }

    /// <summary>runner.ts emits input, before_agent_start, context and tool events with whatever the session holds: no text, JSON,
    /// image, concurrency or context-message bound. The reentrant dispatch depth stays a recursion guard.</summary>
    public static PiSharp.Extensions.Runtime.Dispatch.ExtensionEventDispatchOptions PiEventDispatch { get; } = new(
        MaximumTextCharacters: int.MaxValue, MaximumJsonCharacters: int.MaxValue, MaximumJsonBytes: int.MaxValue, MaximumJsonDepth: 64,
        MaximumImages: int.MaxValue, MaximumConcurrentDispatches: int.MaxValue, MaximumContextMessages: int.MaxValue);

    /// <summary>An extension or MCP tool binding of the Pi entry: every registered tool (formerly 128), Pi-sized tool results (formerly
    /// 65,536 characters and 128 blocks), every event handler, and declarations bounded only by the request payload.</summary>
    public static PiSharp.Extensions.Agent.ExtensionAgentBindingOptions PiBinding(PiSharp.Extensions.Agent.ExtensionAgentBindingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            MaximumTools = int.MaxValue, MaximumDeclarationCharacters = RequestPayloadBytes, MaximumDeclarationBytes = RequestPayloadBytes,
            ResultValues = PiToolResults, ToolEventDispatch = PiEventDispatch, MaximumToolEventHandlers = int.MaxValue
        };
    }

    /// <summary>skills.ts loads every skill under every skill path, whatever their number or size. A skill file is read whole, so its
    /// read keeps the 64 MiB memory bound the read tool has; the directory depth keeps the loader's recursion guard.</summary>
    public static PiSharp.CodingAgent.Resources.Skills.SkillResourceOptions PiSkills { get; } = new(MaximumFiles: int.MaxValue,
        MaximumEntries: int.MaxValue, MaximumDepth: 64, MaximumFileBytes: 64 * 1024 * 1024, MaximumTotalBytes: int.MaxValue);

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
            // session-manager.ts writes and reads JSON.stringify lines of any size or depth: one record keeps only the line's memory bound
            // (characters never exceed its UTF-8 bytes) and the 64 levels an owned JSON value holds.
            CodecOptions = (bounds.CodecOptions ?? new()) with { MaximumRecordCharacters = SessionLineBytes, MaximumUtf8Bytes = SessionLineBytes, MaximumJsonDepth = 64 }
        };
    }
}
