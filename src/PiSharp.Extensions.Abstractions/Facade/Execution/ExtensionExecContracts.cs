using System.Collections.Immutable;

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
    double? TimeoutMilliseconds = null, string? Cwd = null);

public sealed record ExtensionExecResult(string Stdout, string Stderr, int Code, bool Killed);
