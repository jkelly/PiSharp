using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Prompts;
using PiSharp.CodingAgent.Resources;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;
using static PromptTemplateDiscoveryTests;

internal static class PromptTemplateCliAdapterTests
{
    public const string Prefix = "prompt template CLI adapter ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "consume repeatable selections only at the parser option boundary", Extract),
        (Prefix + "reject missing and relative flag values", Invalid),
        (Prefix + "plain policy reports nonempty YAML without interpreting it", Policy),
        (Prefix + "binding loads and expands plain template without extensions", Usable),
        (Prefix + "wire catalog preserves extension rows before prompt rows and omits hint", Catalog),
        (Prefix + "template rows do not acquire extension completion authority", Completion)
    ];

    private static Task Extract()
    {
        var first = P("first.md"); var second = P("second.md");
        var selections = ImmutableArray.CreateBuilder<PromptTemplatePathSelection>();
        string[] args = ["--prompt-template", first, "--workspace", "--prompt-template", "--prompt-template", second];
        var index = 0; Equal(true, PromptTemplateCliConfiguration.TryConsume(args, ref index, selections)); Equal(1, index);
        index++; Equal(false, PromptTemplateCliConfiguration.TryConsume(args, ref index, selections)); Equal(2, index);
        // The existing workspace parser consumes its opaque value at index 3, even when it looks like a flag.
        index = 4; Equal(true, PromptTemplateCliConfiguration.TryConsume(args, ref index, selections)); Equal(5, index);
        Equal(first, selections[0].Path); Equal(second, selections[1].Path);
        Equal(true, selections.All(x => x.ReportMissingPath)); return Task.CompletedTask;
    }

    private static Task Invalid()
    {
        foreach (var args in new[] { new[] { "--prompt-template" }, new[] { "--prompt-template", "relative.md" } })
        {
            try { Configure(args); }
            catch (SessionCommandException) { continue; }
            throw new InvalidOperationException("Invalid prompt path accepted.");
        }
        return Task.CompletedTask;
    }

    private static async Task Policy()
    {
        var fs = new Filesystem(); var plain = P("plain.md"); var yaml = P("yaml.md"); var empty = P("empty.md");
        fs.File(plain, "plain $1"); fs.File(yaml, "---\ndescription: |\n  valid YAML\n---\nbody"); fs.File(empty, "---\n---\nempty");
        var config = Configure(["--prompt-template", plain, "--prompt-template", yaml, "--prompt-template", empty]);
        var binding = await PromptTemplateCliBinding.LoadAsync(config, fileSystem: fs);
        Equal("plain,empty", string.Join(',', binding.Templates.Commands.Select(x => x.Name)));
        Equal(PromptTemplateResourceDiagnosticType.Warning, binding.Templates.Catalog.Diagnostics.Single().Type);
        Equal(true, binding.Templates.Catalog.Diagnostics.Single().Message.StartsWith("YAML prompt frontmatter is unavailable", StringComparison.Ordinal));
    }

    private static async Task Usable()
    {
        var fs = new Filesystem(); var path = P("review.md"); fs.File(path, "Review $1");
        var config = Configure(["--prompt-template", path]);
        var binding = await PromptTemplateCliBinding.LoadAsync(config, fileSystem: fs);
        foreach (var operation in new[] { "prompt", "steer", "follow_up" })
            Equal("Review change", (await binding.AdmissionForRpc(operation).ReduceAsync(new("/review change", PromptInputSource.Rpc), default)).Text);
        Equal(PromptInputAction.Continue, (await binding.AdmissionFor(PromptTemplateInputOperation.ExtensionMessage)
            .ReduceAsync(new("/review change", PromptInputSource.Extension), default)).Action);
    }

    private static async Task Catalog()
    {
        var fs = new Filesystem(); var path = P("name with spaces.md"); fs.File(path, "---\nmeta\n---\nbody");
        var set = await PromptTemplateResourceSet.LoadAsync([Select(path)], _ => new("description", "[hint]"), fs);
        var extensions = new Extensions(); var wire = new PromptTemplateRpcCommandCatalog(set, extensions).CommandCatalog.Value;
        Equal(2, wire.GetArrayLength()); Equal("extension", wire[0].GetProperty("source").GetString());
        Equal("prompt", wire[1].GetProperty("source").GetString()); Equal("name with spaces", wire[1].GetProperty("name").GetString());
        Equal(false, wire[1].TryGetProperty("argumentHint", out _));
        var source = wire[1].GetProperty("sourceInfo"); Equal("temporary", source.GetProperty("scope").GetString());
        Equal("top-level", source.GetProperty("origin").GetString()); Equal(path, source.GetProperty("path").GetString());
        Equal("[hint]", set.Commands.Single().ArgumentHint);
    }

    private static async Task Completion()
    {
        var fs = new Filesystem(); var path = P("review.md"); fs.File(path, "body");
        var set = await PromptTemplateResourceSet.LoadAsync([Select(path)], _ => throw new Exception("Unexpected decoder"), fs);
        var extensions = new Extensions(); var catalog = new PromptTemplateRpcCommandCatalog(set, extensions);
        using var stop = new CancellationTokenSource();
        await catalog.CompleteCommandAsync("ext", "prefix", stop.Token); Equal(stop.Token, extensions.LastToken);
        try { await new PromptTemplateRpcCommandCatalog(set).CompleteCommandAsync("review", "prefix", default); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Template acquired extension completion authority.");
    }

    private static PromptTemplateCliConfiguration Configure(string[] args)
    {
        var selections = ImmutableArray.CreateBuilder<PromptTemplatePathSelection>();
        for (var index = 0; index < args.Length; index++)
            if (!PromptTemplateCliConfiguration.TryConsume(args, ref index, selections)) throw new InvalidOperationException("Unexpected fixture option.");
        return new(selections.ToImmutable());
    }

    private sealed class Extensions : IRpcExtensionCommandCatalog
    {
        public JsonData CommandCatalog => JsonData.Parse("[{\"name\":\"ext\",\"source\":\"extension\",\"description\":\"extension\"}]");
        public CancellationToken LastToken { get; private set; }
        public ValueTask<JsonData> CompleteCommandAsync(string name, string prefix, CancellationToken token)
        { Equal("ext", name); Equal("prefix", prefix); LastToken = token; return ValueTask.FromResult(JsonData.Parse("[]")); }
    }
}
