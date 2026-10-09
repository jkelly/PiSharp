using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PiSharp.Agent;

namespace PiSharp.CodingAgent.Resources.Skills;

public sealed record SkillMetadata(string? Name = null, string? Description = null, bool DisableModelInvocation = false);
public sealed record SkillResource(string Name, string Description, string FilePath, string BaseDir,
    PromptTemplateSourceInfo SourceInfo, bool DisableModelInvocation);
public sealed record SkillPathSelection(string Path, bool Trusted = true)
{
    public PromptTemplateSourceScope Scope { get; init; } = PromptTemplateSourceScope.Temporary;
    public bool ReportMissingPath { get; init; } = true;
}
public sealed record SkillDiagnostic(string Type, string Code, string Path, string? Name = null, string? WinnerPath = null);
public sealed record SkillResourceOptions(int MaximumFiles = 128, int MaximumEntries = 4096, int MaximumDepth = 16,
    int MaximumFileBytes = 65_536, int MaximumTotalBytes = 1_048_576);
public sealed record ParsedSkillBlock(string Name, string Location, string Content, string? UserMessage);

/// <summary>Explicit inert resource reads only. Entries and reads must enforce the supplied bounds;
/// links/ancestors must be rejected by the read owner. No adjacent resources, credentials or executable grants.</summary>
public interface ISkillResourceFileSystem
{
    PromptTemplateFileKind Stat(string path);
    ImmutableArray<PromptTemplateDirectoryEntry> ReadDirectory(string path, int maximumEntries);
    ValueTask<byte[]> ReadFileAsync(string path, int maximumBytes, CancellationToken cancellationToken);
}

/// <summary>Pi v0.99.1 metadata, first-name-wins capture and selected-file expansion.
/// A borrowed filesystem owns reads; the caller directly awaits original loads and expansions.</summary>
public sealed class SkillResourceSet
{
    public ImmutableArray<SkillResource> Skills { get; }
    public ImmutableArray<SkillDiagnostic> Diagnostics { get; }
    public ImmutableArray<SkillReloadDescriptor> ReloadDescriptors { get; }
    private readonly ISkillResourceFileSystem files;
    private readonly SkillResourceOptions options;
    private readonly Func<string, SkillMetadata> decode;
    private SkillResourceSet(ImmutableArray<SkillResource> skills, ImmutableArray<SkillDiagnostic> diagnostics,
        ISkillResourceFileSystem files, SkillResourceOptions options, Func<string, SkillMetadata> decode,
        ImmutableArray<SkillReloadDescriptor> reloadDescriptors)
    { Skills = skills; Diagnostics = diagnostics; this.files = files; this.options = options; this.decode = decode;
        ReloadDescriptors = reloadDescriptors; }

