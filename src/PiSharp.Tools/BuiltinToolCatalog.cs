using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;

namespace PiSharp.Tools;

/// <summary>Owned declaration/adapter pair. Registration and selection grant no execution authority.</summary>
public sealed record BuiltinToolRegistration(JsonData Declaration, IPreparedToolAdapter Adapter)
{
    public string Name => Adapter.Name;
}

/// <summary>Instance-owned built-ins with one write/edit queue and explicitly borrowed optional executors.</summary>
public sealed class BuiltinToolCatalog
{
    private readonly FileMutationQueue _mutations;
    public ImmutableArray<BuiltinToolRegistration> Registered { get; }
    public FileMutationQueueSnapshot MutationSnapshot => _mutations.Snapshot;

    public BuiltinToolCatalog(string workingDirectory, string homeDirectory, IDirectoryFileOperations? operations = null,
        ReadWriteToolOptions? readWriteOptions = null, EditToolOptions? editOptions = null,
        BashTool? bash = null, IFindExecutor? find = null, IGrepExecutor? grep = null, IFileAccessProbe? editAccessProbe = null,
        IGrepContextReader? grepContextReader = null)
    {
        var files = operations ?? new LocalFileOperations();
        var readOptions = readWriteOptions ?? new(); var editProfile = editOptions ?? new();
        _mutations = new(async (path, token) =>
        {
            var key = await files.CanonicalizeAsync(path, token).ConfigureAwait(false);
            return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
        }, new(MaximumKeyCharacters: Math.Max(readOptions.MaximumPathCharacters, editProfile.MaximumPathCharacters)));
        var readWrite = new ReadWriteTools(workingDirectory, homeDirectory, files, readOptions, _mutations);
        var edit = new EditTool(workingDirectory, homeDirectory, _mutations, files, editProfile, editAccessProbe);
        var listing = new LsTool(workingDirectory, homeDirectory, files);
        var tools = ImmutableArray.CreateBuilder<BuiltinToolRegistration>();
        tools.Add(new(readWrite.Declarations[0], readWrite.Adapters[0]));
        if (bash is not null) tools.Add(new(bash.Declaration, bash));
        tools.Add(new(edit.Declaration, edit));
        tools.Add(new(readWrite.Declarations[1], readWrite.Adapters[1]));
        if (grep is not null)
        {
            var search = new GrepTool(workingDirectory, homeDirectory, grep, files, grepContextReader);
            tools.Add(new(search.Declaration, search.Adapter));
        }
        if (find is not null)
        {
            var search = new FindTool(workingDirectory, homeDirectory, find, files);
            tools.Add(new(search.Declaration, search.Adapter));
        }
        tools.Add(new(listing.Declaration, listing.Adapter));
        Registered = tools.ToImmutable();
    }

    /// <summary>Caller order is retained; unknown, unavailable and duplicate names fail without invoking adapters.</summary>
    public ImmutableArray<BuiltinToolRegistration> Select(ImmutableArray<string> names)
    {
        if (names.IsDefault || names.Length > Registered.Length) throw new ArgumentException("Invalid built-in selection.", nameof(names));
        var selected = ImmutableArray.CreateBuilder<BuiltinToolRegistration>(names.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name is null || name.Length is < 1 or > 64 || !seen.Add(name))
                throw new ArgumentException("Invalid or duplicate built-in name.", nameof(names));
            var tool = Registered.FirstOrDefault(tool => tool.Name == name);
            if (tool is null) throw new ArgumentException("Unknown or unavailable built-in tool: " + name, nameof(names));
            selected.Add(tool);
        }
        return selected.ToImmutable();
    }

    // Preserve pinned factory order. These complete factories reject a missing borrowed capability.
    public ImmutableArray<BuiltinToolRegistration> CodingTools() => Select(["read", "bash", "edit", "write"]);
    public ImmutableArray<BuiltinToolRegistration> ReadOnlyTools() => Select(["read", "grep", "find", "ls"]);
}
