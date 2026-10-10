// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/index.ts (builtInExtensions),
// packages/coding-agent/src/core/resource-loader.ts (reload: the extension paths of -e and resolve() merged, all but -e with
// noExtensions, disabledBuiltinExtensions left out; loadExtensionPaths: "Unknown built-in extension"; omitReplacedExtensions),
// packages/coding-agent/src/core/extensions/types.ts (InlineExtension builtin and replaceable) and packages/coding-agent/src/main.ts
// (disabledBuiltinExtensions ["mcp"] for --no-mcp; the "Failed to load extension" and "Extension package" diagnostics).
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>A built-in extension (extensions/index.ts): its name, whether an extension registering one of the names it registers while
/// loading (<c>tool:</c>, <c>command:</c> or <c>flag:</c>) takes over from it, and those names.</summary>
internal sealed record PiBuiltinExtension(string Name, bool Replaceable, ImmutableArray<string> Registers);

/// <summary>The built-in extensions a Pi entry run loads. <c>builtin:&lt;name&gt;</c> is an extension resource like a file: it loads by
/// default, <c>pi config</c> lists it, <c>-builtin:&lt;name&gt;</c> in the <c>extensions</c> setting (or a project override) and
/// <c>--no-extensions</c> disable it, <c>-e builtin:&lt;name&gt;</c> loads it explicitly and <c>--no-mcp</c> leaves <c>mcp</c> out.
/// The session reads <see cref="IsEnabled"/> when it registers the built-in features (the llama.cpp provider and <c>/llama</c>, the
/// codemode and tool_search tools, MCP servers and <c>/mcp</c>); a reload resolves the set again.</summary>
internal sealed class PiBuiltinExtensions
{
    internal const string PathPrefix = "builtin:";
    internal const string Llama = "llama.cpp", Codemode = "codemode", ToolSearch = "tool-search", Mcp = "mcp";

    /// <summary>builtInExtensions in order. codemode, tool-search and mcp are replaceable: an extension that registers the
    /// <c>codemode</c> tool, the <c>tool_search</c> tool or the <c>/mcp</c> command takes over instead of running alongside.</summary>
    internal static ImmutableArray<PiBuiltinExtension> All { get; } =
    [
        new(Llama, false, ["command:llama"]),
        new(Codemode, true, ["tool:codemode"]),
        new(ToolSearch, true, ["tool:tool_search"]),
        new(Mcp, true, ["command:mcp"])
    ];

    internal static ImmutableArray<string> Names { get; } = [.. All.Select(builtin => builtin.Name)];

    private ImmutableHashSet<string> enabled;

    internal PiBuiltinExtensions(IEnumerable<string> enabled) => this.enabled = enabled.ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>Every built-in extension enabled (hosts outside the Pi entry).</summary>
    internal static PiBuiltinExtensions AllEnabled() => new(Names);

    internal ImmutableHashSet<string> Enabled => Volatile.Read(ref enabled);
    internal bool IsEnabled(string name) => Enabled.Contains(name);

    /// <summary>Replaces the enabled set (a reload).</summary>
    internal void Set(ImmutableHashSet<string> value) => Volatile.Write(ref enabled, value);

    /// <summary>The commands the built-in extensions register (llama/index.ts and mcp/index.ts registerCommand), in load order.</summary>
    internal static ImmutableArray<(string Builtin, string Name, string Description)> Commands { get; } =
    [
        (Llama, "llama", "Manage llama.cpp router models"),
        (Mcp, "mcp", "Manage MCP servers: sign in, reconnect, enable or disable, and change exposure")
    ];

    private ImmutableDictionary<string, string> scopes = ImmutableDictionary<string, string>.Empty;

    /// <summary>The sourceInfo scope of a built-in extension (resource-loader.ts metadataByPath from resolve(): "user", or "project" for
    /// a project override).</summary>
    internal string ScopeOf(string name) => Volatile.Read(ref scopes).GetValueOrDefault(name, "user");

