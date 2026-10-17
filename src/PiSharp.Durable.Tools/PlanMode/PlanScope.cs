// PiSharp-only (decision 0005): no upstream counterpart. Plan mode's containment of `write` and `edit`: the wrapped tools run with an
// execution environment whose mutations are checked against the plan directory, read from the plan-mode state document at execute time.
// `bash` and `powershell` are not wrapped (owner decision 2026-10-17): the prompt asks the agent not to change anything through the shell,
// which is not enforced.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Tools;

/// <summary>The <c>write</c> and <c>edit</c> wrappers of the <c>plan-mode</c> extension.</summary>
internal static class PlanScope
{
    /// <summary>
    /// <paramref name="inner"/> confined to the plan directory: the path argument is checked before the tool runs (so the model reads why),
    /// and the tool gets a <see cref="PlanScopedEnv"/>, which checks every mutation it makes. The directory is read from
    /// <see cref="PlanMode.StateDoc"/> when the call executes; without one, the call fails. The check does not depend on the mode, so a
    /// wrapped call that runs after plan mode ended is still confined.
    /// </summary>
    public static ToolRegistration Wrap(ToolRegistration inner) => inner with
    {
        Execute = async (arguments, api, context) =>
        {
            var root = await RootAsync(api, context).ConfigureAwait(false);
            var scoped = new PlanScopedEnv(ToolEnv.Require(api), root);
            if (arguments is JsonObjectValue args && args.TryGetValue("path", out var value) && value is JsonStringValue path)
            {
                if (PlanScopedEnv.RawProblem(path.Value) is { } problem) throw scoped.Denied(path.Value, problem);
                var absolutePath = await PathUtils.ResolveToolPathAsync(scoped, path.Value, context).ConfigureAwait(false);
                if (await scoped.CheckAsync(absolutePath, context).ConfigureAwait(false) is { } denied) throw denied;
            }
            return await inner.Execute(arguments, new PlanScopedApi(api, scoped), context).ConfigureAwait(false);
        },
    };

    /// <summary>The plan directory of the call's conversation; throws when none is set.</summary>
    internal static async Task<string> RootAsync(IDocumentReader reader, ConversationId conversationId, DurableContext context) =>
        PlanMode.StateOf(await reader.SnapshotAsync(PlanMode.StateDoc, conversationId, context).ConfigureAwait(false))?.Directory is { Length: > 0 } directory
            ? directory
            : throw new InvalidOperationException("Plan mode: no plan directory is set, so write and edit are disabled");

    private static Task<string> RootAsync(IToolExecutionApi api, DurableContext context) => RootAsync(api, api.ConversationId, context);
}

/// <summary>A tool execution api whose environment is confined to the plan directory.</summary>
internal sealed class PlanScopedApi(IToolExecutionApi inner, IExecutionEnv env) : DelegatingToolExecutionApi(inner)
{
    public override IExecutionEnv? Env => env;
}

