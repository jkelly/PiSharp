// Decision 0011 (run W2, WU7, WU8): what the chat pages and their stream fragments render (Views/Chat). Items come from PiSharp.Durable.Client's
// Transcript (the stored entries for a page, AgentModel for the stream), and each renders through the same partial, keyed by the item's
// Key, so a reattached page and its stream agree; ChatFacts adds what the entries know beyond the transcript (time, usage, failures,
// restarts, the running turn's tail). Model and user text goes through Web.Common's MarkdownRenderer (sanitized) or Razor's encoding, never
// raw. The stream also renders panels: the "Waiting for you" panel, the queue, the session details (_ChatInfo), the banners (_ChatBanners)
// and the run notices (_ChatNotices). ChatDisplay holds the small formatting helpers the partials share. W3 (S5) adds the Spend Guard's
// parts: the answers' cost marks from the ledger (ChatRenderContext.Spend), the composer's send estimate (ChatComposerView.Estimate), the
// chat's spend in the session panel (ChatInfoView.Spend) and the banner of a call stopped at a cap (ChatBannersView.Spend).
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

    /// <summary>The chat's provider (its account belongs to it).</summary>
    public string? Provider { get; init; }

    /// <summary>How the chat's provider is paid for (the footer's cost wording).</summary>
    public BillingKind Billing { get; init; } = BillingKind.Unknown;

    /// <summary>The catalog, for list prices; null leaves prices out.</summary>
    public IModelCatalog? Catalog { get; init; }

    /// <summary>The facts of the items, by item key (<see cref="ChatFacts.Build"/>).</summary>
    public IReadOnlyDictionary<string, ChatItemFacts> Facts { get; init; } = new Dictionary<string, ChatItemFacts>(StringComparer.Ordinal);

    /// <summary>The stored tool results, by call id (<see cref="ChatFacts.ToolResults"/>).</summary>
    public IReadOnlyDictionary<string, ChatToolResultMeta> Results { get; init; } = new Dictionary<string, ChatToolResultMeta>(StringComparer.Ordinal);

    /// <summary>The answers' cost marks from the spend ledger, and the running answer's reservation (empty without the Spend area).</summary>
    public ChatSpendFacts Spend { get; init; } = ChatSpendFacts.Empty;

    /// <summary>The pending approval of tool call <paramref name="callId"/>, or null.</summary>
    public PendingApproval? Approval(string callId) => Approvals.TryGetValue(callId, out var approval) ? approval : null;

    /// <summary>The facts of an item (empty when none are known).</summary>
    public ChatItemFacts FactsOf(TranscriptItem item) => item is null ? ChatItemFacts.Empty : Facts.GetValueOrDefault(item.Key) ?? ChatItemFacts.Empty;

    /// <summary>The stored result of tool call <paramref name="callId"/>, or null.</summary>
    public ChatToolResultMeta? Result(string callId) => Results.GetValueOrDefault(callId);

    /// <summary>"provider · account" for a message that <paramref name="provider"/> answered, when that is the chat's provider and the chat
    /// has an account; else null (the account is not recorded per message, so another provider's message names none).</summary>
    public string? AccountFor(string? provider) =>
        AccountLabel is { Length: > 0 } label && Provider is { Length: > 0 } own && (provider is null || provider == own) ? $"{own} · {label}" : null;
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
/// <param name="NewChat">Whether it starts a chat (no queue, no stop, no session commands).</param>
/// <param name="Thinking">The selected thinking level.</param>
/// <param name="ThinkingLevels">The levels the model supports (a model that cannot turn thinking off shows "off" disabled).</param>
/// <param name="Tier">The selected service tier, or null for the default.</param>
public sealed record ChatComposerView(bool NewChat, string Thinking, IReadOnlyList<string> ThinkingLevels, string? Tier)
{
    /// <summary>The send estimate by Send (the empty draft's, until the page asks again), or null to show none.</summary>
    public ChatSpendEstimateView? Estimate { get; init; }
}

