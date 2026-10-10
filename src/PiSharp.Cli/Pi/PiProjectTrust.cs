// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/trust-manager.ts (ProjectTrustStore,
// hasTrustRequiringProjectResources, getProjectTrustOptions) and packages/coding-agent/src/core/project-trust.ts (resolveProjectTrusted).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Cli.Pi;

internal sealed record ProjectTrustStoreEntry(string Path, bool Decision);
internal sealed record ProjectTrustUpdate(string Path, bool? Decision);
internal sealed record ProjectTrustOption(string Label, bool Trusted, ImmutableArray<ProjectTrustUpdate> Updates, string? SavedPath = null);

/// <summary>Source ProjectTrustStore: <c>&lt;agentDir&gt;/trust.json</c> maps canonical directories to true, false or null, and a
/// directory inherits the nearest decision of its ancestors. Reads and writes hold the same <c>trust.json.lock</c> directory lock
/// proper-lockfile uses upstream, so Pi and PiSharp can share the file.</summary>
internal sealed class ProjectTrustStore
{
    private static readonly string[] TrustRequiringProjectConfigResources =
        ["settings.json", "mcp.json", "extensions", "skills", "prompts", "themes", "SYSTEM.md", "APPEND_SYSTEM.md"];
    internal string TrustPath { get; }
    private readonly string home;

    internal ProjectTrustStore(string agentDir, string home)
    {
        TrustPath = Path.Join(Path.GetFullPath(agentDir), "trust.json");
        this.home = home;
    }

    private string NormalizeCwd(string cwd) => PiPaths.Canonicalize(PiPaths.ResolvePath(cwd, Directory.GetCurrentDirectory(), home));

    internal bool? Get(string cwd) => GetEntry(cwd)?.Decision;

    internal ProjectTrustStoreEntry? GetEntry(string cwd) => WithLock(() =>
    {
        var data = ReadTrustFile();
        var current = NormalizeCwd(cwd);
        while (true)
        {
            if (data.TryGetValue(current, out var value) && value is { } decision) return new ProjectTrustStoreEntry(current, decision);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) return null;
            current = parent;
        }
    });

    internal void Set(string cwd, bool? decision) => SetMany([new(cwd, decision)]);

    internal void SetMany(IEnumerable<ProjectTrustUpdate> decisions) => WithLock(() =>
    {
        var data = ReadTrustFile();
        foreach (var update in decisions)
        {
            var key = NormalizeCwd(update.Path);
            if (update.Decision is null) data.Remove(key); else data[key] = update.Decision;
        }
        WriteTrustFile(data);
        return 0;
    });

    /// <summary>Source readTrustFile, with its error texts.</summary>
    private Dictionary<string, bool?> ReadTrustFile()
    {
        var data = new Dictionary<string, bool?>(StringComparer.Ordinal);
        if (!File.Exists(TrustPath)) return data;
        JsonNode? parsed;
        try { parsed = PiJson.Parse(PiPaths.ReadText(TrustPath)); }
        catch (JsonException error) { throw new InvalidDataException($"Failed to read trust store {TrustPath}: {error.Message}"); }
        if (parsed is not JsonObject entries) throw new InvalidDataException($"Invalid trust store {TrustPath}: expected an object");
        foreach (var (key, value) in entries)
        {
            if (value is null) { data[key] = null; continue; }
            if (value is not JsonValue scalar || !scalar.TryGetValue<bool>(out var decision))
                throw new InvalidDataException($"Invalid trust store {TrustPath}: value for {JsonSerializer.Serialize(key)} must be true, false, or null");
            data[key] = decision;
        }
        return data;
    }

    /// <summary>Source writeTrustFile: keys sorted, two-space JSON, a trailing newline.</summary>
    private void WriteTrustFile(Dictionary<string, bool?> data)
    {
        var sorted = new JsonObject();
        foreach (var key in data.Keys.OrderBy(key => key, StringComparer.Ordinal)) sorted[key] = data[key] is { } value ? JsonValue.Create(value) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(TrustPath)!);
        File.WriteAllText(TrustPath, PiJson.Stringify(sorted, indent: true) + "\n", new System.Text.UTF8Encoding(false));
    }

    /// <summary>proper-lockfile: an exclusive <c>mkdir</c> of <c>trust.json.lock</c>, ten attempts 20 ms apart, stale after 10 s.</summary>
    private T WithLock<T>(Func<T> run)
    {
        var directory = Path.GetDirectoryName(TrustPath)!;
        Directory.CreateDirectory(directory);
        var lockPath = TrustPath + ".lock";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(lockPath))
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(lockPath) > TimeSpan.FromSeconds(10)) Directory.Delete(lockPath);
                    else throw new IOException("ELOCKED");
                }
                Directory.CreateDirectory(lockPath);
                break;
            }
            catch (IOException) when (attempt < 10) { Thread.Sleep(20); }
        }
        try { return run(); }
        finally { try { Directory.Delete(lockPath); } catch (IOException) { } }
    }

    /// <summary>Source hasTrustRequiringProjectResources: trust-requiring entries under <c>cwd/.pi</c>, or <c>.agents/skills</c> in cwd or
    /// an ancestor other than the user's own <c>~/.agents/skills</c>.</summary>
    internal static bool HasTrustRequiringProjectResources(string cwd, string home)
    {
        var userAgentsSkills = Path.Join(PiPaths.Canonicalize(Path.GetFullPath(home)), ".agents", "skills");
        var current = PiPaths.Canonicalize(Path.GetFullPath(cwd));
        var configDir = Path.Join(current, PiConfig.ConfigDirName);
        if (TrustRequiringProjectConfigResources.Any(entry => Path.Exists(Path.Join(configDir, entry)))) return true;
        while (true)
        {
            var agentsSkills = Path.Join(current, ".agents", "skills");
            if (agentsSkills != userAgentsSkills && Path.Exists(agentsSkills)) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) return false;
            current = parent;
        }
    }

    /// <summary>Source getProjectTrustParentPath.</summary>
    internal string? ParentPath(string cwd)
    {
        var trustPath = NormalizeCwd(cwd); var parent = Path.GetDirectoryName(trustPath);
        return parent is null || parent == trustPath ? null : parent;
    }

    /// <summary>Source getProjectTrustOptions: the choices of the trust prompt (IMPL-I renders them).</summary>
    internal ImmutableArray<ProjectTrustOption> Options(string cwd, bool includeSessionOnly = false)
    {
        var trustPath = NormalizeCwd(cwd);
        var options = ImmutableArray.CreateBuilder<ProjectTrustOption>();
        options.Add(new("Trust", true, [new(trustPath, true)], trustPath));
        if (ParentPath(cwd) is { } parent)
            options.Add(new($"Trust parent folder ({parent})", true, [new(parent, true), new(trustPath, null)], parent));
        if (includeSessionOnly) options.Add(new("Trust (this session only)", true, []));
        options.Add(new("Do not trust", false, [new(trustPath, false)], trustPath));
        if (includeSessionOnly) options.Add(new("Do not trust (this session only)", false, []));
        return options.ToImmutable();
    }

    /// <summary>Source formatProjectTrustPrompt.</summary>
    internal static string PromptText(string cwd) =>
        $"Trust project folder?\n{cwd}\n\nThis allows {PiConfig.AppName} to load {PiConfig.ConfigDirName} settings and resources, install missing project packages, and execute project extensions.";
}