    /// <summary>Takes the scopes of the built-in extension resources <paramref name="resolved"/> lists (startup and every reload).</summary>
    internal void SetScopes(PiSharp.Cli.Packages.PiResolvedPaths? resolved) => Volatile.Write(ref scopes,
        (resolved?.Extensions ?? []).Where(resource => resource.Path.StartsWith(PathPrefix, StringComparison.Ordinal))
            .GroupBy(resource => resource.Path[PathPrefix.Length..], StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.First().Metadata.Scope, StringComparer.Ordinal));

    internal sealed record Resolution(ImmutableHashSet<string> Enabled, ImmutableArray<PiDiagnostic> Errors, ImmutableArray<PiDiagnostic> Warnings);

    /// <summary>resource-loader.ts: the <c>builtin:</c> paths of <c>mergePaths(-e, resolve())</c> (only the <c>-e</c> ones with
    /// <paramref name="noExtensions"/>) without <paramref name="disabled"/>; an unknown name is a load error, and a replaceable built-in
    /// whose tool, command or flag another loaded extension registers is left out with a warning (omitReplacedExtensions).</summary>
    internal static Resolution Resolve(IEnumerable<string>? cliSources, PiSharp.Cli.Packages.PiResolvedPaths? resolved, bool noExtensions,
        IEnumerable<string> disabled, IEnumerable<PiLoadedExtension> loaded)
    {
        var off = disabled.ToHashSet(StringComparer.Ordinal);
        var paths = (cliSources ?? []).Where(source => source.StartsWith(PathPrefix, StringComparison.Ordinal))
            .Concat(noExtensions || resolved is null ? [] : resolved.Extensions.Where(resource => resource.Enabled &&
                resource.Path.StartsWith(PathPrefix, StringComparison.Ordinal)).Select(resource => resource.Path))
            .Distinct(StringComparer.Ordinal).Where(path => !off.Contains(path[PathPrefix.Length..]));
        var names = new List<string>(); var errors = ImmutableArray.CreateBuilder<PiDiagnostic>();
        foreach (var path in paths)
            if (Names.Contains(path[PathPrefix.Length..])) names.Add(path[PathPrefix.Length..]);
            else errors.Add(new("error", $"Failed to load extension \"{path}\": Unknown built-in extension: {path}"));
        // omitReplacedExtensions: the names the other (non-replaceable) extensions registered while loading, the last one winning.
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var extension in loaded)
            foreach (var name in Registered(extension.Descriptor)) taken[name] = extension.Path;
        foreach (var builtin in All.Where(builtin => !builtin.Replaceable && names.Contains(builtin.Name)))
            foreach (var name in builtin.Registers) taken[name] = PathPrefix + builtin.Name;
        var warnings = ImmutableArray.CreateBuilder<PiDiagnostic>();
        foreach (var name in names.ToList())
        {
            var builtin = All.First(candidate => candidate.Name == name);
            if (!builtin.Replaceable || builtin.Registers.FirstOrDefault(taken.ContainsKey) is not { } registered) continue;
            names.Remove(name);
            var kind = registered[..registered.IndexOf(':')]; var raw = registered[(kind.Length + 1)..];
            var shown = kind == "command" ? "/" + raw : kind == "flag" ? "--" + raw : raw;
            warnings.Add(new("warning", $"Extension package \"{PathPrefix}{name}\": Extension {taken[registered]} registers {kind} `{shown}`, so built-in " +
                $"extension `{name}` was not loaded. To use `{name}`, run `pi config` and make sure it is enabled under Built-in extensions, then disable " +
                "or remove the existing extension. We recommend only having one or the other loaded at a time."));
        }
        return new(names.ToImmutableHashSet(StringComparer.Ordinal), errors.ToImmutable(), warnings.ToImmutable());
    }

    private static IEnumerable<string> Registered(JsonObject descriptor) =>
        new[] { ("tools", "tool"), ("commands", "command"), ("flags", "flag") }.SelectMany(kind =>
            (descriptor[kind.Item1] as JsonArray ?? []).OfType<JsonObject>().Select(item => item["name"]?.GetValue<string>())
                .OfType<string>().Select(name => kind.Item2 + ":" + name));
}