/// <summary>The session details panel (<c>Views/Chat/_ChatInfo.cshtml</c>), rendered on the page and by the stream.</summary>
public sealed record ChatInfoView
{
    /// <summary>The chat's binding.</summary>
    public required ChatBinding Binding { get; init; }

    /// <summary>The agent shown.</summary>
    public required long AgentId { get; init; }

    /// <summary>Its name (Main, Fork #2...).</summary>
    public required string AgentName { get; init; }

    /// <summary>The catalog's provider, if it lists it.</summary>
    public CatalogProvider? Provider { get; init; }

    /// <summary>The catalog's model, if it lists it.</summary>
    public CatalogModel? Model { get; init; }

    /// <summary>The bound account's label.</summary>
    public string? AccountLabel { get; init; }

    /// <summary>About the size of the context now (the newest request's tokens); 0 when unknown.</summary>
    public double ContextTokens { get; init; }

    /// <summary>The branch's usage totals, when the stream has them.</summary>
    public UsageSummary? Usage { get; init; }

    /// <summary>The provider API of the newest answer.</summary>
    public string? Api { get; init; }

    /// <summary>Settings › Tools' approval mode.</summary>
    public ToolApprovalMode ApprovalMode { get; init; }

    /// <summary>The tools the agent's next request offers.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Of those, the ones that ask first in Ask mode (they change files or run commands).</summary>
    public IReadOnlyList<string> AskTools { get; init; } = [];

    /// <summary>The agent's own instructions (system prompt), if it has any.</summary>
    public string? Instructions { get; init; }

    /// <summary>The session's compaction settings.</summary>
    public SessionCompaction? Compaction { get; init; }

    /// <summary>The working directory.</summary>
    public required string Cwd { get; init; }

    /// <summary>The session's database file.</summary>
    public string? SessionPath { get; init; }

    /// <summary>Messages you sent in this branch's context.</summary>
    public int UserCount { get; init; }

    /// <summary>Answers in this branch's context.</summary>
    public int AssistantCount { get; init; }

    /// <summary>Tool results in this branch's context.</summary>
    public int ToolCount { get; init; }

    /// <summary>The chat's spend from the ledger and its per-chat cap, or null without the Spend area.</summary>
    public ChatSpendSummary? Spend { get; init; }
}

/// <summary>The banners under the top bar (<c>Views/Chat/_ChatBanners.cshtml</c>): no providers, the chat's sign-in expired, or its newest
/// request was stopped at a spend cap.</summary>
/// <param name="NoProviders">The catalog has no provider.</param>
/// <param name="ExpiredProvider">The provider whose account could not be used for the newest answer, or null.</param>
/// <param name="ExpiredProviderName">Its display name.</param>
/// <param name="AccountLabel">The chat's account label.</param>
/// <param name="Detail">Why, as the failure said (without the marker).</param>
public sealed record ChatBannersView(bool NoProviders, string? ExpiredProvider = null, string? ExpiredProviderName = null, string? AccountLabel = null,
    string? Detail = null)
{
    /// <summary>No banner.</summary>
    public static ChatBannersView None { get; } = new(false);

    /// <summary>The newest answer is a call the Spend Guard stopped (over a cap, or no price), or null.</summary>
    public ChatSpendStopView? Spend { get; init; }
}

/// <summary>The run notices above the composer (<c>Views/Chat/_ChatNotices.cshtml</c>): compacting, a compaction failure and an automatic
/// retry's wait.</summary>
/// <param name="Compacting">A compaction runs.</param>
/// <param name="CompactionFailure">The newest compaction failure since one last succeeded.</param>
/// <param name="RetryAt">When the failed request is retried, in Unix milliseconds.</param>
/// <param name="RetryError">Why it failed.</param>
public sealed record ChatNoticesView(bool Compacting, string? CompactionFailure, double? RetryAt, string? RetryError);

/// <summary>One entry of the session tree.</summary>
public sealed class ChatTreeNode
{
    /// <summary>The entry.</summary>
    public required long EntryId { get; init; }

    /// <summary>The entry just before it in its branch (a fork there is "before this message"), or null for the first.</summary>
    public long? PreviousId { get; init; }

