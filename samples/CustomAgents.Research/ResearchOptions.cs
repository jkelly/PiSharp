// Decision 0012: the research sample's command line, and the file names a topic maps to.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.CustomAgents.Tools;

namespace PiSharp.CustomAgents.Research;

/// <summary>The command line.</summary>
internal sealed partial record ResearchOptions(string Topic, string? Provider, string? Model, string? Thinking, int Parts, string Output,
    string Directory, bool Fresh, string? Web, string XModel, int? Days)
{
    public const string DefaultTopic = "How developers use AI coding agents in 2026";

    public const string Usage = """
        Usage: CustomAgents.Research ["<topic>"] [--provider <id> --model <id>] [--thinking <level>] [--parts <2-8>] [--out <file.md>]
                                     [--dir <path>] [--fresh] [--web <backend>|none] [--x-model <id>] [--days <n>]
          <topic>     what to research (default: "How developers use AI coding agents in 2026")
          --provider  the agents' provider, for example xai with --model grok-4.7; its API key comes from its environment variable.
                      Default: scripted models and canned search results, offline
          --parts     how many researcher subagents the lead splits the topic between (default 4)
          --out       the Markdown report (default <topic>.md in the current directory)
          --dir       where each topic's SQLite storage is kept (default ./research-runs). A run that stopped resumes from it; a
                      finished one returns its report again
          --fresh     discards the topic's storage and researches it again
          --web       the web_search backend (brave, tavily, firecrawl, openai, anthropic, xai or openrouter, with its key in its
                      variable), or none. Default: the first whose key is set
          --x-model   the Grok model that runs X search (default grok-4.7). X search needs XAI_API_KEY
          --days      X search: only posts from the last <n> days
        """;

    public static ResearchOptions? Parse(string[] args)
    {
        string? topic = null, provider = null, model = null, thinking = null, output = null, web = null;
        string xModel = XaiWebSearch.DefaultModel;
        int parts = 4;
        int? days = null;
        var fresh = false;
        var directory = Path.Combine(Environment.CurrentDirectory, "research-runs");
        for (var index = 0; index < args.Length; index++)
        {
            string? Value() => index + 1 < args.Length ? args[++index] : null;
            switch (args[index])
            {
                case "--provider": provider = Value(); if (provider is null) return null; break;
                case "--model": model = Value(); if (model is null) return null; break;
                case "--thinking": thinking = Value(); if (thinking is null) return null; break;
                case "--parts": if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out parts) || parts is < 2 or > 8) return null; break;
                case "--out": output = Value() is { } file ? Path.GetFullPath(file) : null; if (output is null) return null; break;
                case "--dir": directory = Value() is { } path ? Path.GetFullPath(path) : null!; if (directory is null) return null; break;
                case "--fresh": fresh = true; break;
                case "--web": web = Value(); if (web is null || (web != "none" && !WebSearchTool.Backends.Contains(web))) return null; break;
                case "--x-model": if (Value() is not { } id) return null; xModel = id; break;
                case "--days":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1) return null;
                    days = count;
                    break;
                default:
                    if (args[index].StartsWith("--", StringComparison.Ordinal) || topic is not null || string.IsNullOrWhiteSpace(args[index])) return null;
                    topic = args[index].Trim();
                    break;
            }
        }
        if ((provider is null) != (model is null)) return null;
        topic ??= DefaultTopic;
        output ??= Path.Combine(Environment.CurrentDirectory, Words(topic) + ".md");
        return new ResearchOptions(topic, provider, model, thinking, parts, output, directory, fresh, web, xModel, days);
    }

    /// <summary>The topic's storage: one SQLite file per topic, named by its words and a hash of the whole topic.</summary>
    public string StoragePath => Path.Combine(Directory, Words(Topic) + "-" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Topic)))[..8] + ".sqlite");

    /// <summary>The topic's words, lower case and joined by hyphens (at most 48 characters).</summary>
    private static string Words(string topic)
    {
        var words = string.Join('-', Word().Matches(topic.ToLowerInvariant()).Select(match => match.Value));
        if (words.Length > 48) words = words[..48].TrimEnd('-');
        return words.Length == 0 ? "research" : words;
    }

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();
}
