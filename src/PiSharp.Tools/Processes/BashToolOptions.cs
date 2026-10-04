using System.Collections.Immutable;

namespace PiSharp.Tools.Processes;

/// <summary>Trusted explicit configuration. Environment is the complete child environment, not an overlay.</summary>
public sealed record BashToolOptions(string Executable, string WorkingDirectory,
    ImmutableDictionary<string, string> Environment, string SpillDirectory,
    int MaximumCommandCharacters = 12_000, int MaximumArgumentCharacters = 96_000);
