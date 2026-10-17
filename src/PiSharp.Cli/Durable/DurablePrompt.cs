// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/prompt.ts
// pi's system prompt sections come from PiSharp's port of core/system-prompt.ts buildSystemPromptSections (OriginalSystemPromptBuilder),
// with the read, bash, edit and write tools' prompt contributions (BuiltinToolPrompts, core/tools/*.ts), the context files of
// core/resource-loader.ts loadProjectContextFiles (PiResources) and the skills of core/skills.ts loadSkills (SkillDiscovery).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Cli.Pi;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent.Resources.Skills;
using PiSharp.Durable;
using PiSharp.Tools;

namespace PiSharp.Cli.Durable;

/// <summary>Where the prompt's resources come from: pi's agent directory, the home directory paths expand against, and the documentation
/// paths of the <c>docs</c> section (config.ts getReadmePath, getDocsPath, getExamplesPath).</summary>
internal sealed record DurablePromptResources(string AgentDir, string Home, OriginalPromptDocumentation Documentation);

/// <summary>pi's system prompt as one extension (upstream <c>createPiPrompt</c>).</summary>
internal static class DurablePrompt
{
    /// <summary>The tools with prompt contributions (upstream <c>CONTRIBUTIONS</c>).</summary>
    private static readonly ImmutableArray<string> Contributions = ["read", "bash", "edit", "write"];

    /// <summary>pi's section order; buildSystemPromptSections omits the ones without content (upstream <c>KEYS</c>).</summary>
    internal static readonly ImmutableArray<string> Keys = ["preamble", "tools", "rules", "docs", "project_context", "skills", "cwd"];

    private sealed record Loaded(ImmutableArray<OriginalPromptContextFile> ContextFiles, ImmutableArray<OriginalPromptSkill> Skills);

    /// <summary>
    /// pi's system prompt as one extension: the sections of buildSystemPromptSections for the request's tools and the conversation's
    /// directory. Context files and skills load once per directory, like pi at startup.
    /// </summary>
    internal static Extension Create(PiSettings settings, string fallbackCwd, DurablePromptResources resources)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resources);
        var loaded = new ConcurrentDictionary<string, Lazy<Task<Loaded>>>(StringComparer.Ordinal);
        Task<Loaded> Load(string cwd) => loaded.GetOrAdd(cwd, key => new(() => LoadAsync(key, settings, resources))).Value;

        // The sections of one request render from one build.
        var built = new ConditionalWeakTable<PromptInput, Task<IReadOnlyDictionary<string, string>>>();
        Task<IReadOnlyDictionary<string, string>> Build(PromptInput input) => built.GetValue(input, key => BuildAsync(key));

        async Task<IReadOnlyDictionary<string, string>> BuildAsync(PromptInput input)
        {
            var cwd = input.Env?.Cwd ?? input.Agent.Cwd ?? fallbackCwd;
            if (!Path.IsPathFullyQualified(cwd)) cwd = Path.GetFullPath(cwd);
            ImmutableArray<string> selected = [.. input.Agent.Tools.Select(tool => tool.Name)];
            var contributed = selected.Where(name => Contributions.Contains(name)).Distinct(StringComparer.Ordinal).ToArray();
            var resourcesOfCwd = await Load(cwd).ConfigureAwait(false);
            var admission = new OriginalSystemPromptAdmission
            {
                SelectedTools = selected,
                ToolSnippets = [.. contributed.Select(name => KeyValuePair.Create(name, BuiltinToolPrompts.Snippets[name]))],
                ToolGuidelines = [.. contributed.Select(name => KeyValuePair.Create(name, BuiltinToolPrompts.Guidelines[name]))],
                ContextFiles = resourcesOfCwd.ContextFiles,
                Skills = resourcesOfCwd.Skills,
                Documentation = resources.Documentation,
            };
            var snapshot = OriginalSystemPromptBuilder.Capture(admission, cwd, selected);
            return OriginalSystemPromptBuilder.Sections(snapshot).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        return Define.Extension(new Extension
        {
            Name = "pi-prompt",
            // The built sections carry their own tags.
            Sections = [.. Keys.Select(key => Define.Section(key, async (input, _) =>
                (await Build(input).ConfigureAwait(false)).TryGetValue(key, out var text) ? text : null, tag: false))],
        });
    }

    /// <summary>The context files (loadProjectContextFiles) and skills (loadSkills with the default locations and the settings' skill
    /// paths) of <paramref name="cwd"/>.</summary>
    private static async Task<Loaded> LoadAsync(string cwd, PiSettings settings, DurablePromptResources resources)
    {
        var contextFiles = PiResources.LoadProjectContextFiles(cwd, resources.AgentDir)
            .Select(file => new OriginalPromptContextFile(file.Path, file.Content)).ToImmutableArray();
        ImmutableArray<OriginalPromptSkill> skills = [];
        try
        {
            ImmutableArray<SkillPathSelection> paths = [.. settings.Strings("skills")
                .Select(path => new SkillPathSelection(PiPaths.ResolvePath(path, cwd, resources.Home, trim: true)))];
            var request = new SkillDiscoveryRequest(new SkillRootGrant(Path.GetFullPath(resources.AgentDir), true), new SkillRootGrant(cwd, true), paths);
            var set = await SkillDiscovery.LoadAsync(request, SkillFrontendDecoder.Decode).ConfigureAwait(false);
            skills = [.. set.Skills.Select(PiSystemPrompt.Skill)];
        }
        // loadSkills reports problems as diagnostics, which the durable prompt drops; a skill set that cannot load is empty.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException) { }
        return new(contextFiles, skills);
    }
}
