using System.Collections.Immutable;
using System.Text;
using PiSharp.CodingAgent.Resources;

internal static class PromptTemplateDiscoveryTests
{
    public const string Prefix = "prompt template discovery ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "explicit files use ordinal md suffix and retain selection order", Files),
        (Prefix + "explicit directory is shallow and includes hidden md children", DirectoryChildren),
        (Prefix + "file links follow stat while broken and directory links are skipped", Links),
        (Prefix + "warnings precede collisions and requested missing-path errors", Diagnostics),
        (Prefix + "missing-path errors suppress repeated selections and diagnosed paths", MissingDuplicates),
        (Prefix + "directory enumeration failures are silent and root stat failures warn", Enumeration),
        (Prefix + "read and metadata decoding interleave in supplied order", Interleaving),
        (Prefix + "duplicate selected paths are read and collide without canonicalization", DuplicatePaths),
        (Prefix + "relative selections are rejected before file operations", RelativePaths),
        (Prefix + "pre-cancellation avoids enumeration and file operations", PreCancellation),
        (Prefix + "pending original read is joined before cancellation returns", PendingCancellation),
        (Prefix + "original read and decoder cancellation exceptions propagate", OriginalCancellation),
        (Prefix + "system adapter preserves raw BOM and shallow file kinds", SystemAdapter)
    ];

    private static async Task Files()
    {
        var fs = new Filesystem(); var a = P("a.md"); var b = P("b.md"); var upper = P("upper.MD");
        fs.File(a, "a $1"); fs.File(b, "b"); fs.File(upper, "ignored");
        var result = await Load(fs, [Select(b), Select(upper), Select(a)]);
        Equal("b,a", string.Join(',', result.Resources.Select(x => x.Template.Name)));
        Equal(b + "|" + a, string.Join('|', fs.Reads)); Equal(a, result.Resources[1].SourceInfo.Path);
        Equal("caller", result.Resources[1].SourceInfo.Source); Equal(P("base"), result.Resources[1].SourceInfo.BaseDir);
    }

    private static async Task DirectoryChildren()
    {
        var fs = new Filesystem(); var dir = P("dir"); var hidden = P("dir", ".hidden.md");
        var visible = P("dir", "visible.md"); var nested = P("dir", "nested"); var upper = P("dir", "upper.MD");
        fs.File(hidden, "hidden"); fs.File(visible, "visible"); fs.File(upper, "ignored");
        fs.Directory(dir, Entry(hidden), new("nested", nested, PromptTemplateFileKind.Directory), Entry(upper), Entry(visible));
        var result = await Load(fs, [Select(dir)]);
        Equal(".hidden,visible", string.Join(',', result.Resources.Select(x => x.Template.Name)));
        Equal(2, fs.Reads.Count); Equal(false, fs.Stats.Contains(nested));
    }

    private static async Task Links()
    {
        var fs = new Filesystem(); var dir = P("links"); var file = P("links", "file.md");
        var broken = P("links", "broken.md"); var folder = P("links", "folder.md");
        fs.File(file, "linked"); fs.Kinds[folder] = PromptTemplateFileKind.Directory;
        fs.StatErrors[broken] = new IOException("broken link");
        fs.Directory(dir, Entry(file, PromptTemplateFileKind.SymbolicLink), Entry(broken, PromptTemplateFileKind.SymbolicLink),
            Entry(folder, PromptTemplateFileKind.SymbolicLink));
        var result = await Load(fs, [Select(dir)]);
        Equal("file", result.Resources.Single().Template.Name); Equal(0, result.Diagnostics.Length); Equal(1, fs.Reads.Count);
    }

    private static async Task Diagnostics()
    {
        var fs = new Filesystem(); var first = P("first", "same.md"); var second = P("second", "same.md");
        var unreadable = P("unreadable.md"); var malformed = P("malformed.md"); var missing = P("missing.md");
        fs.File(first, "winner"); fs.File(second, "loser"); fs.File(unreadable, "unused");
        fs.ReadErrors[unreadable] = new IOException("read failed"); fs.File(malformed, "---\nbad\n---\nbody");
        var result = await PromptTemplateDiscovery.LoadAsync([Select(missing, true), Select(first), Select(second), Select(unreadable), Select(malformed)],
            _ => throw new FormatException("decode failed"), fs);
        Equal(1, result.Resources.Length); Equal("winner", result.Resources[0].Template.Content);
        Equal("Warning,Warning,Collision,Error", string.Join(",", result.Diagnostics.Select(x => x.Type)));
        Equal("read failed", result.Diagnostics[0].Message); Equal("decode failed", result.Diagnostics[1].Message);
        Equal(first, result.Diagnostics[2].Collision!.WinnerPath); Equal(second, result.Diagnostics[2].Collision!.LoserPath);
        Equal("Prompt template path does not exist", result.Diagnostics[3].Message); Equal(missing, result.Diagnostics[3].Path);
    }

    private static async Task Enumeration()
    {
        var fs = new Filesystem(); var dir = P("denied-directory"); var root = P("denied.md"); var absent = P("absent.md");
        fs.Directory(dir); fs.DirectoryErrors[dir] = new IOException("directory denied"); fs.StatErrors[root] = new IOException("root denied");
        var result = await Load(fs, [Select(dir), Select(root), Select(absent)]);
        Equal(0, result.Resources.Length); Equal(1, result.Diagnostics.Length); Equal("root denied", result.Diagnostics[0].Message);
    }

    private static async Task MissingDuplicates()
    {
        var fs = new Filesystem(); var path = P("missing.md");
        var repeated = await Load(fs, [Select(path, true), Select(path, true)]);
        Equal(1, repeated.Diagnostics.Length); Equal(PromptTemplateResourceDiagnosticType.Error, repeated.Diagnostics[0].Type);
        var calls = 0;
        fs.OnStat = selected => { if (++calls == 2) fs.StatErrors[selected] = new IOException("later stat failure"); };
        var diagnosed = await Load(fs, [Select(path, true), Select(path, true)]);
        Equal(1, diagnosed.Diagnostics.Length); Equal(PromptTemplateResourceDiagnosticType.Warning, diagnosed.Diagnostics[0].Type);
        Equal("later stat failure", diagnosed.Diagnostics[0].Message);
    }

    private static async Task Interleaving()
    {
        var fs = new Filesystem(); var a = P("a.md"); var b = P("b.md"); var events = new List<string>();
        fs.File(a, "---\na\n---\nA"); fs.File(b, "---\nb\n---\nB");
        fs.OnRead = path => events.Add("read " + Path.GetFileNameWithoutExtension(path));
        await PromptTemplateDiscovery.LoadAsync([Select(a), Select(b)], yaml => { events.Add("decode " + yaml); return new(yaml); }, fs);
        Equal("read a|decode a|read b|decode b", string.Join('|', events));
    }

    private static async Task DuplicatePaths()
    {
        var fs = new Filesystem(); var path = P("same.md"); fs.File(path, "body");
        var result = await Load(fs, [Select(path), Select(path)]);
        Equal(2, fs.Reads.Count); Equal(1, result.Resources.Length); Equal(1, result.Diagnostics.Length);
        Equal(PromptTemplateResourceDiagnosticType.Collision, result.Diagnostics[0].Type);
    }

    private static async Task RelativePaths()
    {
        var fs = new Filesystem();
        try { await Load(fs, [Select("relative.md")]); }
        catch (ArgumentException) { Equal(0, fs.Stats.Count); return; }
        throw new InvalidOperationException("Relative selection accepted.");
    }

    private static async Task PreCancellation()
    {
        var fs = new Filesystem(); var enumerated = false; using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        IEnumerable<PromptTemplatePathSelection> Paths() { enumerated = true; yield return Select(P("never.md")); }
        try { await PromptTemplateDiscovery.LoadAsync(Paths(), DecodeNever, fs, cancellation.Token); }
        catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token)
        { Equal(false, enumerated); Equal(0, fs.Stats.Count); return; }
        throw new InvalidOperationException("Pre-cancellation was lost.");
    }

    private static async Task PendingCancellation()
    {
        var fs = new Filesystem(); var path = P("pending.md"); fs.File(path, "body");
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fs.PendingRead = async (_, token) => { Equal(cancellation.Token, token); entered.SetResult(); await release.Task; return Encoding.UTF8.GetBytes("body"); };
        var pending = PromptTemplateDiscovery.LoadAsync([Select(path)], DecodeNever, fs, cancellation.Token);
        try
        {
            Equal(entered.Task, await Task.WhenAny(entered.Task, pending)); await entered.Task; cancellation.Cancel();
            Equal(false, pending.IsCompleted); release.SetResult();
            try { await pending; }
            catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { return; }
            throw new InvalidOperationException("Canceled read produced a catalog.");
        }
        finally { release.TrySetResult(); try { await pending; } catch (OperationCanceledException) { } }
    }

    private static async Task SystemAdapter()
    {
        // This fixture owns only its newly created unique temporary directory.
        var root = Path.Combine(Path.GetTempPath(), "pisharp-prompt-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "bom.md"); var bytes = Encoding.UTF8.GetBytes("\uFEFF\uFEFFbody\r\n");
            await File.WriteAllBytesAsync(path, bytes); Directory.CreateDirectory(Path.Combine(root, "nested"));
            var fs = new SystemPromptTemplateFileSystem(); Equal(PromptTemplateFileKind.File, fs.Stat(path));
            Equal(PromptTemplateFileKind.Directory, fs.Stat(root)); Equal(PromptTemplateFileKind.Missing, fs.Stat(Path.Combine(root, "missing")));
            var actualBytes = await fs.ReadFileAsync(path, default);
            Equal(true, bytes.SequenceEqual(actualBytes));
            var entries = fs.ReadDirectory(root); Equal(2, entries.Length);
            Equal(PromptTemplateFileKind.Directory, entries.Single(x => x.Name == "nested").Kind);
            var catalog = await Load(fs, [Select(root)]); Equal("\uFEFFbody\n", catalog.Resources.Single().Template.Content);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task OriginalCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var original = new OperationCanceledException("original boundary cancellation", cancellation.Token);
        foreach (var decoder in new[] { false, true })
        {
            var fs = new Filesystem(); var path = P("cancel.md"); fs.File(path, "---\nmetadata\n---\nbody");
            if (!decoder) fs.ReadErrors[path] = original;
            try { await PromptTemplateDiscovery.LoadAsync([Select(path)], _ => throw original, fs); }
            catch (OperationCanceledException error) when (ReferenceEquals(error, original)) { continue; }
            throw new InvalidOperationException("Original cancellation was converted or lost.");
        }
    }

    internal static string P(params string[] parts) => Path.GetFullPath(Path.Combine(new[] { Path.GetTempPath(), "pisharp-prompt-discovery-fixture" }.Concat(parts).ToArray()));
    internal static PromptTemplatePathSelection Select(string path, bool missing = false) => new(path,
        new(path, "caller", PromptTemplateSourceScope.Temporary, PromptTemplateSourceOrigin.TopLevel, P("base")), missing);
    private static PromptTemplateDirectoryEntry Entry(string path, PromptTemplateFileKind kind = PromptTemplateFileKind.File) => new(Path.GetFileName(path), path, kind);
    private static PromptTemplateMetadata DecodeNever(string _) => throw new InvalidOperationException("Unexpected decoder.");
    private static Task<PromptTemplateCatalogSnapshot> Load(IPromptTemplateFileSystem fs, IEnumerable<PromptTemplatePathSelection> paths) =>
        PromptTemplateDiscovery.LoadAsync(paths, DecodeNever, fs);
    internal static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}."); }

    internal sealed class Filesystem : IPromptTemplateFileSystem
    {
        public Dictionary<string, PromptTemplateFileKind> Kinds { get; } = new();
        public Dictionary<string, byte[]> Texts { get; } = new();
        public Dictionary<string, ImmutableArray<PromptTemplateDirectoryEntry>> Directories { get; } = new();
        public Dictionary<string, Exception> StatErrors { get; } = new();
        public Dictionary<string, Exception> ReadErrors { get; } = new();
        public Dictionary<string, Exception> DirectoryErrors { get; } = new();
        public List<string> Reads { get; } = new(); public List<string> Stats { get; } = new();
        public Action<string>? OnRead { get; set; }
        public Action<string>? OnStat { get; set; }
        public Func<string, CancellationToken, Task<byte[]>>? PendingRead { get; set; }
        public void File(string path, string text) { Kinds[path] = PromptTemplateFileKind.File; Texts[path] = Encoding.UTF8.GetBytes(text); }
        public void Directory(string path, params PromptTemplateDirectoryEntry[] entries) { Kinds[path] = PromptTemplateFileKind.Directory; Directories[path] = [.. entries]; }
        public PromptTemplateFileKind Stat(string path) { Stats.Add(path); OnStat?.Invoke(path); if (StatErrors.TryGetValue(path, out var error)) throw error; return Kinds.GetValueOrDefault(path, PromptTemplateFileKind.Missing); }
        public ImmutableArray<PromptTemplateDirectoryEntry> ReadDirectory(string path) { if (DirectoryErrors.TryGetValue(path, out var error)) throw error; return Directories[path]; }
        public ValueTask<byte[]> ReadFileAsync(string path, CancellationToken token)
        { Reads.Add(path); OnRead?.Invoke(path); if (ReadErrors.TryGetValue(path, out var error)) throw error; return PendingRead is null ? ValueTask.FromResult(Texts[path]) : new(PendingRead(path, token)); }
    }
}
