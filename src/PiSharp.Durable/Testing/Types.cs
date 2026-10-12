// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/testing/types.ts. Upstream injects runner assertions; these
// cases assert themselves and fail by throwing StorageConformanceException or EnvConformanceException, which any test runner reports. The
// environment provider hands out an IFileSystem until the ExecutionEnv joins in P9 (then shell and symlink options join too).
namespace PiSharp.Durable.Testing;

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

/// <summary>Calls <c>use</c> exactly once with a file system whose <see cref="Env.IFileSystem.Cwd"/> is a fresh, empty, writable directory,
/// awaits it, then cleans up (upstream <c>EnvConformanceProvider</c>, file-system half).</summary>
public delegate Task EnvConformanceProvider(Func<Env.IFileSystem, Task> use);

/// <summary>One runner-independent environment conformance case.</summary>
/// <param name="Name">Upstream's case name.</param>
/// <param name="Run">Runs the case against a file system from the provider.</param>
public sealed record EnvConformanceCase(string Name, Func<Task> Run)
{
    /// <summary>The name as a lowercase, hyphen-separated identifier, for test IDs.</summary>
    public string Slug => System.Text.RegularExpressions.Regex.Replace(Name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

/// <summary>An environment conformance assertion failed.</summary>
public sealed class EnvConformanceException(string message) : Exception(message);
