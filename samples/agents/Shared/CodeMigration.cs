// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Samples;

/// <summary>A bounded, real model-driven source migration used after a dependency upgrade.</summary>
public static class CodeMigration
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".csproj", ".props", ".targets", ".razor", ".resx", ".xaml", ".fs", ".fsproj", ".vb", ".vbproj" };

    public static async Task<JsonObject> RunAsync(string workspace, string request, CancellationToken token = default)
    {
        var context = new SampleContext(workspace, new JsonObject { ["request"] = request }, approved: true, live: true)
        { OutputDirectory = Path.Combine(Path.GetTempPath(), "pisharp-migration-reports", Guid.NewGuid().ToString("N")) };
        var edited = new JsonArray();
        string Allowed(string path)
        {
            var full = context.ResolvePath(path);
            var parts = path.Replace('\\', '/').Split('/');
            if (parts.Any(p => p is "bin" or "obj" or ".git" or "node_modules" || p.StartsWith('.')) || !Extensions.Contains(Path.GetExtension(full)))
                throw new ArgumentException("Only ordinary source and project files may be migrated.");
            return full;
        }
        var list = new ToolSpec("list_source", "List source files available to migrate.", SampleJson.Schema(), (_, ct) =>
        {
            var files = new List<string>();
            void Visit(string directory)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (files.Count >= 1000) throw new InvalidOperationException("Source listing exceeds 1000 files; configure a narrower migration workspace.");
                    if (Extensions.Contains(Path.GetExtension(file)) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                        files.Add(Path.GetRelativePath(workspace, file).Replace('\\', '/'));
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (name is "bin" or "obj" or "node_modules" || name.StartsWith('.') || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    Visit(child);
                }
            }
            Visit(context.Workspace);
            return Task.FromResult<JsonNode?>(new JsonArray(files.Order(StringComparer.Ordinal).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));
        });
        var read = new ToolSpec("read_source", "Read one source file and its current SHA256.",
            SampleJson.Schema("{\"path\":{\"type\":\"string\"}}", "path"), async (a, ct) =>
            {
                var path = Allowed(a.Text("path"));
                if (new FileInfo(path).Length > 262144) throw new InvalidOperationException("Source file exceeds the migration limit.");
                var bytes = await File.ReadAllBytesAsync(path, ct);
                return new JsonObject { ["path"] = a.Text("path"), ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["content"] = Encoding.UTF8.GetString(bytes) };
            });
        var write = new ToolSpec("replace_source", "Replace a source file only if its reviewed SHA256 still matches. Preserve behavior and make only compatibility fixes required by the dependency upgrade.",
            SampleJson.Schema("{\"path\":{\"type\":\"string\"},\"sha256\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}}", "path", "sha256", "content"), async (a, ct) =>
            {
                var path = Allowed(a.Text("path"));
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != a.Text("sha256")) throw new InvalidOperationException("Source changed after it was read; read it again.");
                var content = a.Text("content");
                if (Encoding.UTF8.GetByteCount(content) > 262144) throw new InvalidOperationException("Replacement exceeds the migration limit.");
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
                edited.Add(a.Text("path"));
                return new JsonObject { ["written"] = a.Text("path") };
            }, RequiresApproval: true);
        var spec = new WorkflowSpec("32-migration", "Dependency source migration",
            "Make the minimum source compatibility edits required by this dependency upgrade. Inspect before editing. Do not change security checks, package versions, test expectations, or disable tests to make a build pass. You cannot run commands; the caller independently validates restore/build/tests afterward. Explain uncertainty.\n" + request,
            [list, read, write], []);
        var result = await SampleHost.ExecuteAsync(spec, context, token);
        result.Report["editedFiles"] = edited;
        return result.Report;
    }
}
