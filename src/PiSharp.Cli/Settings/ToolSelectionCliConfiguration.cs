using System.Collections.Immutable;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent.Configuration;
using PiSharp.CodingAgent.ToolSelection;

namespace PiSharp.Cli.Settings;

internal sealed record InitialToolSelection(ImmutableArray<string> Names, bool IncludeDefaultExtensions)
{
    /// <summary>Captured CLI cap for the core owner's atomic initial-admission integration.</summary>
    internal AllowedToolSelection? LifetimePolicy { get; init; }
    internal bool UseAvailableDefaults { get; init; }
}

internal sealed record ToolSelectionCliOptions(ImmutableArray<string>? Tools = null,
    ImmutableArray<string> Excluded = default, bool NoTools = false, bool NoBuiltinTools = false)
{
    internal bool IsSpecified => Tools is not null || !Excluded.IsDefault || NoTools || NoBuiltinTools;
}

internal static class ToolSelectionCliConfiguration
{
    internal const string Flags = "[--tools|-t <comma-separated names; empty selects no tools>] [--no-tools|-nt] [--no-builtin-tools|-nbt] [--exclude-tools|-xt <comma-separated names>]";
    internal static bool TryConsume(string[] args, ref int index, ref ImmutableArray<string>? selection)
    {
        if (args[index] is not ("--tools" or "-t")) return false;
        if (++index >= args.Length) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        selection = args[index].Split(',').Select(value => value.Trim()).Where(value => value.Length > 0).ToImmutableArray();
        return true;
    }
    internal static bool TryConsume(string[] args, ref int index, ref ToolSelectionCliOptions selection)
    {
        switch (args[index])
        {
            case "--no-tools": case "-nt": selection = selection with { NoTools = true }; return true;
            case "--no-builtin-tools": case "-nbt": selection = selection with { NoBuiltinTools = true }; return true;
            case "--tools": case "-t":
                selection = selection with { Tools = ReadNames(args, ref index) }; return true;
            case "--exclude-tools": case "-xt":
                selection = selection with { Excluded = ReadNames(args, ref index) }; return true;
            default: return false;
        }
    }
    private static ImmutableArray<string> ReadNames(string[] args, ref int index)
    {
        if (++index >= args.Length) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        return args[index].Split(',').Select(value => value.Trim()).Where(value => value.Length > 0).ToImmutableArray();
    }
    internal static InitialToolSelection? ResolveOptions(ToolSelectionCliOptions cli, StartupSettingsSnapshot? settings)
    {
        if (!cli.IsSpecified) return Resolve((ImmutableArray<string>?)null, settings);
        try
        {
            var configured = settings is null ? null : StartupToolSelection.Resolve(settings.Values);
            var policy = AllowedToolSelection.Create(cli.Tools, cli.Excluded,
                cli.NoTools ? NoToolsMode.All : cli.NoBuiltinTools ? NoToolsMode.Builtin : NoToolsMode.None,
                configuredDefaults: configured);
            return new(policy.InitialNames, policy.IncludeDefaultExtensions)
            { LifetimePolicy = policy, UseAvailableDefaults = cli.Tools is null && configured is null };
        }
        catch (ArgumentException) { throw new SessionCommandException(SessionCommandFailure.InvalidArguments); }
    }
    internal static InitialToolSelection? Resolve(ImmutableArray<string>? cli, StartupSettingsSnapshot? settings)
    {
        if (cli is { } names)
        {
            try { return new(names, false) { LifetimePolicy = AllowedToolSelection.Create(names) }; }
            catch (ArgumentException) { throw new SessionCommandException(SessionCommandFailure.InvalidArguments); }
        }
        return settings is not null && StartupToolSelection.Resolve(settings.Values) is { } configured ? new(configured, true) : null;
    }
}
