// Decision 0011 (run W2, WU7): what the chat pages and their stream fragments render (Views/Chat). Items come from PiSharp.Durable.Client's
// Transcript (the stored entries for a page, AgentModel for the stream), and each renders through the same partial, keyed by the item's
// Key, so a reattached page and its stream agree. Model and user text goes through Web.Common's MarkdownRenderer (sanitized) or Razor's
// encoding, never raw. ChatDisplay holds the small formatting helpers the partials share.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Chat;

/// <summary>What every item of one agent's transcript renders with.</summary>
public sealed class ChatRenderContext
{
    /// <summary>The chat (session) id.</summary>
    public required string SessionId { get; init; }

    /// <summary>The agent shown.</summary>
    public required long AgentId { get; init; }

    /// <summary>The sanitizing Markdown renderer.</summary>
    public required MarkdownRenderer Markdown { get; init; }

    /// <summary>The chat's pending approvals of this agent, by tool call id (rendered inline in their tool card).</summary>
    public IReadOnlyDictionary<string, PendingApproval> Approvals { get; init; } = new Dictionary<string, PendingApproval>(StringComparer.Ordinal);

    /// <summary>The bound account's label, shown on hover in an answer's footer; null when the chat has none.</summary>
    public string? AccountLabel { get; init; }

    /// <summary>The pending approval of tool call <paramref name="callId"/>, or null.</summary>
    public PendingApproval? Approval(string callId) => Approvals.TryGetValue(callId, out var approval) ? approval : null;
}

/// <summary>One transcript item (<c>Views/Chat/_ChatItem.cshtml</c>).</summary>
/// <param name="Item">The item.</param>
/// <param name="Context">The agent's render context.</param>
public sealed record ChatItemView(TranscriptItem Item, ChatRenderContext Context);

/// <summary>One tool call card (<c>Views/Chat/_ChatToolCall.cshtml</c>).</summary>
/// <param name="Call">The call.</param>
/// <param name="Context">The agent's render context.</param>
/// <param name="Nested">Whether the call is nested in another.</param>
public sealed record ChatToolView(ToolCallView Call, ChatRenderContext Context, bool Nested);

/// <summary>An approval prompt (<c>Views/Chat/_ChatApproval.cshtml</c>), inline in its tool card or in the pending panel.</summary>
/// <param name="Approval">The waiting call.</param>
/// <param name="Inline">Whether it renders inside the call's tool card.</param>
public sealed record ChatApprovalView(PendingApproval Approval, bool Inline);

/// <summary>The "Waiting for you" panel (<c>Views/Chat/_ChatPending.cshtml</c>): question forms and plan reviews of the agent, and approvals
/// that have no tool card in view (another agent's, or a nested call's).</summary>
/// <param name="Calls">Waiting <c>ask_user_question</c> and <c>exit_plan_mode</c> calls.</param>
/// <param name="Approvals">Approvals not shown inline.</param>
/// <param name="AgentId">The agent the forms answer.</param>
public sealed record ChatPendingView(IReadOnlyList<PendingCall> Calls, IReadOnlyList<PendingApproval> Approvals, long AgentId);

/// <summary>The composer (<c>Views/Chat/_ChatComposer.cshtml</c>).</summary>
/// <param name="NewChat">Whether it starts a chat (no queue, no stop).</param>
/// <param name="Thinking">The selected thinking level.</param>
/// <param name="ThinkingLevels">The levels the model supports.</param>
/// <param name="Tier">The selected service tier, or null for the default.</param>
public sealed record ChatComposerView(bool NewChat, string Thinking, IReadOnlyList<string> ThinkingLevels, string? Tier);

/// <summary>The new chat page (<c>Views/Chat/Index.cshtml</c>).</summary>
/// <param name="Provider">The default provider, or null when the catalog has none.</param>
/// <param name="Model">The default model.</param>
/// <param name="Thinking">The default thinking level, clamped to the model.</param>
/// <param name="ThinkingLevels">The model's levels.</param>
/// <param name="WorkingDirectory">Where the chat's tools will work.</param>
public sealed record NewChatPage(string? Provider, string? Model, string Thinking, IReadOnlyList<string> ThinkingLevels, string WorkingDirectory);

/// <summary>A chat page (<c>Views/Chat/Session.cshtml</c>): the stored transcript of one agent, which the stream then catches up.</summary>
public sealed record ChatPage
{
    /// <summary>The chat's binding.</summary>
    public required ChatBinding Binding { get; init; }

    /// <summary>The agent shown.</summary>
    public required HostedAgent Agent { get; init; }

    /// <summary>The session's agents (branches and subagents).</summary>
    public required IReadOnlyList<HostedAgent> Agents { get; init; }

    /// <summary>The stored transcript of <see cref="Agent"/>.</summary>
    public required IReadOnlyList<TranscriptItem> Items { get; init; }

