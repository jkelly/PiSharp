using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent.Resources;
using PiSharp.CodingAgent.Resources.Skills;

internal static class SkillDiscoveryTests
{
    public const string Prefix = "skill discovery ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "explicit root defaults precedence scope and missing defaults", Roots),
        (Prefix + "denied roots and direct files never read adjacent ignore files", Authority),
        (Prefix + "ordered ignore files root stop and parent pruning", Traversal),
        (Prefix + "nested source prefix and escaped anchor quirks", Nested),
        (Prefix + "wildcards ranges POSIX whitespace case and parent negation", Patterns),
        (Prefix + "ignore reads share file and byte budgets", Budgets),
        (Prefix + "canceled held ignore read joins original before enumeration", Cancellation),
        (Prefix + "reload captures body provenance without extra reads", Reload)
    ];
    private static string P(params string[] parts)
    { string[] paths = [Path.GetTempPath(), "pisharp-skill-discovery-fixture", .. parts]; return Path.Combine(paths); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; observed {actual}."); }
    private static string Doc(string name, string body = "body") => "---\n" + JsonSerializer.Serialize(new { name, description = "fixture" }) + "\n---\n" + body;
    private static SkillMetadata Decode(string yaml)
    { using var json = JsonDocument.Parse(yaml); return new(json.RootElement.GetProperty("name").GetString(), json.RootElement.GetProperty("description").GetString()); }
    private sealed class Files : ISkillResourceFileSystem
    {
        internal readonly Dictionary<string, byte[]> Text = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, ImmutableArray<PromptTemplateDirectoryEntry>> Dirs = new(StringComparer.Ordinal);
        internal readonly List<string> Stats = [], Lists = [], Reads = [];
        internal Func<string, int, CancellationToken, ValueTask<byte[]>>? Pending;
        internal void Put(string path, string text) => Text[path] = Encoding.UTF8.GetBytes(text);
        internal void Dir(string path, params (string Name, PromptTemplateFileKind Kind)[] entries) =>
            Dirs[path] = entries.Select(entry => new PromptTemplateDirectoryEntry(entry.Name, Path.Combine(path, entry.Name), entry.Kind)).ToImmutableArray();
        public PromptTemplateFileKind Stat(string path)
        { Stats.Add(path); return Dirs.ContainsKey(path) ? PromptTemplateFileKind.Directory : Text.ContainsKey(path) ? PromptTemplateFileKind.File : PromptTemplateFileKind.Missing; }
        public ImmutableArray<PromptTemplateDirectoryEntry> ReadDirectory(string path, int maximumEntries)
        { Lists.Add(path); var entries = Dirs[path]; if (entries.Length > maximumEntries) throw new IOException("Bound"); return entries; }
        public ValueTask<byte[]> ReadFileAsync(string path, int maximumBytes, CancellationToken token)
        {
            Reads.Add(path); if (Pending is not null) return Pending(path, maximumBytes, token);
            token.ThrowIfCancellationRequested(); var bytes = Text[path]; if (bytes.Length > maximumBytes) throw new IOException("Bound");
            return ValueTask.FromResult(bytes.ToArray());
        }
    }
    private static async Task Roots()
    {
        var fs = new Files(); var user = P("agent"); var project = P("work");
        var userSkills = Path.Combine(user, "skills"); var projectSkills = Path.Combine(project, ".pi", "skills");
        fs.Dir(userSkills, ("a.md", PromptTemplateFileKind.File)); fs.Dir(projectSkills, ("b.md", PromptTemplateFileKind.File));
        fs.Put(Path.Combine(userSkills, "a.md"), Doc("same")); fs.Put(Path.Combine(projectSkills, "b.md"), Doc("same"));
        var explicitFile = P("explicit.md"); fs.Put(explicitFile, Doc("last"));
        var request = new SkillDiscoveryRequest(new(user, true), new(project, true), [new(explicitFile)]);
        var set = await SkillDiscovery.LoadAsync(request, Decode, fs);
        Equal("same,last", string.Join(',', set.Skills.Select(skill => skill.Name)));
        Equal(PromptTemplateSourceScope.User, set.Skills[0].SourceInfo.Scope);
        Equal(PromptTemplateSourceScope.Temporary, set.Skills[1].SourceInfo.Scope);
        Equal(Path.Combine(userSkills, "a.md"), set.Diagnostics.Single().WinnerPath);
        var selections = SkillDiscovery.BuildSelections(request with { IncludeDefaults = false, ExplicitPaths = [new(Path.Combine(projectSkills, "b.md"))] });
        Equal(1, selections.Length); Equal(PromptTemplateSourceScope.Project, selections[0].Scope);
        set = await SkillDiscovery.LoadAsync(new(new(P("missing-user"), true), new(P("missing-work"), true), []), Decode, fs);
        Equal(0, set.Diagnostics.Length); Equal(0, set.Skills.Length);
    }
    private static async Task Authority()
    {
        var fs = new Files(); var denied = P("denied"); var file = P("direct", "SKILL.md"); fs.Put(file, Doc("direct"));
        fs.Put(P("direct", ".gitignore"), "SKILL.md");
        var set = await SkillDiscovery.LoadAsync(new(null, new(denied, false), [new(file)]), Decode, fs);
        Equal("direct", set.Skills.Single().Name); Equal(1, fs.Stats.Count); Equal(file, fs.Stats[0]);
        Equal(file, fs.Reads.Single()); Equal(0, fs.Lists.Count); Equal("DeniedSelection", set.Diagnostics.Single().Code);
    }
    private static async Task Traversal()
    {
        var fs = new Files(); var root = P("ordered");
        fs.Dir(root, ("SKILL.md", PromptTemplateFileKind.File), ("keep.md", PromptTemplateFileKind.File), ("drop.md", PromptTemplateFileKind.File), ("blocked", PromptTemplateFileKind.Directory));
        fs.Put(Path.Combine(root, ".gitignore"), "SKILL.md\n*.md\nblocked/\n");
        fs.Put(Path.Combine(root, ".ignore"), "!keep.md\n!drop.md\n!blocked/child/SKILL.md\n");
        fs.Put(Path.Combine(root, ".fdignore"), "drop.md\n");
        fs.Put(Path.Combine(root, "SKILL.md"), Doc("root")); fs.Put(Path.Combine(root, "keep.md"), Doc("keep"));
        fs.Put(Path.Combine(root, "drop.md"), Doc("drop"));
        fs.Dir(Path.Combine(root, "blocked"), ("SKILL.md", PromptTemplateFileKind.File));
        var set = await SkillResourceSet.LoadAsync([new(root)], Decode, fs);
        Equal("keep", set.Skills.Single().Name); Equal(1, fs.Lists.Count); Equal(4, fs.Reads.Count);
        Equal(false, fs.Stats.Any(path => path.StartsWith(Path.Combine(root, "blocked") + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        Equal(".gitignore,.ignore,.fdignore,keep.md", string.Join(',', fs.Reads.Select(Path.GetFileName)));
        fs.Put(Path.Combine(root, ".gitignore"), ""); fs.Put(Path.Combine(root, ".ignore"), ""); fs.Put(Path.Combine(root, ".fdignore"), "");
        set = await SkillResourceSet.LoadAsync([new(root)], Decode, fs);
        Equal("root", set.Skills.Single().Name); // An admitted SKILL.md stops root traversal.
    }
    private static async Task Nested()
    {
        var fs = new Files(); var root = P("nested"); var child = Path.Combine(root, "child"); var allowed = Path.Combine(child, "allowed");
        fs.Dir(root, ("child", PromptTemplateFileKind.Directory));
        fs.Dir(child, ("SKILL.md", PromptTemplateFileKind.File), ("blocked", PromptTemplateFileKind.Directory), ("allowed", PromptTemplateFileKind.Directory));
        fs.Put(Path.Combine(child, ".gitignore"), "SKILL.md\nblocked\n");
        fs.Dir(allowed, ("SKILL.md", PromptTemplateFileKind.File)); fs.Put(Path.Combine(allowed, "SKILL.md"), Doc("allowed"));
        var set = await SkillResourceSet.LoadAsync([new(root)], Decode, fs); Equal("allowed", set.Skills.Single().Name); Equal(3, fs.Lists.Count);
        var rules = new SkillIgnoreRules(); rules.AddFile("/foo\n\\!bang\n\\#hash\n");
        Equal(true, rules.IsIgnored("deeper/foo")); Equal(false, rules.IsIgnored("bang")); Equal(true, rules.IsIgnored("#hash"));
        rules = new(); rules.AddFile("foo\n\\!bang\n", "child");
        Equal(true, rules.IsIgnored("child/foo")); Equal(false, rules.IsIgnored("child/deeper/foo")); Equal(true, rules.IsIgnored("child/!bang"));
    }
    private static Task Patterns()
    {
        var rules = new SkillIgnoreRules(); rules.AddFile("*.MD\n!keep.md\ncache/\n!cache/a.md\nlogs/**/a?.txt\nassets/**\n[[:digit:]][a-c].tmp\n[!a-z].bin\nspace   \nescaped\\ \n");
        foreach (var path in new[] { "nested/X.md", "cache/a.md", "logs/a1.txt", "logs/deep/a2.txt", "assets/deep/file", "2b.tmp", "7.bin", "space", "escaped " }) Equal(true, rules.IsIgnored(path));
        foreach (var path in new[] { "keep.md", "logs/abc.txt", "d.tmp", "a.bin", "assets", "space " }) Equal(false, rules.IsIgnored(path));
        Equal(false, rules.IsIgnored("cache")); Equal(true, rules.IsIgnored("cache", true));
        rules = new(); rules.AddFile("drop\n!drop\n"); Equal(false, rules.IsIgnored("drop"));
        rules = new(); rules.AddFile("[z-a]\n"); Equal(false, rules.IsIgnored("a"));
        bool bounded = false; try { new SkillIgnoreRules(1).AddFile("one\ntwo\n"); } catch (ArgumentException) { bounded = true; }
        Equal(true, bounded); return Task.CompletedTask;
    }
    private static async Task Budgets()
    {
        var fs = new Files(); var root = P("limits"); fs.Dir(root, ("one.md", PromptTemplateFileKind.File));
        fs.Put(Path.Combine(root, ".gitignore"), "unrelated\n"); fs.Put(Path.Combine(root, "one.md"), Doc("one"));
        var set = await SkillResourceSet.LoadAsync([new(root)], Decode, fs, new(MaximumFiles: 1));
        Equal(0, set.Skills.Length); Equal(1, fs.Reads.Count); Equal("ReadLimit", set.Diagnostics.Single().Code);
        fs.Reads.Clear(); fs.Put(Path.Combine(root, ".gitignore"), new string('x', 100));
        set = await SkillResourceSet.LoadAsync([new(root)], Decode, fs, new(MaximumFileBytes: 32, MaximumTotalBytes: 32));
        Equal(1, fs.Reads.Count); Equal("ReadFailed,ReadLimit", string.Join(',', set.Diagnostics.Select(row => row.Code)));
    }
    private static async Task Cancellation()
    {
        var fs = new Files(); var root = P("held"); fs.Dir(root, ("one.md", PromptTemplateFileKind.File));
        var ignorePath = Path.Combine(root, ".gitignore"); fs.Put(ignorePath, "nothing");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)); bool joined = false;
        fs.Pending = async (path, _, token) => { Equal(ignorePath, path); Equal(stop.Token, token); entered.TrySetResult();
            try { await release.Task; return fs.Text[path]; } finally { joined = true; } };
        var original = SkillResourceSet.LoadAsync([new(root)], Decode, fs, cancellationToken: stop.Token);
        try
        {
            await entered.Task.WaitAsync(stop.Token); stop.Cancel(); Equal(false, original.IsCompleted); Equal(0, fs.Lists.Count);
        }
        finally
        {
            stop.Cancel(); release.TrySetResult();
            try { await original; throw new InvalidOperationException("Cancellation expected"); }
            catch (OperationCanceledException error) { Equal(stop.Token, error.CancellationToken); }
        }
        Equal(true, joined); Equal(0, fs.Lists.Count); Equal(1, fs.Reads.Count);
    }
    private static async Task Reload()
    {
        var fs = new Files(); var path = P("reload", "SKILL.md"); fs.Put(path, Doc("reload", "first"));
        var first = await SkillResourceSet.LoadAsync([new(path)], Decode, fs); Equal(1, fs.Reads.Count);
        fs.Put(path, Doc("reload", "second"));
        var second = await SkillResourceSet.LoadAsync([new(path)], Decode, fs); Equal(2, fs.Reads.Count);
        Equal(first.ReloadDescriptors[0].Key, second.ReloadDescriptors[0].Key);
        Equal(64, first.ReloadDescriptors[0].Key.Length); Equal(false, first.ReloadDescriptors[0].Fingerprint == second.ReloadDescriptors[0].Fingerprint);
        var third = await SkillResourceSet.LoadAsync([new(path) { Scope = PromptTemplateSourceScope.User }], Decode, fs);
        Equal(false, second.ReloadDescriptors[0].Fingerprint == third.ReloadDescriptors[0].Fingerprint);
        Equal(PromptTemplateSourceScope.User, third.ReloadDescriptors[0].Skill.SourceInfo.Scope);
        var binding = await SkillCliBinding.LoadDiscoveredAsync(new(null, null, [new(path)]), fs, Decode);
        Equal(1, binding.Commands().CommandCatalog.Value.GetArrayLength());
        Equal("temporary", binding.Commands().CommandCatalog.Value[0].GetProperty("sourceInfo").GetProperty("scope").GetString());
    }
}
