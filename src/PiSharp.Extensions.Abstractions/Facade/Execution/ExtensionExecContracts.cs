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
    [Experimental("PISHARP_EXEC001")] public long? MaxBuffer { get; init; }
}

public sealed record ExtensionExecResult(string Stdout, string Stderr, int Code, bool Killed)
{
    /// <summary>Output exceeded the byte limit (source truncated): stdout and stderr are incomplete and the code is nonzero.</summary>
    [Experimental("PISHARP_EXEC001")] public bool Truncated { get; init; }
}
