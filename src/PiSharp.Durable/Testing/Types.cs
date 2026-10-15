// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/testing/types.ts. Upstream's assertion interface is
// IConformanceAssertions (Assertions.cs builds one). The storage kit asserts itself and fails by throwing StorageConformanceException (it
// predates the injected assertions and keeps that contract); the environment kit takes its assertions from EnvConformanceOptions, by default
// ones that throw EnvConformanceException, which any test runner reports.
using System.Collections.Immutable;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Testing;

/// <summary>The assertions a conformance kit makes (upstream <c>StorageConformanceAssertions</c> and its alias
/// <c>EnvConformanceAssertions</c>). Each fails by throwing the runner's failure.</summary>
public interface IConformanceAssertions
{
    /// <summary>Fails unless <paramref name="value"/> is true.</summary>
    void Ok(bool value, string? message = null);

    /// <summary>Fails unless the values are equal by <see cref="EqualityComparer{T}.Default"/>.</summary>
    void StrictEqual<T>(T actual, T expected, string? message = null);

    /// <summary>Fails unless the values are structurally equal: sequences element by element, everything else by <see cref="object.Equals(object?)"/>.</summary>
    void DeepEqual(object? actual, object? expected, string? message = null);

    /// <summary>Fails unless every property of <paramref name="expected"/> (an anonymous object, say) is deeply equal to the property of the same
    /// name of <paramref name="actual"/>.</summary>
    void PartialDeepEqual(object? actual, object expected, string? message = null);

    /// <summary>Fails unless <paramref name="actual"/> is greater than <paramref name="expected"/>.</summary>
    void GreaterThan(double actual, double expected, string? message = null);

    /// <summary>Fails unless <paramref name="operation"/> throws an exception whose message contains <paramref name="messageIncludes"/>.</summary>
    Task RejectsAsync(Func<Task> operation, string messageIncludes);
}

/// <summary>Calls <c>use</c> exactly once with a storage for one case and awaits it, then releases the storage. A provider may hand out a
/// fresh backend, or one that is closed and reopened between operations to run the suite across reopen.</summary>
public delegate Task StorageConformanceProvider(Func<IStorage, Task> use);

/// <summary>One runner-independent storage conformance case.</summary>
/// <param name="Name">Upstream's case name.</param>
/// <param name="Run">Runs the case against a storage from the provider.</param>
public sealed record StorageConformanceCase(string Name, Func<Task> Run)
{
    /// <summary>The name as a lowercase, hyphen-separated identifier, for test IDs.</summary>
    public string Slug => System.Text.RegularExpressions.Regex.Replace(Name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

/// <summary>A storage conformance assertion failed.</summary>
public sealed class StorageConformanceException(string message) : Exception(message);

/// <summary>Calls <c>use</c> exactly once with an environment whose <see cref="IFileSystem.Cwd"/> is a fresh, empty, writable directory,
/// awaits it, then cleans up (upstream <c>EnvConformanceProvider</c>).</summary>
public delegate Task EnvConformanceProvider(Func<IExecutionEnv, Task> use);

/// <summary>Options of <see cref="EnvConformance.Create(EnvConformanceOptions)"/> (upstream <c>EnvConformanceOptions</c>).</summary>
public sealed record EnvConformanceOptions
{
    /// <summary>Hands out an environment per case.</summary>
    public required EnvConformanceProvider WithEnv { get; init; }

    /// <summary>The assertions; by default ones that throw <see cref="EnvConformanceException"/>.</summary>
    public IConformanceAssertions? Assertions { get; init; }

    /// <summary>Program and flag that run a POSIX shell script from the next argument; default <c>["sh", "-c"]</c>.</summary>
    public ImmutableArray<string> Shell { get; init; } = ["sh", "-c"];

    /// <summary>Whether the shell's <c>ln -s</c> creates symbolic links; default true.</summary>
    public bool Symlinks { get; init; } = true;

    /// <summary>Whether the environment implements the optional <see cref="IFileSystem.ListFilesAsync"/>, which the suite then checks;
    /// default false.</summary>
    public bool ListFiles { get; init; }
}

/// <summary>One runner-independent environment conformance case.</summary>
/// <param name="Name">Upstream's case name.</param>
/// <param name="Run">Runs the case against an environment from the provider.</param>
public sealed record EnvConformanceCase(string Name, Func<Task> Run)
{
    /// <summary>Cases that wait for an environment's watch latency need longer than a test runner's default timeout.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>The name as a lowercase, hyphen-separated identifier, for test IDs.</summary>
    public string Slug => System.Text.RegularExpressions.Regex.Replace(Name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

/// <summary>An environment conformance assertion failed.</summary>
public sealed class EnvConformanceException(string message) : Exception(message);
