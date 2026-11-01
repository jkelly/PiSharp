// Decision 0011 (run W5, E2): the judge never sees the target's provider or model name (mockup ce-judge: "The judge ... never sees the
// target's provider or model name"; contracts: "never sees the target's provider or model name"). Before a judgement is sent, its rubric,
// the case's request and the answer are rewritten: the target's names become [provider] and [model]. The names are the target's provider id
// and its parts (openai-codex: openai, codex; generic parts such as responses, coding, cli are kept), the provider's display name, the model
// id and its '/' parts (openrouter's anthropic/claude-...), the model's catalog name, the model the provider said answered (responseModel),
// and the family's brand words (Claude; GPT, ChatGPT, Codex; Gemini; ...) without case; the family's makers' names (Anthropic, OpenAI,
// Google, Meta, ...) only as written, so ordinary words stay. A name matches as a whole word (no letter or digit on either side; a brand may
// be followed by digits or a plural s: GPT4, GPTs), with '-', '_', '.' and spaces between its parts interchangeable or missing (gpt-5.4-mini,
// "GPT 5.4 mini", GPT-5.4mini). Longer names are replaced first. The rewrite is a single regex pass per kind with a time limit; if it ever
// stops at the limit the judgement is not sent (RubricJudge reports an error), so a name is never sent by accident.
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Evals.Checks;

/// <summary>Hides one target's names from its judge.</summary>
public sealed class JudgeRedaction
{
    /// <summary>What a model name becomes.</summary>
    public const string ModelMark = "[model]";

    /// <summary>What a provider or maker name becomes.</summary>
    public const string ProviderMark = "[provider]";

    /// <summary>How long one rewrite may run before the judgement is given up.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai", "api", "auto", "base", "beta", "chat", "cli", "cloud", "cn", "code", "coding", "default", "fast", "free", "gateway", "latest", "max",
        "mini", "model", "models", "plus", "preview", "pro", "responses", "router", "workers", "provider",
    };

    private readonly Regex? models;
    private readonly Regex? providers;

    private JudgeRedaction(IReadOnlyList<string> modelTerms, IReadOnlyList<string> providerTerms, Regex? models, Regex? providers)
    {
        ModelTerms = modelTerms;
        ProviderTerms = providerTerms;
        this.models = models;
        this.providers = providers;
    }

    /// <summary>The model names hidden (ids, catalog names, brands), longest first.</summary>
    public IReadOnlyList<string> ModelTerms { get; }

    /// <summary>The provider and maker names hidden, longest first.</summary>
    public IReadOnlyList<string> ProviderTerms { get; }

    /// <summary>The rewrite that hides <paramref name="target"/>'s names, and <paramref name="responseModel"/> (the model the provider said
    /// answered), with display names from <paramref name="catalog"/> when it knows them.</summary>
    public static JudgeRedaction For(ModelIdentity target, string? responseModel = null, IModelCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var provider = target.Provider.Trim();
        var model = target.Model.Trim();
        var modelTerms = new List<(string Term, bool Brand, bool Exact)>();
        var providerTerms = new List<(string Term, bool Brand, bool Exact)>();
        void Add(List<(string, bool, bool)> list, string? term, bool brand = false, bool exact = false)
        {
            var text = term?.Trim().Trim('-', '_', '.', ' ', '/');
            if (string.IsNullOrEmpty(text) || text.Length < 2 || Generic.Contains(text)) return;
            list.Add((text, brand, exact));
        }

        Add(providerTerms, provider);
        foreach (var part in provider.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)) Add(providerTerms, part);
        Add(providerTerms, catalog?.FindProvider(provider)?.DisplayName);
        foreach (var id in new[] { model, responseModel })
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            Add(modelTerms, id);
            var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                foreach (var part in parts[..^1]) Add(providerTerms, part);
                Add(modelTerms, parts[^1]);
            }
        }
        Add(modelTerms, catalog?.FindModel(provider, model)?.Name);
        foreach (var family in new[] { ModelFamilies.Of(provider, model), ModelFamilies.Of(provider, responseModel) }.OfType<ModelFamily>().Distinct())
        {
            foreach (var brand in family.Brands) Add(modelTerms, brand, brand: true);
            foreach (var maker in family.Makers) Add(providerTerms, maker, exact: true);
        }
        var modelList = Distinct(modelTerms);
        var providerList = Distinct(providerTerms);
        return new JudgeRedaction([.. modelList.Select(term => term.Term)], [.. providerList.Select(term => term.Term)], Build(modelList), Build(providerList));
    }

    /// <summary><paramref name="text"/> with the target's names replaced (model names first, then provider names).</summary>
    /// <exception cref="RegexMatchTimeoutException">The rewrite ran past <see cref="Timeout"/>; send nothing.</exception>
    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var hidden = models is null ? text : models.Replace(text, ModelMark);
        return providers is null ? hidden : providers.Replace(hidden, ProviderMark);
    }

    /// <summary>Whether <paramref name="text"/> still names the target (a hidden name matches in it).</summary>
    public bool Names(string? text) =>
        !string.IsNullOrEmpty(text) && ((models?.IsMatch(text) ?? false) || (providers?.IsMatch(text) ?? false));

    private static List<(string Term, bool Brand, bool Exact)> Distinct(List<(string Term, bool Brand, bool Exact)> terms) =>
        [.. terms.GroupBy(term => (term.Exact ? term.Term : term.Term.ToLowerInvariant(), term.Brand, term.Exact)).Select(group => group.First())
            .OrderByDescending(term => term.Term.Length).ThenBy(term => term.Term, StringComparer.Ordinal)];

    private static Regex? Build(List<(string Term, bool Brand, bool Exact)> terms)
    {
        if (terms.Count == 0) return null;
        var pattern = string.Join("|", terms.Select(term => Pattern(term.Term, term.Brand, term.Exact)));
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    }

    private static string Pattern(string term, bool brand, bool exact)
    {
        var text = new StringBuilder(@"(?<![\p{L}\p{N}])");
        if (exact) text.Append("(?-i:");
        var separated = false;
        foreach (var character in term)
        {
            if (character is '-' or '_' or '.' or ' ' || char.IsWhiteSpace(character))
            {
                if (!separated) text.Append(@"[\s_.-]*");
                separated = true;
                continue;
            }
            separated = false;
            text.Append(Regex.Escape(character.ToString()));
        }
        if (exact) text.Append(')');
        text.Append(brand ? @"(?:'?s)?(?![\p{L}])" : @"(?![\p{L}\p{N}])");
        return "(?:" + text + ")";
    }
}