    /// <summary><c>user</c>, <c>assistant</c>, <c>compaction</c> or <c>reset</c>.</summary>
    public required string Role { get; init; }

    /// <summary>A one-line preview (text, never HTML).</summary>
    public required string Text { get; init; }

    /// <summary>Tool calls an answer made.</summary>
    public int Tools { get; init; }

    /// <summary>The branch a fork or a switch at this entry uses: the shown one when it holds the entry, else the first that does.</summary>
    public long AgentId { get; set; }

    /// <summary>The branches whose newest entry this is.</summary>
    public List<long> LeafOf { get; } = [];

    /// <summary>One of those branches is running.</summary>
    public bool Running { get; set; }

    /// <summary>The shown branch's newest entry.</summary>
    public bool Current { get; set; }

    /// <summary>On the shown branch.</summary>
    public bool OnCurrentPath { get; set; }

    /// <summary>The entries after it, by id.</summary>
    public List<ChatTreeNode> Children { get; } = [];
}

/// <summary>The session tree modal's body (<c>Views/Chat/_ChatTree.cshtml</c>, from <c>GET /chat/{id}/tree</c>).</summary>
/// <param name="SessionId">The chat.</param>
/// <param name="AgentId">The branch shown.</param>
/// <param name="Roots">The first entries.</param>
/// <param name="Branches">The branches (forks and Main, not subagents).</param>
/// <param name="Running">Branches running now.</param>
/// <param name="Entries">Entries in the tree.</param>
/// <param name="Leaf">The shown branch's newest entry.</param>
/// <param name="AgentNames">Branch names, by agent.</param>
public sealed record ChatTreeView(string SessionId, long AgentId, IReadOnlyList<ChatTreeNode> Roots, int Branches, int Running, int Entries, long? Leaf,
    IReadOnlyDictionary<long, string> AgentNames);

/// <summary>A run of tree entries (<c>Views/Chat/_ChatTreeRun.cshtml</c>): the entry, its only child and so on, then the branches where they
/// split.</summary>
/// <param name="Start">The first entry of the run.</param>
/// <param name="Tree">The tree.</param>
public sealed record ChatTreeRunView(ChatTreeNode Start, ChatTreeView Tree);

/// <summary>The "kept running while you were away" toast (<c>Views/Chat/_ChatKeptRunning.cshtml</c>).</summary>
/// <param name="Chats">The chats running now.</param>
/// <param name="ActiveChat">The chat this page shows, if any (listed without a link).</param>
public sealed record ChatKeptRunningView(IReadOnlyList<RecentChat> Chats, string? ActiveChat);

