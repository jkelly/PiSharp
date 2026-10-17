// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/testing/runner.ts, and testing/index.ts, which only re-exports
// the kits (the PiSharp.Durable.Testing namespace is that subpath here). Upstream registers the cases with a Vitest/Jest-compatible runner's
// describe/it/expect; a .NET runner implements IConformanceTestRunner.
namespace PiSharp.Durable.Testing;

/// <summary>A test runner the conformance kits register with (upstream <c>StorageConformanceRunner</c>).</summary>
public interface IConformanceTestRunner
{
    /// <summary>Groups the cases <paramref name="suite"/> registers under <paramref name="name"/>.</summary>
    void Describe(string name, Action suite);

    /// <summary>Registers one case; <paramref name="timeoutMs"/> overrides the runner's default timeout.</summary>
    void It(string name, Func<Task> test, int? timeoutMs = null);

    /// <summary>The runner's assertions (upstream <c>createExpectAssertions(runner.expect)</c>).</summary>
    IConformanceAssertions Assertions { get; }
}

/// <summary>Registers the runner-independent cases with a test runner (upstream <c>registerStorageConformance</c> and
/// <c>registerEnvConformance</c>).</summary>
public static class ConformanceRunner
{
    /// <summary>Registers the storage conformance cases under <paramref name="name"/>. The storage kit asserts itself, failing with
    /// <see cref="StorageConformanceException"/>.</summary>
    public static void RegisterStorageConformance(IConformanceTestRunner runner, string name, StorageConformanceProvider withStorage)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var cases = StorageConformance.Create(withStorage);
        runner.Describe(name, () =>
        {
            foreach (var testCase in cases) runner.It(testCase.Name, testCase.Run);
        });
    }

    /// <summary>Registers the <see cref="Env.IExecutionEnv"/> cases under <paramref name="name"/>, with the runner's assertions.
    /// <paramref name="shell"/> is the program and flag that run a POSIX shell script (default <c>["sh", "-c"]</c>); <paramref name="symlinks"/>
    /// says whether its <c>ln -s</c> creates symbolic links; <paramref name="listFiles"/> whether the environment implements the optional
    /// <c>ListFilesAsync</c> (upstream's <c>Pick&lt;EnvConformanceOptions, "shell" | "symlinks" | "listFiles"&gt;</c>).</summary>
    public static void RegisterEnvConformance(IConformanceTestRunner runner, string name, EnvConformanceProvider withEnv,
        System.Collections.Immutable.ImmutableArray<string> shell = default, bool symlinks = true, bool listFiles = false)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var options = new EnvConformanceOptions { WithEnv = withEnv, Assertions = runner.Assertions, Symlinks = symlinks, ListFiles = listFiles };
        var cases = EnvConformance.Create(shell.IsDefaultOrEmpty ? options : options with { Shell = shell });
        runner.Describe(name, () =>
        {
            foreach (var testCase in cases) runner.It(testCase.Name, testCase.Run, testCase.TimeoutMs);
        });
    }
}
