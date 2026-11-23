using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace PiSharp.Extensions.Facade.Execution;

/// <summary>Optional explicitly installed owner capability, not executable authorization.</summary>
public interface IExtensionExecFacade
{
    string OwnerId { get; }
    long OwnerGeneration { get; }
    Task<ExtensionExecResult> ExecAsync(string command, ImmutableArray<string> arguments,
        ExtensionExecOptions? options = null);
}

public sealed record ExtensionExecOptions(CancellationToken Signal = default,
    double? TimeoutMilliseconds = null, string? Cwd = null)
{
    /// <summary>Combined stdout/stderr byte limit (source maxBuffer); exceeding it stops the command with
    /// <see cref="ExtensionExecResult.Truncated"/>. Null is the default, 256 MiB capped by the runtime string limit.</summary>
    public long? MaxBuffer { get; init; }
}

/// <param name="Code">The exit code, or 128 + the signal number when the command was ended instead of exiting (source exec.ts: 143 for
/// SIGTERM, 137 for SIGKILL). A command the host stopped (timeout, abort, output over the limit) reads 137 on Linux and macOS, where
/// its process group is killed, and 143 on Windows, where its job is terminated.</param>
/// <param name="Killed">The host stopped the command: timeout, abort, or output over <see cref="ExtensionExecOptions.MaxBuffer"/>.</param>
public sealed record ExtensionExecResult(string Stdout, string Stderr, int Code, bool Killed)
{
    /// <summary>Output exceeded the byte limit (source truncated): stdout and stderr are incomplete and the code is nonzero.</summary>
    public bool Truncated { get; init; }
}