/// <summary>The new chat page (<c>Views/Chat/Index.cshtml</c>).</summary>
/// <param name="Provider">The default provider, or null when the catalog has none.</param>
/// <param name="Model">The default model.</param>
/// <param name="Thinking">The default thinking level, clamped to the model.</param>
/// <param name="ThinkingLevels">The model's levels.</param>
/// <param name="WorkingDirectory">Where the chat's tools will work.</param>
public sealed record NewChatPage(string? Provider, string? Model, string Thinking, IReadOnlyList<string> ThinkingLevels, string WorkingDirectory)
{
    /// <summary>The send estimate of an empty first message, or null.</summary>
    public ChatSpendEstimateView? Estimate { get; init; }
}

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

    /// <summary>The session details.</summary>
    public required ChatInfoView Info { get; init; }

    /// <summary>The banners.</summary>
    public ChatBannersView Banners { get; init; } = ChatBannersView.None;

    /// <summary>The send estimate of an empty draft on this branch, or null.</summary>
    public ChatSpendEstimateView? Estimate { get; init; }

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

    /// <summary>A Unix-milliseconds time as this computer's local clock time (<c>HH:mm</c>, or <c>HH:mm:ss</c>); empty when unknown.</summary>
    public static string Clock(double? milliseconds, bool seconds = false) =>
        milliseconds is { } value && double.IsFinite(value) && value > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value).ToLocalTime().ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture)
            : "";

    /// <summary>A Unix-milliseconds time as an ISO 8601 instant, for <c>&lt;time datetime&gt;</c>; empty when unknown.</summary>
    public static string Iso(double? milliseconds) =>
        milliseconds is { } value && double.IsFinite(value) && value > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
            : "";

    /// <summary>A duration: <c>11 ms</c>, <c>14.2s</c> or <c>2m 05s</c>.</summary>
    public static string Duration(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return "";
        if (milliseconds < 1000) return milliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
        if (milliseconds < 60_000) return (milliseconds / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        var minutes = (long)(milliseconds / 60_000);
        return $"{minutes}m {((milliseconds - minutes * 60_000) / 1000).ToString("00", CultureInfo.InvariantCulture)}s";
    }

    /// <summary>The average output rate (PLAN.md's message throughput): output tokens ÷ (elapsed ms / 1000), one decimal; "— tokens/s" when
    /// the output tokens or a positive elapsed time are unknown.</summary>
    public static (string Text, string Label, string Title) OutputRate(double? outputTokens, double? elapsedMs)
    {
        if (outputTokens is not { } output || !double.IsFinite(output) || output < 0 || elapsedMs is not { } elapsed || !double.IsFinite(elapsed) || elapsed <= 0)
            return ("— tokens/s", "Average output rate unavailable", "Available when output-token usage and elapsed message time are known.");
        var rate = output / (elapsed / 1000);
        var text = rate.ToString("0.0", CultureInfo.InvariantCulture);
        return (text + " tokens/s", $"Average output rate: {text} tokens per second",
            $"Average output rate: {Count(output)} output tokens ÷ {(elapsed / 1000).ToString("#,0.###", CultureInfo.InvariantCulture)} seconds. Uses the displayed message duration, including wait and tool time.");
    }

    /// <summary>US dollars: four decimals under a dollar, else two.</summary>
    public static string Money(double dollars) =>
        "$" + dollars.ToString(Math.Abs(dollars) < 1 ? "0.0000" : "#,0.00", CultureInfo.InvariantCulture);

    /// <summary>A message's cost for its footer, by billing kind: a subscription's list price ("≈ $x list") or a metered price ("$x"); null for
    /// free and unknown-price providers (the Spend Guard prices those later) and when nothing was used.</summary>
    public static (string Text, string Title)? CostText(ChatEntryMeta meta, BillingKind billing, CatalogModel? model, string? account)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (billing is not (BillingKind.Subscription or BillingKind.Metered)) return null;
        var rates = model?.Cost;
        var cost = meta.Cost > 0 ? meta.Cost
            : rates is null ? 0 : (meta.Input * rates.Input + meta.Output * rates.Output + meta.CacheRead * rates.CacheRead + meta.CacheWrite * rates.CacheWrite) / 1_000_000;
        if (cost <= 0) return null;
        var formula = rates is null ? "the cost the provider layer reported for this message"
            : $"{Count(meta.Input)} in × {Rate(rates.Input)}/M + {Count(meta.Output)} out × {Rate(rates.Output)}/M" +
              (meta.CacheRead > 0 ? $" + {Count(meta.CacheRead)} cached × {Rate(rates.CacheRead)}/M" : "") +
              (meta.CacheWrite > 0 ? $" + {Count(meta.CacheWrite)} cache write × {Rate(rates.CacheWrite)}/M" : "");
        return billing == BillingKind.Subscription
            ? ("≈ " + Money(cost) + " list", $"List price at catalog rates: {formula}. Billed through the subscription{(account is null ? "" : $" ({account})")}, so it isn't counted toward money caps.")
            : (Money(cost), $"Metered at catalog rates: {formula}.");
    }

    private static string Rate(double perMillion) => "$" + perMillion.ToString("0.00##", CultureInfo.InvariantCulture);

    /// <summary>A one-line preview of a text: its lines joined with spaces, cut to <paramref name="length"/> characters.</summary>
    public static string Preview(string? text, int length = 90) =>
        Cut(string.Join(" ", (text ?? "").ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)), length);
}