    public static async Task<SkillResourceSet> LoadAsync(IEnumerable<SkillPathSelection> selections,
        Func<string, SkillMetadata> decode, ISkillResourceFileSystem? files = null,
        SkillResourceOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections); ArgumentNullException.ThrowIfNull(decode);
        files ??= new SystemSkillResourceFileSystem(); options ??= new();
        // Hosts following Pi (skills.ts reads every skill file of any size) pass int.MaxValue counts; the per-file read stays a memory
        // bound of the host's choosing and the directory depth a recursion guard.
        if (options.MaximumFiles < 1 || options.MaximumEntries < 1 || options.MaximumDepth is < 0 or > 64 || options.MaximumFileBytes < 1 ||
            options.MaximumTotalBytes < options.MaximumFileBytes)
            throw new ArgumentException("Invalid skill resource limits.", nameof(options));
        var skills = ImmutableArray.CreateBuilder<SkillResource>(); var diagnostics = ImmutableArray.CreateBuilder<SkillDiagnostic>();
        var descriptors = ImmutableArray.CreateBuilder<SkillReloadDescriptor>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var names = new Dictionary<string, SkillResource>(StringComparer.Ordinal);
        int readCount = 0, entriesSeen = 0, selectedCount = 0; long readBytes = 0;
        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++selectedCount > options.MaximumFiles) throw new ArgumentException("Too many skill selections.");
            if (selection is null || !Path.IsPathFullyQualified(selection.Path) || selection.Path.Length > 4096)
                throw new ArgumentException("Skills require explicit absolute paths.");
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selection.Path));
            if (!selection.Trusted) { diagnostics.Add(new("warning", "DeniedSelection", path)); continue; }
            try
            {
                var kind = files.Stat(path);
                if (kind == PromptTemplateFileKind.Directory) await Visit(path, true, 0, path, new(), selection.Scope).ConfigureAwait(false);
                else if (kind == PromptTemplateFileKind.File && path.EndsWith(".md", StringComparison.Ordinal)) await Read(path, selection.Scope).ConfigureAwait(false);
                else if (kind != PromptTemplateFileKind.Missing || selection.ReportMissingPath)
                    diagnostics.Add(new("warning", kind == PromptTemplateFileKind.Missing ? "MissingPath" : "UnsupportedPath", path));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { diagnostics.Add(new("warning", "DiscoveryFailed", path)); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(skills.ToImmutable(), diagnostics.ToImmutable(), files, options, decode, descriptors.ToImmutable());

        async Task Visit(string directory, bool root, int depth, string discoveryRoot, SkillIgnoreRules ignore,
            PromptTemplateSourceScope scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > options.MaximumDepth) { diagnostics.Add(new("warning", "DepthLimit", directory)); return; }
            var remaining = options.MaximumEntries - entriesSeen;
            if (remaining <= 0) { diagnostics.Add(new("warning", "EntryLimit", directory)); return; }
            var prefix = root ? "" : Relative(directory);
            foreach (var leaf in new[] { ".gitignore", ".ignore", ".fdignore" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ignorePath = Path.Combine(directory, leaf);
                try
                {
                    if (files.Stat(ignorePath) != PromptTemplateFileKind.File) continue;
                    var content = await ReadBytes(ignorePath).ConfigureAwait(false);
                    if (content is not null) ignore.AddFile(System.Text.Encoding.UTF8.GetString(content), prefix);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { diagnostics.Add(new("warning", "IgnoreRulesFailed", ignorePath)); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Reserve the whole remaining allowance before borrowing enumeration. A failed enumeration
            // may have observed every admitted entry; only a successful bounded result proves a refund.
            entriesSeen += remaining;
            var entries = files.ReadDirectory(directory, remaining);
            if (entries.IsDefault || entries.Length > remaining) throw new IOException("Skill entry limit.");
            entriesSeen -= remaining - entries.Length;
            foreach (var entry in entries)
                if (entry.Path.Length > 4096 || entry.Name.Length > 255 || !Path.IsPathFullyQualified(entry.Path) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(entry.Path)), directory,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    entry.Name != Path.GetFileName(entry.Path)) throw new IOException("Invalid skill directory entry.");
            var declared = entries.FirstOrDefault(entry => entry.Name == "SKILL.md" && entry.Kind == PromptTemplateFileKind.File && !ignore.IsIgnored(Relative(entry.Path)));
            if (declared is not null) { await Read(declared.Path, scope).ConfigureAwait(false); return; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                if (ignore.IsIgnored(Relative(entry.Path), entry.Kind == PromptTemplateFileKind.Directory)) continue;
                if (entry.Kind == PromptTemplateFileKind.Directory) await Visit(entry.Path, false, depth + 1, discoveryRoot, ignore, scope).ConfigureAwait(false);
                else if (root && entry.Kind == PromptTemplateFileKind.File && entry.Name.EndsWith(".md", StringComparison.Ordinal))
                    await Read(entry.Path, scope).ConfigureAwait(false);
            }
            string Relative(string path) => Path.GetRelativePath(discoveryRoot, path).Replace(Path.DirectorySeparatorChar, '/');
        }
        async Task<byte[]?> ReadBytes(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readCount >= options.MaximumFiles || readBytes >= options.MaximumTotalBytes)
            { diagnostics.Add(new("warning", "ReadLimit", path)); return null; }
            readCount++;
            var budget = (int)Math.Min(options.MaximumFileBytes, options.MaximumTotalBytes - readBytes);
            byte[] bytes;
            try
            {
                bytes = await files.ReadFileAsync(path, budget, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes.Length > budget) throw new IOException("Skill read limit.");
                readBytes += bytes.Length;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { readBytes += budget; diagnostics.Add(new("warning", "ReadFailed", path)); return null; }
            return bytes;
        }
        async Task Read(string path, PromptTemplateSourceScope scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!paths.Add(path)) return;
            var bytes = await ReadBytes(path).ConfigureAwait(false); if (bytes is null) return;
            SkillMetadata metadata;
            try
            {
                var document = PromptTemplateParser.ExtractDocument(System.Text.Encoding.UTF8.GetString(bytes));
                metadata = document.FrontmatterYaml is null ? new() : decode(document.FrontmatterYaml);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            { if (Path.GetFileName(path) == "SKILL.md") diagnostics.Add(new("warning", "InvalidFrontmatter", path)); return; }
            cancellationToken.ThrowIfCancellationRequested();
            var description = metadata.Description;
            if (description is null || Trim(description).Length == 0)
            { if (Path.GetFileName(path) == "SKILL.md") diagnostics.Add(new("warning", "DescriptionRequired", path)); return; }
            var baseDir = Path.GetDirectoryName(path)!;
            var name = string.IsNullOrEmpty(metadata.Name) ? Path.GetFileName(baseDir) : metadata.Name;
            if (description.Length > 1024) diagnostics.Add(new("warning", "DescriptionTooLong", path));
            if (name.Length > 64 || name.Length == 0 || name.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
                name.StartsWith('-') || name.EndsWith('-') || name.Contains("--", StringComparison.Ordinal))
                diagnostics.Add(new("warning", "InvalidName", path)); // Source loads valid descriptions even when names warn.
            var skill = new SkillResource(name, description, path, baseDir,
                new(path, "local", scope, PromptTemplateSourceOrigin.TopLevel, baseDir), metadata.DisableModelInvocation);
            if (names.TryGetValue(name, out var winner)) diagnostics.Add(new("collision", "NameCollision", path, name, winner.FilePath));
            else
            {
                names.Add(name, skill); skills.Add(skill);
                var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
                var rawHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                var capture = System.Text.Json.JsonSerializer.Serialize(new { rawHash, skill });
                var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(capture))).ToLowerInvariant();
                descriptors.Add(new(key, fingerprint, skill));
            }
        }
    }

    public string FormatForPrompt(string fileReadTool = "read")
    {
        if (fileReadTool is not ("read" or "bash")) throw new ArgumentException("Unsupported skill read tool.");
        var visible = Skills.Where(skill => !skill.DisableModelInvocation).ToArray();
        if (visible.Length == 0) return "";
        var lines = new List<string> { "\n\nThe following skills provide specialized instructions for specific tasks.",
            fileReadTool == "read" ? "Use the read tool to load a skill's file when the task matches its description." : "Use bash to load a skill's file when the task matches its description.",
            "When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.", "", "<available_skills>" };
        foreach (var skill in visible)
            lines.AddRange(["  <skill>", $"    <name>{Xml(skill.Name)}</name>", $"    <description>{Xml(skill.Description)}</description>",
                $"    <location>{Xml(skill.FilePath)}</location>", "  </skill>"]);
        lines.Add("</available_skills>"); return string.Join('\n', lines);
    }

    public async ValueTask<string> ExpandAsync(string text, Func<SkillDiagnostic, CancellationToken, ValueTask>? report = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(text);
        if (!text.StartsWith("/skill:", StringComparison.Ordinal)) return text;
        var space = text.IndexOf(' '); var name = space < 0 ? text[7..] : text[7..space];
        var skill = Skills.FirstOrDefault(skill => skill.Name == name); if (skill is null) return text;
        try
        {
            var bytes = await files.ReadFileAsync(skill.FilePath, options.MaximumFileBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length > options.MaximumFileBytes) throw new IOException("Skill read limit.");
            var document = PromptTemplateParser.ExtractDocument(System.Text.Encoding.UTF8.GetString(bytes));
            if (document.FrontmatterYaml is not null) _ = decode(document.FrontmatterYaml);
            cancellationToken.ThrowIfCancellationRequested();
            var body = Trim(document.Body);
            var block = $"<skill name=\"{skill.Name}\" location=\"{skill.FilePath}\">\nReferences are relative to {skill.BaseDir}.\n\n{body}\n</skill>";
            var args = space < 0 ? "" : Trim(text[(space + 1)..]); return args.Length == 0 ? block : block + "\n\n" + args;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            if (report is not null) await report(new("warning", "ExpansionReadFailed", skill.FilePath), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); return text;
        }
    }

    public static ParsedSkillBlock? ParseBlock(string text)
    {
        // skills.ts parseSkillBlock matches any message text; the non-backtracking match is linear in its length.
        ArgumentNullException.ThrowIfNull(text);
        var match = Regex.Match(text, "^<skill name=\"([^\"]+)\" location=\"([^\"]+)\">\\n([\\s\\S]*?)\\n</skill>(?:\\n\\n([\\s\\S]+))?$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (!match.Success) return null;
        var user = Trim(match.Groups[4].Value); return new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, user.Length == 0 ? null : user);
    }
    private static string Xml(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&apos;", StringComparison.Ordinal);
    private static string Trim(string text)
    { int first = 0, last = text.Length; while (first < last && PromptTemplateParser.IsEcmaWhitespace(text[first])) first++;
        while (last > first && PromptTemplateParser.IsEcmaWhitespace(text[last - 1])) last--; return text[first..last]; }
}

/// <summary>Raw borrowed input handlers run first; selected skills expand before the outer template reducer.</summary>
public sealed class SkillInputAdmission(SkillResourceSet skills, IPromptInputAdmission? rawHandlers = null,
    Func<SkillDiagnostic, CancellationToken, ValueTask>? report = null) : IPromptInputAdmission
{
    public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); input = PromptInputValue.Own(input, PromptInputAdmissionOptions.Unbounded);
        var decision = rawHandlers is null ? new PromptInputDecision(PromptInputAction.Continue) : await rawHandlers.ReduceAsync(input, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); var effective = PromptInputValue.Apply(input, decision, PromptInputAdmissionOptions.Unbounded);
        if (decision.Action == PromptInputAction.Handled) return decision;
        var expanded = await skills.ExpandAsync(effective.Text, report, token).ConfigureAwait(false);
        var final = PromptInputValue.Own(effective with { Text = expanded }, PromptInputAdmissionOptions.Unbounded); token.ThrowIfCancellationRequested();
        return decision.Action == PromptInputAction.Transform || expanded != effective.Text
            ? new(PromptInputAction.Transform, final.Text, final.Images) : new(PromptInputAction.Continue);
    }
}