    /// <summary>The render context of <see cref="Items"/>.</summary>
    public required ChatRenderContext Context { get; init; }

    /// <summary>The working directory.</summary>
    public required string Cwd { get; init; }

    /// <summary>The catalog's provider of the binding, if it lists it.</summary>
    public CatalogProvider? Provider { get; init; }

    /// <summary>The catalog's model of the binding, if it lists it.</summary>
    public CatalogModel? Model { get; init; }

    /// <summary>The bound account, when the chat has one the accounts know.</summary>
    public ChatAccount? Account { get; init; }

    /// <summary>The thinking levels the model supports.</summary>
    public IReadOnlyList<string> ThinkingLevels { get; init; } = ["off"];

    /// <summary>Settings › Tools' approval mode, for the info line.</summary>
    public ToolApprovalMode ApprovalMode { get; init; }

    /// <summary>Whether a run is going (the composer shows Stop).</summary>
    public bool Busy { get; init; }

    /// <summary>The <c>chat/{session}/{agent}</c> stream key.</summary>
    public string StreamKey => ChatStreamMux.AgentKey(Binding.SessionId, Agent.Id);
}

/// <summary>Formatting helpers of the chat partials.</summary>
public static class ChatDisplay
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The service tiers the composer's Speed offers (value, label); the empty value is the provider's default.</summary>
    public static IReadOnlyList<(string Value, string Label)> Tiers { get; } =
    [
        ("", "Speed: default"), ("auto", "Speed: Auto"), ("default", "Speed: Standard"), ("flex", "Speed: Flex"), ("scale", "Speed: Scale"),
        ("priority", "Speed: Priority"), ("fast", "Speed: Fast"), ("ultrafast", "Speed: UltraFast"),
    ];

    /// <summary>A string property, or null.</summary>
    public static string? Str(JsonObject? json, string key) => json?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A number property as a whole number, or null.</summary>
    public static long? Long(JsonObject? json, string key) => json?[key] is JsonValue value && value.TryGetValue<double>(out var number) ? (long)number : null;

    /// <summary>A boolean property (false when missing).</summary>
    public static bool Bool(JsonObject? json, string key) => json?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>The objects of an array property.</summary>
    public static IEnumerable<JsonObject> Objects(JsonObject? json, string key) => json?[key] is JsonArray array ? array.OfType<JsonObject>() : [];

    /// <summary>A one-line summary of tool arguments: a lone string argument as it is, else compact JSON; cut to 140 characters.</summary>
    public static string ArgsSummary(JsonNode? args)
    {
        if (args is null) return "";
        if (args is JsonObject json && json.Count == 1 && json.First().Value is JsonValue only && only.TryGetValue<string>(out var text))
            return Cut(text.ReplaceLineEndings(" "), 140);
        if (args is JsonObject { Count: 0 }) return "";
        return Cut(args.ToJsonString(), 140);
    }

    /// <summary>Indented JSON.</summary>
    public static string Pretty(JsonNode? node) => node?.ToJsonString(Indented) ?? "";

    /// <summary><paramref name="text"/> cut to <paramref name="length"/> characters with an ellipsis.</summary>
    public static string Cut(string text, int length) => text.Length > length ? text[..(length - 1)] + "…" : text;

    /// <summary>A chat title from its first message: the first line, cut to 60 characters; "New chat" when blank.</summary>
    public static string TitleFrom(string text)
    {
        var line = (text ?? "").ReplaceLineEndings("\n").Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? "";
        return line.Length == 0 ? "New chat" : Cut(line, 60);
    }

    /// <summary>The Bootstrap Icons class of a tool.</summary>
    public static string ToolIcon(string tool) => tool switch
    {
        "bash" or "powershell" => "bi-terminal",
        "read" => "bi-file-earmark-code",
        "write" => "bi-file-earmark-plus",
        "edit" => "bi-pencil-square",
        "ask_user_question" => "bi-question-circle",
        "exit_plan_mode" => "bi-journal-check",
        "subagent" => "bi-diagram-3",
        _ => "bi-tools",
    };

    /// <summary>The thinking levels the durable host accepts, of those a model lists (<c>max</c> is not one of them yet).</summary>
    public static IReadOnlyList<string> HostThinkingLevels(IEnumerable<string>? levels)
    {
        var accepted = new[] { "off", "minimal", "low", "medium", "high", "xhigh" };
        var list = (levels ?? ["off"]).Where(level => accepted.Contains(level, StringComparer.Ordinal)).ToList();
        return list.Count == 0 ? ["off"] : list;
    }

    /// <summary>A label for a thinking level.</summary>
    public static string ThinkingLabel(string level) => "Thinking: " + level;

    /// <summary>A whole number with thousands separators.</summary>
    public static string Count(double value) => value.ToString("#,0", CultureInfo.InvariantCulture);
}
