using System.Collections.Immutable;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent.Resources.Skills;

namespace PiSharp.Cli.Skills;

internal sealed record SkillCliConfiguration(ImmutableArray<SkillPathSelection> Selections)
{
    internal const string Flags = "[--skill <absolute approved Markdown file or directory> (repeatable)]";
    internal static bool TryConsume(string[] args, ref int index, ImmutableArray<SkillPathSelection>.Builder selections)
    {
        if (args[index] != "--skill") return false;
        if (++index >= args.Length || selections.Count == 128) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        selections.Add(new(SessionCommands.Absolute(args[index]))); return true;
    }
}
internal static class SkillFrontendDecoder
{
    internal static SkillMetadata Decode(string yaml) =>
#if PISHARP_PROMPT_YAML
        PiSharp.PromptTemplates.Yaml.SkillYamlDecoder.Decode(yaml);
#else
        throw new NotSupportedException("Standard YAML skill frontmatter is unavailable in this profile.");
#endif
}
