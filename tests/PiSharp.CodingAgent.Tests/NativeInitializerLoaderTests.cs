using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Cli.Mcp;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent.Tests;

internal static class NativeInitializerLoaderTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("native-initializer.actual-loader-install-and-retire", () => Run(0));
        yield return ("native-initializer.foreign-registration-factory-zero-effects", () => Run(1));
        yield return ("native-initializer.foreign-mcp-factory-zero-effects", () => Run(2));
    }
    private static void Check(bool value) { if (!value) throw new IOException("Actual default loader control failed."); }
    private static async Task Run(int mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = "fixture.context." + Guid.NewGuid().ToString("N");
        var log = Path.Combine(root, "callback.log"); var sessionPath = Path.Combine(root, "session.jsonl");
        var package = Path.Combine(root, "package"); var snapshots = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(package); Directory.CreateDirectory(snapshots);
        var failures = new List<Exception>(); var originals = new List<Original>(); PersistentAgentSession? session = null;
        ReplaceableAgentSession? owner = null; NativeExtensionActivation? activation = null;
        ExtensionRegistry? foreignRegistry = null; NativeExtensionRegistrationBridge? foreignBridge = null;
        try
        {
            var assembly = Path.GetFileName(typeof(NativeInitializerLoaderEntry).Assembly.Location);
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
                if (Path.GetFileName(file) is not ("PiSharp.Extensions.Abstractions.dll" or "PiSharp.Contracts.dll"))
                    File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
            foreach (var suffix in new[] { ".deps.json", ".runtimeconfig.json" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(assembly) + suffix),
                    Path.Combine(package, Path.GetFileNameWithoutExtension(assembly) + suffix));
            var hashes = Directory.GetFiles(package).ToDictionary(file => Path.GetFileName(file),
                file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id, packageVersion = "0.0.1",
                hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly,
                entryType = typeof(NativeInitializerLoaderEntry).FullName, tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = new[] { "owned-descriptor-callbacks" }, declaredCapabilities = new[] { "commands" },
                resourcePaths = hashes.Keys.Where(name => name != assembly).ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            var manifestPath = Path.Combine(root, "manifest.json"); var approvalPath = Path.Combine(root, "approval.json");
            File.WriteAllText(manifestPath, manifest);
            File.WriteAllText(approvalPath, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
                packageRoot = package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
                sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
                sessionPath, workspace = root, snapshotRoot = snapshots, enabledTools = Array.Empty<string>(), enabledCommands = new[] { "fixture-context" } }));
            AppContext.SetData("PiSharp.NativeInitializer." + id, log);
            var model = new ModelDescriptor("first", "openai-responses", "offline");
            var runtime = new SessionRuntimeRegistry([new(model, new NoTransport())], [], new Deny());
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "default-loader", timestamp = "2026-01-01T00:00:00Z", cwd = root }));
            var sequence = 0;
            var createOriginal = PersistentAgentSession.CreateAsync(sessionPath, header, runtime, model, () => 1, () => "entry-" + ++sequence);
            session = await Join(createOriginal, originals);
            owner = new ReplaceableAgentSession(session, (_, _) => Task.FromException<PersistentAgentSession>(new NotSupportedException()));
            var configuration = NativeExtensionConfiguration.Optional(package, manifestPath, approvalPath, snapshots, [], commands: ["fixture-context"])!;
            var preflightOriginal = configuration.PreflightAsync(sessionPath, root, default);
            var preflight = await Join(preflightOriginal, originals);
            // Actual default read/action host, with one explicit caller-owned initializer installation.
            var values = new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>());
            var binders = 0;
            if (mode != 0) foreignRegistry = new ExtensionRegistry();
            if (mode == 1) foreignBridge = new NativeExtensionRegistrationBridge(foreignRegistry!, values, [], new NoProviderConfiguration());
            var foreignMcp = mode == 2 ? new McpExtensionRegistrationBridge(foreignRegistry!, McpConfigurationReader.Load(null, null, true)) : null;
            var installation = new NativeExtensionInitializerInstallation(
                registry => foreignBridge ?? new NativeExtensionRegistrationBridge(registry, values, [], new NoProviderConfiguration()),
                (_, facade) => { binders++; File.AppendAllText(log, "binder\n"); facade.RegisterFlag("fixture-mode", new(ExtensionFlagKind.Boolean, DefaultValue: ExtensionFlagValue.Boolean(false))); },
                foreignMcp is null ? null : _ => foreignMcp!, foreignMcp is null ? null : _ => binders++);
            var loadOriginal = NativeExtensionActivation.LoadAsync(preflight, default, configuredInitializerInstallation: installation);
            if (mode != 0)
            {
                Exception? expected = null;
                try { activation = await Join(loadOriginal, originals); } catch (NativeExtensionException error) { expected = error; }
                Check(expected is not null && loadOriginal.IsFaulted && !loadOriginal.IsCanceled && binders == 0 && !File.Exists(log));
                Check(foreignRegistry!.CaptureSnapshot().Registrations.IsEmpty && !values.TryGetValue("fixture-mode", out _) && !Directory.EnumerateFileSystemEntries(snapshots).Any());
                Check(foreignBridge?.CaptureModels().IsEmpty ?? foreignMcp!.CaptureSnapshot().RegisteredServers.IsEmpty);
                // Expected refusal is retained as task evidence, but is not a control failure.
                foreach (var record in originals.Where(record => ReferenceEquals(record.Task, loadOriginal))) record.Expected = true;
            }
            if (mode == 0)
            {
            activation = await Join(loadOriginal, originals);
            Check(binders == 1 && activation.RegistrationInstallation!.CaptureFlags().Length == 1 && values.TryGetValue("fixture-mode", out var flag) && flag == ExtensionFlagValue.Boolean(false));
            activation.RegistrationInstallation!.ConfigureModelCatalog(runtime, runtime.CaptureModelCatalog().Bindings);
            activation.Bind(new Deny(), new()); activation.AttachOwner(owner);
            var commandOriginal = activation.TryExecuteAsync("/fixture-context", default).AsTask();
            Check(await Join(commandOriginal, originals));
            Check(File.ReadAllText(log) == "binder\ninitialize\n" + root + "|first|True\n");
            var disposeOriginal = activation.DisposeAsync().AsTask(); await Join(disposeOriginal, originals); activation = null;
            Check(File.ReadAllText(log).EndsWith("stale-rejected\ndispose\n", StringComparison.Ordinal));
            Check(!Directory.EnumerateFileSystemEntries(snapshots).Any());
            }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (activation is not null) await Cleanup(() => activation.DisposeAsync(), originals, failures);
            if (owner is not null) await Cleanup(() => owner.DisposeAsync(), originals, failures);
            else if (session is not null) await Cleanup(() => session.DisposeAsync(), originals, failures);
            if (foreignRegistry is not null) await Cleanup(() => foreignRegistry.DisposeAsync(), originals, failures);
            try { foreignBridge?.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { AppContext.SetData("PiSharp.NativeInitializer." + id, null); } catch (Exception error) { failures.Add(error); }
            try { Directory.Delete(root, true); } catch (Exception error) { failures.Add(error); }
        }
        foreach (var record in originals)
        {
            if (record.Expected && failures.Count == 0) continue;
            if (record.Aggregate is not null) failures.Add(record.Aggregate);
            if (record.Direct is not null) failures.Add(record.Direct);
        }
        if (failures.Count > 0) throw new AggregateException("Loader/control/cleanup originals.", failures.Distinct<Exception>(ReferenceEqualityComparer.Instance));
    }
    private sealed class Original(Task task)
    { internal readonly Task Task = task; internal AggregateException? Aggregate; internal Exception? Direct; internal bool Expected; }
    private static async Task<T> Join<T>(Task<T> original, List<Original> records)
    {
        var record = new Original(original); records.Add(record);
        try { return await original; }
        catch (Exception error) { record.Aggregate = original.Exception; record.Direct = error; throw; }
    }
    private static async Task Join(Task original, List<Original> records)
    {
        var record = new Original(original); records.Add(record);
        try { await original; }
        catch (Exception error) { record.Aggregate = original.Exception; record.Direct = error; throw; }
    }
    private static async Task Cleanup(Func<ValueTask> factory, List<Original> records, List<Exception> failures)
    {
        try
        {
            // Invocation and one-time materialization are both inside the collector. A synchronous
            // cleanup failure must not prevent the remaining owner/session cleanup originals.
            var original = factory().AsTask();
            await Join(original, records);
        }
        catch (Exception error) { failures.Add(error); }
    }
    private sealed class NoProviderConfiguration : IExtensionProviderConfigurationAdapter
    { public ExtensionProviderDefinition Resolve(string name, JsonData configuration) => throw new IOException("Loader flag control must not acquire a provider."); }
    private sealed class Deny : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new IOException("Loader reads must not invoke transport.")); yield break; }
    }
}

