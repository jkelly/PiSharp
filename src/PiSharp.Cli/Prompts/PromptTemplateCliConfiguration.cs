using System.Collections.Immutable;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent.Resources;

namespace PiSharp.Cli.Prompts;

/// <summary>Explicit prompt flags only. No implicit directories, settings or project authority.</summary>
internal sealed record PromptTemplateCliConfiguration(ImmutableArray<PromptTemplatePathSelection> Selections)
{
    internal const string Flags = "[--prompt-template <absolute file or directory> (repeatable)]";

    // Call at the existing parser's option boundary, so opaque values of other options are never
    // mistaken for prompt flags. The caller retains argument-count limits and all other parsing.
    internal static bool TryConsume(string[] args, ref int index,
        ImmutableArray<PromptTemplatePathSelection>.Builder selections)
    {
        ArgumentNullException.ThrowIfNull(args); ArgumentNullException.ThrowIfNull(selections);
        if ((uint)index >= (uint)args.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (args[index] != "--prompt-template") return false;
        if (++index == args.Length) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        var path = SessionCommands.Absolute(args[index]);
        selections.Add(new(path, new(path, "local", PromptTemplateSourceScope.Temporary,
            PromptTemplateSourceOrigin.TopLevel), ReportMissingPath: true));
        return true;
    }
}