/// <summary>
/// An execution environment that reads through <paramref name="inner"/> and allows a mutation (write, append, truncate, flush, rename of
/// both paths, directory creation, removal) only strictly inside <paramref name="root"/>; it never runs commands, creates temporary files or
/// watches. A path is allowed only when all of these hold, and denied on any doubt (an error while checking denies):
/// <list type="bullet">
/// <item>it has no device or UNC prefix (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>, <c>\\server</c>, <c>//</c>);</item>
/// <item>its absolute form (<c>..</c> resolved) lies strictly below the root's absolute form, so the root itself is never a target;</item>
/// <item>no name below the root is a Windows device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9, CONIN$, CONOUT$, with any extension), ends
/// in a dot or space, contains a colon (an alternate data stream), a wildcard, quote, pipe, angle bracket, backslash or control character, or
/// looks like an 8.3 short name (<c>~</c> followed by a digit);</item>
/// <item>no existing component below the root is a symbolic link or junction (checked without following links);</item>
/// <item>the canonical path of its deepest existing ancestor, joined with the names that do not exist yet, lies strictly below the
/// canonical root.</item>
/// </list>
/// The root is created when missing. A file the check allows can still be changed between the check and the write (TOCTOU) by another
/// process, and a hard link inside the root to a file outside it is not detected.
/// </summary>
internal sealed class PlanScopedEnv(IExecutionEnv inner, string root) : IExecutionEnv
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public string Root { get; } = root;

    /// <summary>Why <paramref name="path"/> may not be changed, or null when it may.</summary>
    public async Task<FileError?> CheckAsync(string path, DurableContext context)
    {
        try
        {
            return await CheckCoreAsync(path, context).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Denied(path, $"it could not be checked: {error.Message}");
        }
    }

    private async Task<FileError?> CheckCoreAsync(string path, DurableContext context)
    {
        if (string.IsNullOrEmpty(path)) return Denied(path ?? "", "the path is empty");
        if (HasDevicePrefix(path)) return Denied(path, "device and network paths are not allowed");
        var absolute = await inner.AbsolutePathAsync(path, context).ConfigureAwait(false);
        if (!absolute.IsOk) return Denied(path, absolute.Error.Message);
        var target = absolute.Value;
        if (HasDevicePrefix(target)) return Denied(path, "device and network paths are not allowed");
        var rootAbsolute = await inner.AbsolutePathAsync(Root, context).ConfigureAwait(false);
        if (!rootAbsolute.IsOk || HasDevicePrefix(rootAbsolute.Value)) return Denied(path, "the plan directory is not a local absolute path");
        var rootPath = rootAbsolute.Value;
        var separator = rootPath.Contains('\\') && !rootPath.Contains('/') ? '\\' : '/';

        // Lexically below the root, as given: the names below it are what gets created.
        var relative = Below(rootPath, target, separator);
        if (relative is null) return Denied(path, null);
        var names = relative.Split(separator);
        foreach (var name in names)
            if (NameProblem(name) is { } problem) return Denied(path, problem);

        // The root exists, so the canonical walk below always finds an existing ancestor.
        var created = await inner.CreateDirAsync(rootPath, new CreateDirOptions { Recursive = true }, context).ConfigureAwait(false);
        if (!created.IsOk) return Denied(path, $"the plan directory cannot be created: {created.Error.Message}");
        var rootCanonical = await inner.CanonicalPathAsync(rootPath, context).ConfigureAwait(false);
        if (!rootCanonical.IsOk) return Denied(path, $"the plan directory cannot be resolved: {rootCanonical.Error.Message}");
        // A plan directory that is (or links to) a file system root would confine nothing.
        if (rootCanonical.Value.TrimEnd(separator).Length == 0 || IsDriveRoot(rootCanonical.Value))
            return Denied(path, $"the plan directory resolves to the file system root {rootCanonical.Value}");

        // No existing component below the root is a link or junction (not followed).
        var prefix = rootPath;
        for (var index = 0; index < names.Length; index++)
        {
            prefix = prefix + separator + names[index];
            var info = await inner.FileInfoAsync(prefix, context).ConfigureAwait(false);
            if (!info.IsOk)
            {
                if (info.Error.Code == FileErrorCode.NotFound) break;
                return Denied(path, info.Error.Message);
            }
            if (info.Value.Kind == FileKind.Symlink) return Denied(path, $"{prefix} is a symbolic link or junction");
        }

        // The canonical deepest existing ancestor plus the names that do not exist yet.
        var probe = target;
        var missing = new List<string>();
        string canonical;
        while (true)
        {
            var resolved = await inner.CanonicalPathAsync(probe, context).ConfigureAwait(false);
            if (resolved.IsOk)
            {
                canonical = resolved.Value;
                break;
            }
            if (resolved.Error.Code != FileErrorCode.NotFound) return Denied(path, resolved.Error.Message);
            var cut = probe.LastIndexOf(separator);
            if (cut < rootPath.Length) return Denied(path, "no existing ancestor inside the plan directory");
            missing.Insert(0, probe[(cut + 1)..]);
            probe = probe[..cut];
        }
        var canonicalTarget = missing.Count == 0 ? canonical : canonical.TrimEnd(separator) + separator + string.Join(separator, missing);
        if (Below(rootCanonical.Value, canonicalTarget, separator) is null) return Denied(path, $"it resolves to {canonicalTarget}");
        return null;
    }

    /// <summary>The part of <paramref name="path"/> strictly below <paramref name="directory"/>, or null. A drive letter compares
    /// case-insensitively; everything else ordinally.</summary>
    private static string? Below(string directory, string path, char separator)
    {
        var parent = DriveUpper(directory).TrimEnd(separator);
        var child = DriveUpper(path);
        if (parent.Length == 0 || child.Length <= parent.Length + 1 || !child.StartsWith(parent, StringComparison.Ordinal) || child[parent.Length] != separator)
            return null;
        var relative = child[(parent.Length + 1)..].TrimEnd(separator);
        return relative.Length == 0 || relative.Split(separator).Any(name => name.Length == 0 || name is "." or "..") ? null : relative;
    }

    private static bool IsDriveRoot(string path) =>
        path.Length is 2 or 3 && path[1] == ':' && char.IsAsciiLetter(path[0]) && (path.Length == 2 || path[2] is '\\' or '/');

    private static string DriveUpper(string path) =>
        path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? char.ToUpperInvariant(path[0]) + path[1..] : path;

    /// <summary>Why a name below the root is refused, or null.</summary>
    private static string? NameProblem(string name)
    {
        if (name.Length == 0) return "a path name is empty";
        if (name.EndsWith('.') || name.EndsWith(' ')) return $"the name \"{name}\" ends in a dot or space";
        foreach (var character in name)
        {
            if (character < 0x20 || character is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\\' or '/')
                return $"the name \"{name}\" contains a character Windows reserves";
        }
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (DeviceNames.Contains(stem)) return $"\"{name}\" is a device name";
        for (var index = 0; index + 1 < name.Length; index++)
            if (name[index] == '~' && char.IsAsciiDigit(name[index + 1])) return $"\"{name}\" looks like an 8.3 short name";
        return null;
    }

    /// <summary>
    /// Why a path as the model wrote it is refused before it is resolved, or null: a name (other than <c>.</c> and <c>..</c>) that ends in a
    /// dot or white space, which Windows path normalization drops so the file written is not the one named, or a colon anywhere but a drive
    /// designator. Both <c>/</c> and <c>\</c> separate names here.
    /// </summary>
    internal static string? RawProblem(string path)
    {
        var names = path.Split('/', '\\');
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            if (name.Length == 0 || name is "." or "..") continue;
            if (index == 0 && name.Length == 2 && name[1] == ':' && char.IsAsciiLetter(name[0])) continue;
            if (name.EndsWith('.') || char.IsWhiteSpace(name[^1])) return $"the name \"{name}\" ends in a dot or space";
            if (name.Contains(':')) return $"the name \"{name}\" contains a colon (an alternate data stream)";
        }
        return null;
    }

    /// <summary>A device, extended-length or UNC prefix.</summary>
    internal static bool HasDevicePrefix(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
        path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith(@"/??/", StringComparison.Ordinal) ||
        path.StartsWith(@"\/", StringComparison.Ordinal) || path.StartsWith(@"/\", StringComparison.Ordinal);

    internal FileError Denied(string path, string? reason) =>
        new(FileErrorCode.PermissionDenied, $"Plan mode: {path} is outside the plan directory {Root}" + (reason is null ? "" : $" ({reason})"), path);

    private async Task<Result<T, FileError>> Checked<T>(string path, DurableContext context, Func<Task<Result<T, FileError>>> mutate) =>
        await CheckAsync(path, context).ConfigureAwait(false) is { } denied ? Result<T, FileError>.Err(denied) : await mutate().ConfigureAwait(false);

    private static Task<Result<T, FileError>> Refused<T>(string what, string? path) =>
        Task.FromResult<Result<T, FileError>>(new FileError(FileErrorCode.PermissionDenied, $"Plan mode: write and edit cannot {what}", path));

    // ─── IExecutionEnv ──────────────────────────────────────────────────────

    public string Id => inner.Id;

    public string Cwd
    {
        get => inner.Cwd;
        set => throw new NotSupportedException("Plan mode: the working directory of a confined environment cannot change");
    }

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, DurableContext context) => inner.AbsolutePathAsync(path, context);
    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, DurableContext context) => inner.JoinPathAsync(parts, context);
    public Task<Result<string, FileError>> ReadTextFileAsync(string path, DurableContext context) => inner.ReadTextFileAsync(path, context);
    public Task<Result<ITextLineReader, FileError>> OpenTextLineReaderAsync(string path, DurableContext context) => inner.OpenTextLineReaderAsync(path, context);
    public Task<Result<ImmutableArray<string>, FileError>> ReadTextLinesAsync(string path, ReadTextLinesOptions? options, DurableContext context) =>
        inner.ReadTextLinesAsync(path, options, context);
    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, DurableContext context) => inner.ReadBinaryFileAsync(path, context);
    public Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(string path, BinaryReaderOptions? options, DurableContext context) =>
        inner.OpenBinaryReaderAsync(path, options, context);
    public Task<Result<FileMetadata, FileError>> FileInfoAsync(string path, DurableContext context) => inner.FileInfoAsync(path, context);
    public Task<Result<ImmutableArray<FileMetadata>, FileError>> ListDirAsync(string path, DurableContext context) => inner.ListDirAsync(path, context);
    public Task<Result<FileListing, FileError>> ListFilesAsync(string path, ListFilesOptions? options, DurableContext context) =>
        inner.ListFilesAsync(path, options, context);
    public Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, DurableContext context) => inner.OpenDirReaderAsync(path, context);
    public Task<Result<string, FileError>> CanonicalPathAsync(string path, DurableContext context) => inner.CanonicalPathAsync(path, context);
    public Task<Result<bool, FileError>> ExistsAsync(string path, DurableContext context) => inner.ExistsAsync(path, context);

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) =>
        Checked(path, context, () => inner.WriteFileAsync(path, content, context));
    public Task<Result<Unit, FileError>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) =>
        Checked(path, context, () => inner.AppendFileAsync(path, content, context));
    public Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, DurableContext context) =>
        Checked(path, context, () => inner.TruncateFileAsync(path, size, context));
    public Task<Result<Unit, FileError>> FlushFileAsync(string path, DurableContext context) =>
        Checked(path, context, () => inner.FlushFileAsync(path, context));
    public Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, DurableContext context) =>
        Checked(sourcePath, context, () => Checked(destinationPath, context, () => inner.RenameFileAsync(sourcePath, destinationPath, context)));
    public Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, DurableContext context) =>
        Checked(path, context, () => inner.CreateDirAsync(path, options, context));
    public Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, DurableContext context) =>
        Checked(path, context, () => inner.RemoveAsync(path, options, context));

    public Task<Result<IFileWatcher, FileError>> WatchAsync(IReadOnlyList<WatchTarget> targets, Action<WatchChange> onChange, DurableContext context) =>
        Refused<IFileWatcher>("watch files", null);
    public Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, DurableContext context) => Refused<string>("create temporary directories", null);
    public Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, DurableContext context) => Refused<string>("create temporary files", null);

    public Task<Result<ShellExecResult, ExecutionError>> ExecAsync(ShellCommand command, ShellExecOptions? options, DurableContext context) =>
        Task.FromResult<Result<ShellExecResult, ExecutionError>>(new ExecutionError(ExecutionErrorCode.SpawnError, "Plan mode: write and edit cannot run commands"));

    /// <summary>Nothing: the inner environment is shared and its owner cleans it up.</summary>
    public Task CleanupAsync(DurableContext context) => Task.CompletedTask;
}