/// <summary>The interactive trust prompt (IMPL-I): shows <paramref name="title"/> with the option labels and returns the chosen
/// option, or null when dismissed. Without one, an undecided project stays untrusted, as upstream does without a UI.</summary>
internal delegate Task<ProjectTrustOption?> PiProjectTrustPrompt(string title, ImmutableArray<ProjectTrustOption> options, CancellationToken cancellationToken);

/// <summary>Source resolveProjectTrusted for a cwd, and the trust seam other packages consume.</summary>
internal static class PiProjectTrust
{
    /// <summary>Source resolveProjectTrusted: the CLI override, then "no trust-requiring resources", then the project_trust extension
    /// handlers (<paramref name="extensionDecision"/>; a remembered decision is saved), then the stored decision, then
    /// <c>defaultProjectTrust</c>, then the prompt when a UI exists.</summary>
    internal static async Task<bool> ResolveAsync(string cwd, string home, ProjectTrustStore store, bool? trustOverride, string defaultProjectTrust,
        PiProjectTrustPrompt? prompt, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<PiProjectTrustDecision?>>? extensionDecision = null)
    {
        if (trustOverride is { } forced) return forced;
        if (!ProjectTrustStore.HasTrustRequiringProjectResources(cwd, home)) return true;
        if (extensionDecision is not null && await extensionDecision(cwd, cancellationToken).ConfigureAwait(false) is { } decided)
        {
            if (decided.Remember) store.Set(cwd, decided.Trusted);
            return decided.Trusted;
        }
        if (store.Get(cwd) is { } decision) return decision;
        switch (defaultProjectTrust)
        {
            case "always": return true;
            case "never": return false;
        }
        if (prompt is null) return false;
        var options = store.Options(cwd, includeSessionOnly: true);
        var selected = await prompt(ProjectTrustStore.PromptText(cwd), options, cancellationToken).ConfigureAwait(false);
        if (selected is null) return false;
        if (!selected.Updates.IsEmpty) store.SetMany(selected.Updates);
        return selected.Trusted;
    }

    /// <summary>A non-interactive trust check for hosts outside the Pi entry (the explicit <c>session</c> verbs, MCP project config):
    /// the stored decision, else the global <c>defaultProjectTrust</c>, for directories with trust-requiring project resources;
    /// directories without them are trusted, as upstream resolves them. The run's own entry uses <see cref="PiEntryOptions.ProjectTrusted"/>.</summary>
    internal static Func<string, bool> CreateResolver(string agentDir, string home)
    {
        var store = new ProjectTrustStore(agentDir, home);
        var defaults = PiSettings.Load(home, agentDir, projectTrusted: false).DefaultProjectTrust;
        return cwd =>
        {
            try { return ResolveAsync(cwd, home, store, null, defaults, null, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { return false; }
        };
    }

    /// <summary>The seam for project-local resources owned elsewhere (IMPL-H <c>.pi/mcp.json</c>, IMPL-E project extensions): true
    /// when the run trusts that directory's project files. The CLI resolves it once per cwd before the session starts.</summary>
    internal static Func<string, bool> Seam(IReadOnlyDictionary<string, bool> resolved) =>
        cwd => resolved.TryGetValue(Path.GetFullPath(cwd), out var trusted) && trusted;
}
