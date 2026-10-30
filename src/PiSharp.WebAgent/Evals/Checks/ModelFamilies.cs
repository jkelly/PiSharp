// Decision 0011 (run W5, E2): model families, for the LLM judge's same-family warning (mockup modal-check-editor's ce-judge pane: "The
// target anthropic/claude-sonnet-5-5 is also a Claude model. Judges tend to favour their own family, so pick a judge from another
// provider." and "No target in this suite is a Gemini model.") and for hiding a target's names from its judge (RubricJudge). A family is
// who made the model, read from the model id first (an id names its maker whatever provider serves it: openai-codex/gpt-6.1-sol and
// openai/gpt-5.6-terra are GPT models, amazon-bedrock/us.anthropic.claude-... and openrouter/anthropic/claude-... are Claude models), then
// from the provider (anthropic, openai, google, xai, ...). A model of an unknown maker on an aggregator or a custom server has no family;
// the warning is then given only when the judge is the target itself. The brand words (Claude, GPT, Gemini, ...) are matched without case;
// the makers' names (Anthropic, OpenAI, Google, Meta, ...) only as written, so "meta tags" and "amazon river" in an answer are left alone.
using System.Text.RegularExpressions;

namespace PiSharp.WebAgent.Evals.Checks;

/// <summary>A model family: one maker's models.</summary>
/// <param name="Id">The family id: <c>claude</c>, <c>gpt</c>, <c>gemini</c>, ...</param>
/// <param name="Label">How the pages say it: <c>Claude</c>, <c>GPT</c>, <c>Gemini</c>, ...</param>
/// <param name="Brands">The family's own names, matched without case when names are hidden (<c>Claude</c>; <c>ChatGPT</c>, <c>GPT</c>).</param>
/// <param name="Makers">The maker's names, matched as written when names are hidden (<c>Anthropic</c>, <c>OpenAI</c>).</param>
public sealed record ModelFamily(string Id, string Label, IReadOnlyList<string> Brands, IReadOnlyList<string> Makers);

/// <summary>How a judge's family compares with a suite's targets (the judge pane's chip and line).</summary>
/// <param name="Same">Whether the judge shares a family with a target, or is a target itself (the chip reads <c>same family</c>, else
/// <c>different family</c>).</param>
/// <param name="Family">The judge's family label (<c>Claude</c>), or null when it is not known.</param>
/// <param name="Targets">The targets (<c>provider/model</c>) in the judge's family, or the target the judge is.</param>
/// <param name="Message">The line beside the chip.</param>
public sealed record JudgeFamilyCheck(bool Same, string? Family, IReadOnlyList<string> Targets, string Message)
{
    /// <summary>The chip's text: <c>same family</c> or <c>different family</c>.</summary>
    public string Chip => Same ? "same family" : "different family";
}

/// <summary>Model families: which family a model is, and the judge's same-family warning.</summary>
public static partial class ModelFamilies
{
    /// <summary>Every known family.</summary>
    public static IReadOnlyList<ModelFamily> All { get; } =
    [
        new("claude", "Claude", ["Claude"], ["Anthropic"]),
        new("gpt", "GPT", ["ChatGPT", "GPT", "Codex"], ["OpenAI"]),
        new("gemini", "Gemini", ["Gemini"], ["Google", "DeepMind"]),
        new("gemma", "Gemma", ["Gemma"], ["Google", "DeepMind"]),
        new("llama", "Llama", ["Llama"], ["Meta"]),
        new("mistral", "Mistral", ["Mistral", "Mixtral", "Codestral", "Devstral", "Magistral", "Ministral", "Pixtral", "Voxtral"], ["Mistral AI"]),
        new("qwen", "Qwen", ["Qwen", "QwQ", "Tongyi"], ["Alibaba"]),
        new("deepseek", "DeepSeek", ["DeepSeek"], []),
        new("grok", "Grok", ["Grok"], ["xAI"]),
        new("kimi", "Kimi", ["Kimi"], ["Moonshot AI", "Moonshot"]),
        new("glm", "GLM", ["ChatGLM", "GLM"], ["Zhipu", "Z.ai"]),
        new("minimax", "MiniMax", ["MiniMax"], []),
        new("phi", "Phi", [], ["Microsoft"]),
        new("command", "Command", [], ["Cohere"]),
        new("nova", "Nova", [], ["Amazon"]),
        new("mimo", "MiMo", ["MiMo"], ["Xiaomi"]),
        new("faux", "Faux", [], []),
    ];

    /// <summary>The family of <paramref name="model"/> served by <paramref name="provider"/>: from the model id first, then the provider;
    /// null when neither names a known maker.</summary>
    public static ModelFamily? Of(string? provider, string? model)
    {
        var id = (model ?? "").Trim().ToLowerInvariant();
        var family = id.Length == 0 ? null : FromModel(id);
        if (family is null)
        {
            var name = (provider ?? "").Trim().ToLowerInvariant() switch
            {
                "anthropic" => "claude",
                "openai" or "openai-codex" or "azure-openai-responses" => "gpt",
                "google" or "google-vertex" or "google-gemini-cli" or "google-antigravity" => "gemini",
                "xai" => "grok",
                "mistral" => "mistral",
                "deepseek" => "deepseek",
                "moonshotai" or "moonshotai-cn" or "kimi-coding" => "kimi",
                "zai" or "zai-coding-cn" => "glm",
                "minimax" or "minimax-cn" => "minimax",
                "meta" => "llama",
                "faux" => "faux",
                var other when other.StartsWith("qwen-", StringComparison.Ordinal) => "qwen",
                var other when other.StartsWith("xiaomi", StringComparison.Ordinal) => "mimo",
                _ => null,
            };
            family = name is null ? null : All.First(item => item.Id == name);
        }
        return family;
    }

