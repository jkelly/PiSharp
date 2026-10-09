using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;

namespace PiSharp.Sessions.Compaction;

public enum SessionSummaryKind { History, TurnPrefix, Branch }
public sealed record SessionSummaryRequest(SessionSummaryKind Kind, ModelDescriptor Model, string SystemPrompt,
    string Prompt, double MaximumOutputTokens, string? ThinkingLevel, string SessionId, string CacheRetention = "none");
public sealed record SessionSummaryRequestOptions(double ModelMaximumTokens = 0, bool ModelSupportsReasoning = false,
    string? ThinkingLevel = null, string? CustomInstructions = null, bool ReplaceBranchInstructions = false);

public static class SessionSummaryRequestBuilder
{
    public static SessionSummaryRequest History(SessionCompactionPlan plan, ModelDescriptor model, string sessionId,
        SessionSummaryRequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan); options ??= new();
        var previous = !string.IsNullOrEmpty(plan.PreviousSummary);
        var prompt = "<conversation>\n" + SerializeConversation(plan.MessagesToSummarize) + "\n</conversation>\n\n";
        if (previous) prompt += "<previous-summary>\n" + plan.PreviousSummary + "\n</previous-summary>\n\n";
        prompt += Focus(previous ? SessionSummaryTemplates.UpdatePrompt : SessionSummaryTemplates.InitialPrompt, options.CustomInstructions);
        return Request(SessionSummaryKind.History, model, sessionId, prompt, Math.Floor(0.8 * plan.Settings.ReserveTokens), options);
    }
    public static SessionSummaryRequest TurnPrefix(SessionCompactionPlan plan, ModelDescriptor model, string sessionId,
        SessionSummaryRequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan); options ??= new();
        var prompt = "# Conversation\n" + SerializeConversation(plan.TurnPrefixMessages) + "\n\n# Instructions\n" + SessionSummaryTemplates.TurnPrefixPrompt;
        return Request(SessionSummaryKind.TurnPrefix, model, sessionId, prompt, Math.Floor(0.5 * plan.Settings.ReserveTokens), options);
    }
    public static SessionSummaryRequest Branch(SessionBranchSummaryPlan plan, ModelDescriptor model, string sessionId,
        SessionSummaryRequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan); options ??= new();
        var instructions = options.ReplaceBranchInstructions && !string.IsNullOrEmpty(options.CustomInstructions)
            ? options.CustomInstructions : Focus(SessionSummaryTemplates.BranchPrompt, options.CustomInstructions);
        // Source branch requests do not forward the optional thinking level.
        return Request(SessionSummaryKind.Branch, model, sessionId,
            "<conversation>\n" + SerializeConversation(plan.Messages) + "\n</conversation>\n\n" + instructions,
            4096, options with { ThinkingLevel = null });
    }
    public static string SerializeConversation(ImmutableArray<TranscriptEntry> messages)
    {
        if (messages.IsDefault || messages.Length > 100_000) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
        var parts = new List<string>(); long characters = 0;
        foreach (var original in messages)
        {
            var message = SessionContextInfluenceProjector.ToLlm(original); if (message is null) continue;
            var body = message.WireBody.Value; var content = body.GetProperty("content");
            if (message.Role == "user") { var text = Text(content, ""); if (text.Length != 0) Add("[User]: " + text); }
            else if (message.Role == "assistant")
            {
                var thinking = content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "thinking").Select(block => block.GetProperty("thinking").GetString()!).ToArray();
                if (thinking.Length != 0) Add("[Assistant thinking]: " + string.Join('\n', thinking));
                if (content.EnumerateArray().Any(block => block.GetProperty("type").GetString() == "text")) Add("[Assistant]: " + Text(content, "\n"));
                var calls = content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "toolCall")
                    .Select(block => block.GetProperty("name").GetString() + "(" + string.Join(", ", SessionSummaryJson.Properties(block.GetProperty("arguments"))
                        .Select(property => property.Name + "=" + SessionSummaryJson.Stringify(property.Value))) + ")").ToArray();
                if (calls.Length != 0) Add("[Assistant tool calls]: " + string.Join("; ", calls));
            }
            else if (message.Role == "toolResult")
            {
                var text = Text(content, ""); if (text.Length == 0) continue;
                Add("[Tool result]: " + (text.Length <= 2000 ? text : text[..2000] + "\n\n[... " + (text.Length - 2000) + " more characters truncated]"));
            }
        }
        return string.Join("\n\n", parts);
        void Add(string text) { characters += text.Length; if (characters > 8_388_608) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit); parts.Add(text); }
    }
    private static string Text(JsonElement content, string separator) => content.ValueKind == JsonValueKind.String
        ? content.GetString()! : string.Join(separator, content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString()!));
    private static string Focus(string prompt, string? instructions) => string.IsNullOrEmpty(instructions) ? prompt : prompt + "\n\nAdditional focus: " + instructions;
    private static SessionSummaryRequest Request(SessionSummaryKind kind, ModelDescriptor model, string sessionId, string prompt,
        double maximum, SessionSummaryRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (!double.IsFinite(maximum) || maximum < 0 || !double.IsFinite(options.ModelMaximumTokens) || options.ModelMaximumTokens < 0 ||
            prompt.Length > 8_388_608 || options.CustomInstructions?.Length > 65_536 || sessionId.Length > 4096)
            throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
        return new(kind, model, SessionSummaryTemplates.SystemPrompt, prompt,
            options.ModelMaximumTokens > 0 ? Math.Min(maximum, options.ModelMaximumTokens) : maximum,
            options.ModelSupportsReasoning && options.ThinkingLevel is not (null or "" or "off") ? options.ThinkingLevel : null, sessionId);
    }
    public static string BranchPreamble => SessionSummaryTemplates.BranchPreamble;
}
