// Decision 0012: the scripted models a research run uses without --provider. FauxAgentModels routes each request by the agent it is for
// (the "agent" prompt section names it) and the last tool it called. The lead delegates every part in one turn, so the researchers run in
// parallel; each researcher searches X, then the web, and answers from the results; the lead merges the answers into the report. With
// --provider a real model answers the same requests instead.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.CustomAgents.Testing;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Research;

internal static partial class ScriptedResearch
{
    private static readonly string[] Angles =
        ["background and key players", "recent developments", "what practitioners report", "risks and criticism", "costs and adoption",
         "tools and techniques", "regulation and policy", "what comes next"];

    public static void Configure(FauxAgentModels models)
    {
        // The lead: one delegate call per part, all in one turn; then the report from the researchers' answers.
        models.On(request => Is(request, ResearchRun.Lead) && !request.AfterTools, request =>
        {
            var topic = Topic(request);
            var parts = Math.Clamp(PartCount().Match(request.SystemText) is { Success: true } count ? int.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 4, 2, Angles.Length);
            return FauxReply.ToolCalls([.. Angles.Take(parts).Select(angle => ("delegate", JsonSerializer.Serialize(new
            {
                agent = ResearchRun.Researcher,
                task = $"Topic: {topic}\nPart: {angle}\nFind the main facts and what people on X say about this part.",
            })))]);
        });
        models.On(request => Is(request, ResearchRun.Lead), request =>
        {
            var report = new StringBuilder($"# {Topic(request)}\n\n(scripted) This report merges what {request.ToolResults.Count} researchers found.\n");
            foreach (var result in request.ToolResults)
            {
                var answer = Answered().Replace(result.Text.ReplaceLineEndings("\n"), "").Trim();
                report.Append("\n## ").Append(PartLine().Match(answer) is { Success: true } part ? part.Groups[1].Value : "A part").Append("\n\n")
                    .Append(PartLine().Replace(answer, "").Trim()).Append('\n');
            }
            var links = Link().Matches(string.Concat(request.ToolResults.Select(result => result.Text))).Select(match => match.Value).Distinct();
            report.Append("\n## Open questions\n\n- (scripted) Real models and searches would raise real ones.\n\n## Sources\n\n")
                .AppendJoin('\n', links.Select(link => "- " + link));
            return report.ToString();
        });

        // A researcher: X first, then the web, then an answer that links every result.
        models.On(request => Is(request, ResearchRun.Researcher) && request.LastToolName is null, request =>
            FauxReply.ToolCall(request.Tools.Contains("x_search") ? "x_search" : "web_search", JsonSerializer.Serialize(new { query = Part(request) })));
        models.On(request => Is(request, ResearchRun.Researcher) && request.LastToolName == "x_search" && request.Tools.Contains("web_search"), request =>
            FauxReply.ToolCall("web_search", JsonSerializer.Serialize(new { query = Part(request) + " facts" })));
        models.On(request => Is(request, ResearchRun.Researcher), request =>
        {
            var found = ToolTexts(request).SelectMany(text => Link().Matches(text)).Select(match => match.Value).Distinct().ToList();
            return $"Part: {Part(request)}\nFindings:\n" + string.Join("\n", found.Where(link => !link.Contains("x.com", StringComparison.Ordinal))
                    .Select(link => $"- (scripted) A finding about {Part(request)} ({link})")) +
                "\nOn X:\n" + string.Join("\n", found.Where(link => link.Contains("x.com", StringComparison.Ordinal))
                    .Select(link => $"- (scripted) A view on {Part(request)} ({link})")) +
                "\nOpen questions:\n- (scripted) none";
        });
    }

    private static bool Is(FauxRequest request, string agent) =>
        request.Sections.TryGetValue("agent", out var role) && role.Contains($"'{agent}'", StringComparison.Ordinal);

    private static string Topic(FauxRequest request) =>
        FirstUserText(request) is { } text && text.StartsWith("Research this topic: ", StringComparison.Ordinal) ? text["Research this topic: ".Length..] : "the topic";

    private static string Part(FauxRequest request) => PartLine().Match(FirstUserText(request) ?? "") is { Success: true } part ? part.Groups[1].Value : "the topic";

    /// <summary>The conversation's first user text (a researcher's is its task, inside the agent-message header).</summary>
    private static string? FirstUserText(FauxRequest request) =>
        request.Messages.OfType<JsonObjectValue>()
            .Where(message => message.TryGetValue("role", out var role) && role is JsonStringValue { Value: "user" })
            .Select(Text).FirstOrDefault();

    /// <summary>The text of every tool result so far (<see cref="FauxRequest.ToolResults"/> holds only the last round's).</summary>
    private static IEnumerable<string> ToolTexts(FauxRequest request) =>
        request.Messages.OfType<JsonObjectValue>()
            .Where(message => message.TryGetValue("role", out var role) && role is JsonStringValue { Value: "toolResult" })
            .Select(Text);

    private static string Text(JsonObjectValue message) => message.TryGetValue("content", out var content) switch
    {
        true when content is JsonStringValue text => text.Value,
        true when content is JsonArrayValue parts => string.Concat(parts.OfType<JsonObjectValue>()
            .Select(part => part.TryGetValue("text", out var value) && value is JsonStringValue piece ? piece.Value : "")),
        _ => "",
    };

    [GeneratedRegex(@"exactly (\d+) distinct parts")]
    private static partial Regex PartCount();

    [GeneratedRegex(@"^Part: (.+)$", RegexOptions.Multiline)]
    private static partial Regex PartLine();

    [GeneratedRegex(@"^\S+ answered:\n", RegexOptions.Multiline)]
    private static partial Regex Answered();

    [GeneratedRegex(@"https://[^\s)]+")]
    private static partial Regex Link();
}