// The real loader imports this entry from the freshly built fixture package. Only shared SDK
// contracts cross the load-context boundary; private copies are declared/hash-bound artifacts.
public sealed class NativeInitializerLoaderEntry : IPiSharpExtension
{
    private string? log; private IExtensionCommandContext? saved; private IExtensionContextReadHost? reads;
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
    {
        log = AppContext.GetData("PiSharp.NativeInitializer." + registry.OwnerId) as string ?? throw new IOException("Missing fixture log.");
        if (File.ReadAllText(log) != "binder\n") throw new IOException("Initializer binder did not precede original initialization exactly once.");
        File.AppendAllText(log, "initialize\n");
        registry.RegisterCommand(new("fixture-context", "fixture-context", "default host reads", async (_, context, _) =>
        {
            var host = (context as IExtensionFacadeHostContext)?.FacadeHost ?? throw new IOException("Default facade missing.");
            saved = context; reads = host;
            var model = host.GetModel(context)?.Value.GetProperty("id").GetString();
            await File.AppendAllTextAsync(log!, host.GetCwd(context) + "|" + model + "|" + host.IsIdle(context) + "\n");
        }));
        return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (saved is null || reads is null || log is null) throw new IOException("Default callback did not run.");
        try { _ = reads.GetCwd(saved); throw new IOException("Saved callback unexpectedly remained usable."); }
        catch (InvalidOperationException) { await File.AppendAllTextAsync(log, "stale-rejected\n"); }
        await File.AppendAllTextAsync(log, "dispose\n");
    }
}
