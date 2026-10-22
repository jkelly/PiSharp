// Decision 0011 (run W2, WU7): the web-approval extension, after command-guard's BeforeTool pattern (PiSharp.Durable.Hosting Apps/Extras.cs).
// Before a tool call's intent is recorded it reads the call's decision memo. With none, and when Settings › Tools needs it (Ask, or
// Allow-list with no match, and no grant of this chat), it registers the call with the ApprovalBroker and waits, cancelled by the run's
// DurableContext. The decision is stored as the call's memo (MemoAsync, first stored wins), so a crash after deciding never asks again and
// the tool runs at most once; a crash while waiting finds no memo when the hook runs again on recovery, and asks again. Read-only tools
// (ToolEffects) never ask. Deny blocks the call with the user's reason, which the model gets as the tool result. Only people's decisions are
// memos: a call that the mode, the allow-list or a grant let through stores nothing, so it is judged again if it runs again.
using System.Text.Json.Nodes;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.Contracts;
using ChordJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.WebAgent.Chat;

/// <summary>The <c>web-approval</c> extension: tool calls wait for the user's decision in the browser.</summary>
public static class WebApproval
{
    /// <summary>The extension's name.</summary>
    public const string ExtensionName = "web-approval";

    /// <summary>The memo of a call's decision: <c>{allow, choice, reason?, by}</c>.</summary>
    public const string MemoName = "web-approval";

    /// <summary>The extension for one session: <paramref name="tools"/> is read at every call (Settings › Tools), <paramref name="effects"/>
    /// tells read-only tools.</summary>
    public static Extension Create(string sessionId, ApprovalBroker broker, Func<ToolsSettings> tools, ToolEffects effects)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(effects);
        return Define.Extension(new Extension
        {
            Name = ExtensionName,
            Hooks =
            [
                Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    BeforeTool = (call, api, context) => BeforeToolAsync(sessionId, broker, tools, effects, call, api, context),
                }),
            ],
        });
    }

    /// <summary>Whether a call of <paramref name="tool"/> must wait for a person under <paramref name="settings"/> and the chat's
    /// <paramref name="grants"/>. Read-only tools are not asked about at all (the hook checks them first).</summary>
    public static bool NeedsApproval(ToolsSettings settings, IReadOnlyList<ToolAllowRule> grants, string tool, JsonObject? arguments)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(grants);
        if (settings.ApprovalMode == ToolApprovalMode.Auto) return false;
        if (ToolAllowList.Allows(grants, tool, arguments)) return false;
        return !(settings.ApprovalMode == ToolApprovalMode.AllowList && ToolAllowList.Allows(settings.AllowList ?? [], tool, arguments));
    }

    /// <summary>The block reason of a denied call of <paramref name="tool"/>.</summary>
    public static string DenyReason(string tool, string? reason) =>
        $"The user denied this {tool} call." + (string.IsNullOrWhiteSpace(reason) ? "" : " Reason: " + reason.Trim());

    private static async Task<BeforeToolDecision?> BeforeToolAsync(string sessionId, ApprovalBroker broker, Func<ToolsSettings> tools, ToolEffects effects,
        JsonObjectValue call, IHookApi api, DurableContext context)
    {
        if (!(call.TryGetValue("name", out var name) && name is JsonStringValue { Value: { Length: > 0 } tool })) return null;
        if (!effects.IsWriter(tool)) return null;
        if (await api.MemoAsync(MemoName, context).ConfigureAwait(false) is JsonObjectValue memo) return Apply(memo, tool);
        var arguments = call.TryGetValue("arguments", out var args) && args is JsonObjectValue values ? ToNode(values) : new JsonObject();
        if (!NeedsApproval(tools(), broker.Grants(sessionId), tool, arguments)) return null;
        var callId = call.TryGetValue("id", out var id) && id is JsonStringValue text ? text.Value : "";
        var parent = call.TryGetValue("parent", out var owner) && owner is JsonObjectValue nested && nested.TryGetValue("callId", out var parentId) &&
            parentId is JsonStringValue parentText ? parentText.Value : null;
        var request = new ApprovalRequest(sessionId, api.ConversationId.Value, api.TaskId.Value, callId, tool, arguments, parent);
        var decision = await broker.WaitAsync(request, context.CancellationToken).ConfigureAwait(false);
        var stored = await api.MemoAsync(MemoName, ToMemo(decision), context).ConfigureAwait(false) as JsonObjectValue;
        broker.Stored(request);
        return stored is null ? null : Apply(stored, tool);
    }

    private static JsonObjectValue ToMemo(ApprovalDecision decision)
    {
        var fields = new List<KeyValuePair<string, ChordJson>>
        {
            new("allow", decision.Allows),
            new("choice", decision.Name),
            new("by", "user"),
        };
        if (!string.IsNullOrWhiteSpace(decision.Reason)) fields.Add(new("reason", decision.Reason.Trim()));
        return ChordJson.Object(fields);
    }

    private static BeforeToolDecision? Apply(JsonObjectValue memo, string tool)
    {
        if (memo.TryGetValue("allow", out var allow) && allow is JsonBooleanValue { Value: true }) return null;
        var reason = memo.TryGetValue("reason", out var because) && because is JsonStringValue text ? text.Value : null;
        return new BeforeToolDecision(Block: DenyReason(tool, reason));
    }

    private static JsonObject ToNode(JsonObjectValue value) => JsonNode.Parse(value.ToJson()) as JsonObject ?? [];
}