    /// <summary>How the judge <paramref name="judgeProvider"/>/<paramref name="judgeModel"/> compares with <paramref name="targets"/>: the
    /// same-family warning when it shares a family with any target (or is one of them), else the "different family" line.</summary>
    public static JudgeFamilyCheck Compare(string judgeProvider, string judgeModel, IEnumerable<ModelIdentity> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var provider = judgeProvider?.Trim() ?? "";
        var model = judgeModel?.Trim() ?? "";
        var list = targets.Where(target => target is not null).ToList();
        var self = list.FirstOrDefault(target => string.Equals(target.Provider.Trim(), provider, StringComparison.Ordinal) &&
            string.Equals(target.Model.Trim(), model, StringComparison.Ordinal));
        var family = Of(provider, model);
        if (self is not null)
            return new JudgeFamilyCheck(true, family?.Label, [self.Name],
                $"The judge is also a target: {self.Name} would judge its own answers. Judges favour their own answers, so pick a judge from another provider.");
        if (family is null)
            return new JudgeFamilyCheck(false, null, [],
                $"PiSharp can't tell which family {provider}/{model} belongs to, so check by hand that no target is from the same maker.");
        var same = list.Where(target => Of(target.Provider, target.Model)?.Id == family.Id).Select(target => target.Name)
            .Distinct(StringComparer.Ordinal).ToList();
        if (same.Count == 0)
            return new JudgeFamilyCheck(false, family.Label, [], list.Count == 0
                ? $"This judge is a {family.Label} model. The suite has no targets yet."
                : $"No target in this suite is {Article(family.Label)} {family.Label} model.");
        var names = same.Count == 1 ? same[0] : string.Join(", ", same.Take(same.Count - 1)) + " and " + same[^1];
        return new JudgeFamilyCheck(true, family.Label, same, same.Count == 1
            ? $"The target {names} is also {Article(family.Label)} {family.Label} model. Judges tend to favour their own family, so pick a judge from another provider."
            : $"The targets {names} are also {family.Label} models. Judges tend to favour their own family, so pick a judge from another provider.");
    }

    private static ModelFamily? FromModel(string id)
    {
        string? name = null;
        if (id.Contains("claude", StringComparison.Ordinal)) name = "claude";
        else if (id.Contains("gemini", StringComparison.Ordinal)) name = "gemini";
        else if (id.Contains("gemma", StringComparison.Ordinal)) name = "gemma";
        else if (id.Contains("gpt", StringComparison.Ordinal) || id.Contains("codex", StringComparison.Ordinal) || id.Contains("davinci", StringComparison.Ordinal) ||
            OpenAiReasoning().IsMatch(id)) name = "gpt";
        else if (id.Contains("llama", StringComparison.Ordinal)) name = "llama";
        else if (MistralIds().IsMatch(id)) name = "mistral";
        else if (id.Contains("qwen", StringComparison.Ordinal) || id.Contains("qwq", StringComparison.Ordinal)) name = "qwen";
        else if (id.Contains("deepseek", StringComparison.Ordinal)) name = "deepseek";
        else if (id.Contains("grok", StringComparison.Ordinal)) name = "grok";
        else if (id.Contains("kimi", StringComparison.Ordinal) || id.Contains("moonshot", StringComparison.Ordinal)) name = "kimi";
        else if (id.Contains("glm", StringComparison.Ordinal)) name = "glm";
        else if (id.Contains("minimax", StringComparison.Ordinal)) name = "minimax";
        else if (PhiIds().IsMatch(id)) name = "phi";
        else if (CohereIds().IsMatch(id)) name = "command";
        else if (id.Contains("nova-", StringComparison.Ordinal) && id.Contains("amazon", StringComparison.Ordinal)) name = "nova";
        else if (id.Contains("mimo", StringComparison.Ordinal)) name = "mimo";
        return name is null ? null : All.First(family => family.Id == name);
    }

    private static string Article(string label) => label.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(label[0]), StringComparison.Ordinal) ? "an" : "a";

    // o1, o3-mini, o4-mini-high, openai/o3: OpenAI's reasoning models, whose ids name no brand.
    [GeneratedRegex(@"(^|[/.:])o\d(-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiReasoning();

    [GeneratedRegex("mistral|mixtral|codestral|devstral|magistral|ministral|pixtral|voxtral", RegexOptions.CultureInvariant)]
    private static partial Regex MistralIds();

    [GeneratedRegex(@"(^|[/.:-])phi-?\d", RegexOptions.CultureInvariant)]
    private static partial Regex PhiIds();

    [GeneratedRegex(@"(^|[/.:])(command-[ar]|command-light|aya-)", RegexOptions.CultureInvariant)]
    private static partial Regex CohereIds();
}
