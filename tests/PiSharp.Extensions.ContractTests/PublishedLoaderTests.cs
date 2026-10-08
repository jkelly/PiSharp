using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Discovery;
using PiSharp.Extensions.Runtime.Loading;

internal static class PublishedLoaderTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("published fixture denial and identity changes precede module/constructor execution", PreloadTrust),
        ("session-snapshot. published required feature rejects unavailable hosts before executable work", SessionFeatureAdmission),
        ("published conflicting private versions use exact owned bytes and shared host contracts", IsolatedPublishedPackages),
        ("private contract copies and an actually compiled future ABI reject before execution", SharedAbi),
        ("missing managed, framework, malformed-image and native dependencies fail closed", DependencyAdmission),
        ("published initialization failure/cancellation and loader close join rollback", ActivationSettlement),
        ("published callback/disposal barriers share cleanup and release cooperative load contexts", LifetimeSettlement),
        ("published private callback accepts exact short and long Windows path aliases while rejecting distinct locations", ManagedPathIdentity),
        ("quiescence. loaded published owner retains package and reservation until actual drain and shutdown", QuiescenceSettlement)
    ];

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void RequireQualifiedProfile()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Published fixture execution currently requires the Windows win-x64 profile; other qualification remains pending.");
    }

    private static async Task PreloadTrust()
    {
        RequireQualifiedProfile();
        using var fixtures = await Fixtures.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(fixtures.Options);
        var package = fixtures.One;
        var metadata = new ExtensionManifestReader().Read(package.Metadata);
        var inspected = await new ExtensionManifestReader().InspectAsync(package.Metadata, package.Root,
            ExtensionSourceScope.Explicit, "approved-fixture-suite", package.Inspection);
        True(inspected.MetadataPreflightPassed); False(inspected.IsLoadAuthorized);
        await Fails(loader.LoadAsync(package.Metadata, package.Root, ExtensionSourceScope.Explicit, "approved-fixture-suite",
            package.Inspection, null, registry), PluginLoadFailure.ExecutionTrustRequired);
        var execution = package.Execution;
        foreach (var changed in new[]
        {
            execution with { Disposition = PluginExecutionDisposition.Deny },
            execution with { CanonicalPackageRoot = package.Root + "-different" },
            execution with { ManifestValueSha256 = new string('0', 64) },
            execution with { ArtifactHashes = [] },
            execution with { SourceScope = ExtensionSourceScope.Project },
            execution with { EffectiveScopeId = "other-scope" },
            execution with { PolicyRevision = "obsolete-policy" },
            execution with { HostGeneration = 2 }
        }) await Fails(Load(loader, package, registry, changed), changed.Disposition == PluginExecutionDisposition.Deny ?
            PluginLoadFailure.ExecutionTrustDenied : PluginLoadFailure.ExecutionTrustBindingMismatch);
        await Fails(loader.LoadAsync(package.Metadata, package.Root, ExtensionSourceScope.Explicit, "approved-fixture-suite",
            null, execution, registry), PluginLoadFailure.MetadataRejected);
        using (var permissive = JsonDocument.Parse(package.Metadata.ToString().Insert(1, "/* retained */"),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }))
            await Fails(loader.LoadAsync(JsonData.FromElement(permissive.RootElement), package.Root, ExtensionSourceScope.Explicit,
                "approved-fixture-suite", package.Inspection, execution, registry), PluginLoadFailure.MetadataRejected);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => Load(loader, package, registry, token: canceled.Token));
        await File.AppendAllTextAsync(package.EntryPath, "authored tamper");
        await Fails(Load(loader, package, registry), PluginLoadFailure.MetadataRejected);
        Equal(0, fixtures.MarkerCount);
        Equal(0, Directory.GetDirectories(fixtures.Snapshots).Length);
        Equal(0, registry.CaptureSnapshot().Registrations.Length);
        True(metadata.IsValid);
    }

    private sealed class EmptySessionView : PiSharp.Extensions.IExtensionSessionViewProvider
    {
        internal int Captures;
        public PiSharp.Extensions.ExtensionSessionSnapshot? Capture(PiSharp.Extensions.IExtensionContext context)
        { Captures++; return new("session", 1, null, []); }
    }

    private static async Task SessionFeatureAdmission()
    {
        RequireQualifiedProfile();
        using var fixtures = await Fixtures.CreateAsync();
        await fixtures.One.RefreshAsync(["owned-descriptor-callbacks", "session-branch-snapshot"]);
        await using var unavailable = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(fixtures.Options);
        var failure = await Throws<PluginLoadException>(() => Load(loader, fixtures.One, unavailable));
        Equal(PluginLoadFailure.MetadataRejected, failure.Failure);
        Equal("host-features", failure.Operation);
        Equal(0, fixtures.MarkerCount);
        Equal(0, Directory.GetDirectories(fixtures.Snapshots).Length);
        Equal(0, unavailable.CaptureSnapshot().Registrations.Length);

        // The exact same approved package can load when this optional capability actually exists.
        var views = new EmptySessionView();
        await using var configured = new ExtensionRegistry(null, null, views);
        await using var loaded = await Load(loader, fixtures.One, configured);
        Equal(0, views.Captures); // Initializing a package grants no callback/session admission.
        var result = await configured.InvokeToolAsync(configured.CaptureSnapshot(), "fixture.one.value", JsonData.EmptyObject);
        Equal("one", result.Value.GetProperty("version").GetString());
        Equal(1, views.Captures);
        True(fixtures.MarkerCount > 0);
        await loaded.DisposeAsync();
        Equal(0, Directory.GetDirectories(fixtures.Snapshots).Length);
    }

    private static async Task IsolatedPublishedPackages()
    {
        RequireQualifiedProfile();
        using var fixtures = await Fixtures.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(fixtures.Options);
        var one = await Load(loader, fixtures.One, registry);
        var two = await Load(loader, fixtures.Two, registry);
        True(one.IsCollectible && two.IsCollectible);
        False(one.SnapshotDirectory == two.SnapshotDirectory);
        // Mutate/delete originals BEFORE the first private-library invocation. Execution stays bound to captured bytes.
        await File.AppendAllTextAsync(fixtures.One.EntryPath, "authored source mutation after admission");
        File.Delete(Path.Combine(fixtures.One.Root, "Fixture.Private.dll"));
        var captured = registry.CaptureSnapshot();
        var resultOne = await registry.InvokeToolAsync(captured, "fixture.one.value", JsonData.EmptyObject);
        var resultTwo = await registry.InvokeToolAsync(captured, "fixture.two.value", JsonData.EmptyObject);
        Equal("one", resultOne.Value.GetProperty("version").GetString());
        Equal("two", resultTwo.Value.GetProperty("version").GetString());
        foreach (var result in new[] { resultOne, resultTwo })
        { True(result.Value.GetProperty("sharedContracts").GetBoolean()); True(result.Value.GetProperty("sharedAbstractions").GetBoolean()); }
        var input = JsonData.Parse("{ \"echo\":true, \"n\":1.0, \"big\":9007199254740993, \"null\":null, \"opaque\":\"\\u0000\" }");
        var original = input.ToString();
        var echo = await registry.InvokeToolAsync(captured, "fixture.one.value", input);
        True(ReferenceEquals(input, echo)); Equal(original, echo.ToString()); Equal(original, input.ToString());
        // A host-private assembly is genuinely present in Default; the plugin must not fall through to it.
        True(AssemblyLoadContext.GetLoadContext(typeof(PublishedConsumer.PublishedConsumerExtension).Assembly) == AssemblyLoadContext.Default);
        await ResolutionFailure(() => registry.InvokeToolAsync(captured, "fixture.one.value", JsonData.Parse("{\"probePrivateDefault\":true}")).AsTask(), PluginLoadFailure.MissingDependency);
        await ResolutionFailure(() => registry.InvokeToolAsync(captured, "fixture.one.value", JsonData.Parse("{\"probeNative\":true}")).AsTask(), PluginLoadFailure.UnsupportedNativeDependency);
        var previousAction = AppContext.GetData("PiSharp.PublishedFixture.HostAction");
        AppContext.SetData("PiSharp.PublishedFixture.HostAction", (Action)(() =>
        {
            Equal(PluginLoadFailure.ReentrantDisposal, ThrowsSync<PluginLoadException>(() => { _ = one.DisposeAsync(); }).Failure);
            Equal(PluginLoadFailure.ReentrantDisposal, ThrowsSync<PluginLoadException>(() => { _ = loader.DisposeAsync(); }).Failure);
        }));
        try { await registry.InvokeToolAsync(captured, "fixture.one.value", JsonData.Parse("{\"probeHostAction\":true}")); }
        finally { AppContext.SetData("PiSharp.PublishedFixture.HostAction", previousAction); }
        var markers = fixtures.MarkerCount;
        await Fails(Load(loader, fixtures.Two, registry), PluginLoadFailure.DuplicateOwner);
        Equal(markers, fixtures.MarkerCount);
        await one.DisposeAsync(); await two.DisposeAsync();
        True(one.UnloadRequested && two.UnloadRequested);
        Equal(0, registry.CaptureSnapshot().Registrations.Length);
        Equal(0, Directory.GetDirectories(fixtures.Snapshots).Length);

        // Inclusive cumulative reservation: one full 16 MiB reservation fits, a second cannot overrun it.
        await using var bounded = new PluginAssemblyLoader(fixtures.Options with { MaximumRetainedSnapshotBytes = 16_777_216 });
        var accepted = await Load(bounded, fixtures.Two, registry);
        await Fails(Load(bounded, fixtures.Fail, registry), PluginLoadFailure.LimitExceeded);
        await accepted.DisposeAsync();
    }

    private static async Task ManagedPathIdentity()
    {
        RequireQualifiedProfile();
        using var fixtures = await Fixtures.CreateAsync();
        var contextType = typeof(PluginAssemblyLoader).Assembly.GetType("PiSharp.Extensions.Runtime.Loading.PluginLoadContext", throwOnError: true)!;
        var comparison = contextType.GetMethod("IsOwnedManagedPath", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Owned managed-path comparison is unavailable.");
        bool Matches(string resolved, string captured) =>
            (bool)(comparison.Invoke(null, [resolved, captured]) ?? throw new InvalidOperationException("Managed-path comparison returned no decision."));
        var source = Path.Combine(fixtures.One.Root, "Fixture.Private.dll");
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var sourceHash = SHA256.HashData(sourceBytes);
        var longParent = Path.Combine(fixtures.Snapshots, "long-owned-path");
        while (longParent.Length <= 300) longParent = Path.Combine(longParent, new string('p', 40));
        Directory.CreateDirectory(longParent);
        foreach (var parent in new[] { fixtures.Snapshots, longParent })
        {
            await using var registry = new ExtensionRegistry();
            await using var loader = new PluginAssemblyLoader(fixtures.Options with { SnapshotParentDirectory = parent });
            await using var loaded = await Load(loader, fixtures.One, registry);
            var captured = Path.Combine(loaded.SnapshotDirectory!, "Fixture.Private.dll");
            var isLong = parent == longParent;
            True(isLong ? captured.Length > 300 : captured.Length < 300,
                "The actual regression must cover a shorter captured path and one exceeding 300 characters.");
            var extended = @"\\?\" + captured;
            var entry = Path.Combine(loaded.SnapshotDirectory!, "PublishedFixture.One.dll");
            var resolved = new AssemblyDependencyResolver(entry).ResolveAssemblyToPath(AssemblyName.GetAssemblyName(source));
            True(resolved is not null, "Actual published resolver lost the approved private dependency.");
            foreach (var spelling in new[] { captured, extended, resolved! })
            {
                var bytes = await File.ReadAllBytesAsync(spelling);
                True(bytes.SequenceEqual(sourceBytes), "Readable alias changed the actual published DLL bytes.");
                True(CryptographicOperations.FixedTimeEquals(sourceHash, SHA256.HashData(bytes)),
                    "Normal, extended and resolver spellings did not retain the captured assembly hash.");
                True(Matches(spelling, captured), "Equivalent local managed path was rejected.");
                True(Matches(captured, spelling), "Owned-path normalization depended on alias direction.");
            }
            var result = await registry.InvokeToolAsync(registry.CaptureSnapshot(), "fixture.one.value", JsonData.EmptyObject);
            Equal("one", result.Value.GetProperty("version").GetString());
            True(result.Value.GetProperty("sharedContracts").GetBoolean());
            True(result.Value.GetProperty("sharedAbstractions").GetBoolean());

            var otherDirectory = Path.Combine(parent, "different-private-location");
            Directory.CreateDirectory(otherDirectory);
            var other = Path.Combine(otherDirectory, "Fixture.Private.dll");
            File.Copy(captured, other);
            var otherHash = SHA256.HashData(await File.ReadAllBytesAsync(other));
            True(CryptographicOperations.FixedTimeEquals(sourceHash, otherHash));
            Equal(Path.GetFileName(captured), Path.GetFileName(other));
            False(Matches(other, captured), "A different location inherited authority from an identical basename/hash.");
            False(Matches(@"\\?\" + other, captured), "Extended spelling bypassed different-location rejection.");
            False(Matches(captured, other), "Captured location mismatch was accepted in the opposite direction.");
            var unc = @"\\localhost\" + captured[0] + "$" + captured[2..];
            foreach (var rejected in new[]
            {
                unc, @"\\?\UNC\" + unc[2..], @"\\.\" + captured,
                @"\\?\Volume{00000000-0000-0000-0000-000000000000}\Fixture.Private.dll",
                @"\\?\" + Path.GetDirectoryName(captured) + @"\.\Fixture.Private.dll",
                "Fixture.Private.dll"
            }) False(Matches(rejected, captured), "UNC/device/volume/noncanonical/relative spelling widened snapshot authority.");
            File.Delete(other); Directory.Delete(otherDirectory);
        }
    }

    private static async Task SharedAbi()
    {
        RequireQualifiedProfile();
        using var fixtures = await Fixtures.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(fixtures.Options);
        foreach (var assembly in new[] { typeof(PiSharp.Extensions.IPiSharpExtension).Assembly, typeof(JsonData).Assembly })
        {
            var privateCopy = Path.Combine(fixtures.One.Root, Path.GetFileName(assembly.Location));
            File.Copy(assembly.Location, privateCopy);
            await fixtures.One.RefreshAsync();
            await Fails(Load(loader, fixtures.One, registry), PluginLoadFailure.UnsupportedAbi);
            File.Delete(privateCopy);
        }
        await fixtures.One.RefreshAsync();
        await Fails(Load(loader, fixtures.Future, registry), PluginLoadFailure.UnsupportedAbi);
        Equal(0, fixtures.MarkerCount);
        Equal(0, Directory.GetDirectories(fixtures.Snapshots).Length);
    }

    private static async Task DependencyAdmission()
    {
        RequireQualifiedProfile();
        using var fixtures = await Fixtures.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(fixtures.Options);
        File.Delete(Path.Combine(fixtures.One.Root, "Fixture.Private.dll"));
        await fixtures.One.RefreshAsync();
        await Fails(Load(loader, fixtures.One, registry), PluginLoadFailure.MissingDependency);
        await File.WriteAllTextAsync(fixtures.Two.RuntimePath,
            "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.AspNetCore.App\",\"version\":\"10.0.0\"}}}");
        await fixtures.Two.RefreshAsync();
        await Fails(Load(loader, fixtures.Two, registry), PluginLoadFailure.UnsupportedFramework);
        var deps = JsonNode.Parse(await File.ReadAllTextAsync(fixtures.Fail.DepsPath))!.AsObject();
        var target = deps["runtimeTarget"]!["name"]!.GetValue<string>();
        deps["targets"]![target]!.AsObject().First().Value!.AsObject()["native"] = new JsonObject { ["missing-native.dll"] = new JsonObject() };
        await File.WriteAllTextAsync(fixtures.Fail.DepsPath, deps.ToJsonString());
        await fixtures.Fail.RefreshAsync();
        await Fails(Load(loader, fixtures.Fail, registry), PluginLoadFailure.UnsupportedNativeDependency);
        await File.WriteAllTextAsync(fixtures.Fail.DepsPath, (await File.ReadAllTextAsync(fixtures.Fail.DepsPath)).Insert(1, "/* retained published-json comment */"));
        await fixtures.Fail.RefreshAsync();
        await Fails(Load(loader, fixtures.Fail, registry), PluginLoadFailure.InvalidPublishedPackage);
        await File.WriteAllTextAsync(fixtures.Future.EntryPath, "authored invalid PE image");
        await fixtures.Future.RefreshAsync();
        await Fails(Load(loader, fixtures.Future, registry), PluginLoadFailure.InvalidPublishedPackage);
        Equal(0, fixtures.MarkerCount);
        Equal(0, registry.CaptureSnapshot().Registrations.Length);
        Equal(0, Directory.GetDirectories(fixtures.Snapshots).Length);
    }

    private static async Task ActivationSettlement()
    {
        RequireQualifiedProfile();
        using var fixtures = await Fixtures.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(fixtures.Options);
        try
        {
            var healthy = await Load(loader, fixtures.Two, registry);
            await Fails(Load(loader, fixtures.Fail, registry), PluginLoadFailure.InitializationFailed);
            Equal(1, fixtures.Count("PublishedFixture.Fail", "module"));
            Equal(1, fixtures.Count("PublishedFixture.Fail", "constructor"));
            Equal(1, fixtures.Count("PublishedFixture.Fail", "dispose"));
            Equal(2, registry.CaptureSnapshot().Registrations.Length);
            var existing = await registry.ActivateAsync("fixture.one", new ExistingOwner());
            await Fails(Load(loader, fixtures.One, registry), PluginLoadFailure.InitializationFailed);
            Equal(1, fixtures.Count("PublishedFixture.One", "dispose"));
            var hostInput = JsonData.Parse("{\"stillActive\":true}");
            True(ReferenceEquals(hostInput, await registry.InvokeToolAsync(registry.CaptureSnapshot(), "host-existing", hostInput)));
            await existing.DisposeAsync();
            var entered = fixtures.AddGate("fixture.one.initialize-entered");
            fixtures.AddGate("fixture.one.initialize-release");
            var disposeEntered = fixtures.AddGate("fixture.one.dispose-entered");
            var disposeRelease = fixtures.AddGate("fixture.one.dispose-release");
            using var cancellation = new CancellationTokenSource();
            var loading = Load(loader, fixtures.One, registry, token: cancellation.Token);
            await Reach(entered.Task, loading);
            Equal(2, registry.CaptureSnapshot().Registrations.Length);
            cancellation.Cancel();
            try
            {
                await Reach(disposeEntered.Task, loading);
                False(loading.IsCompleted, "Canceled activation joins actual published disposal.");
                Equal(2, Directory.GetDirectories(fixtures.Snapshots).Length);
            }
            finally { disposeRelease.TrySetResult(); }
            await Throws<OperationCanceledException>(() => loading);
            Equal(2, fixtures.Count("PublishedFixture.One", "dispose"));
            Equal(1, Directory.GetDirectories(fixtures.Snapshots).Length);
            Equal("two", (await registry.InvokeToolAsync(registry.CaptureSnapshot(), "fixture.two.value", JsonData.EmptyObject)).Value.GetProperty("version").GetString());
            await healthy.DisposeAsync();

            // Closing a loader cancels and JOINS an admitted initializer, and concurrent close shares its settlement.
            entered = fixtures.AddGate("fixture.one.initialize-entered");
            disposeEntered = fixtures.AddGate("fixture.one.dispose-entered");
            disposeRelease = fixtures.AddGate("fixture.one.dispose-release");
            var owned = Load(loader, fixtures.One, registry);
            await Reach(entered.Task, owned);
            var closeOne = loader.DisposeAsync().AsTask();
            var closeTwo = loader.DisposeAsync().AsTask();
            True(ReferenceEquals(closeOne, closeTwo));
            try { await Reach(disposeEntered.Task, owned); False(closeOne.IsCompleted); False(owned.IsCompleted); }
            finally { disposeRelease.TrySetResult(); }
            await Throws<OperationCanceledException>(() => owned);
            await closeOne; await closeTwo;
            Equal(3, fixtures.Count("PublishedFixture.One", "dispose"));
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
            Equal(0, Directory.GetDirectories(fixtures.Snapshots).Length);
        }
        finally { fixtures.ReleaseGates(); }
    }

    private static async Task LifetimeSettlement()
    {
        RequireQualifiedProfile();
        using var fixtures = await Fixtures.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(fixtures.Options);
        try
        {
            var entered = fixtures.AddGate("fixture.one.observe-entered");
            var release = fixtures.AddGate("fixture.one.observe-release");
            var disposeEntered = fixtures.AddGate("fixture.one.dispose-entered");
            var disposeRelease = fixtures.AddGate("fixture.one.dispose-release");
            var loaded = await Load(loader, fixtures.One, registry);
            var captured = registry.CaptureSnapshot();
            var dispatch = registry.DispatchObservationsAsync(captured, "fixture.one.notice", JsonData.EmptyObject).AsTask();
            await Reach(entered.Task, dispatch);
            var closing = loaded.DisposeAsync().AsTask();
            try
            {
                False(closing.IsCompleted);
                False(disposeEntered.Task.IsCompleted, "Shutdown waits for the already admitted observer.");
                True(Directory.Exists(loaded.SnapshotDirectory));
                var entry = Path.Combine(loaded.SnapshotDirectory!, "PublishedFixture.One.dll");
                ThrowsSync<IOException>(() => { using var exclusive = File.Open(entry, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
                release.TrySetResult();
                await Throws<OperationCanceledException>(() => dispatch);
                await Reach(disposeEntered.Task, closing);
                var same = loaded.DisposeAsync().AsTask();
                True(ReferenceEquals(closing, same)); False(same.IsCompleted);
                disposeRelease.TrySetResult();
                await same;
            }
            finally
            {
                release.TrySetResult(); disposeRelease.TrySetResult();
                try { await dispatch; } catch (OperationCanceledException) { }
                await closing;
            }
            Equal(1, fixtures.Count("PublishedFixture.One", "dispose"));
            Equal(1, fixtures.Count("PublishedFixture.One", "observe-closed"));
            True(loaded.UnloadRequested); False(Directory.Exists(loaded.SnapshotDirectory));
            await Throws<ExtensionRegistrationException>(() => registry.DispatchObservationsAsync(captured, "fixture.one.notice", JsonData.EmptyObject).AsTask());
            fixtures.ReleaseGates();
            var weak = await LoadAndRelease(fixtures);
            for (var attempt = 0; attempt < 20 && weak.IsAlive; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
            False(weak.IsAlive, "A cooperative published context must collect after all harness/host roots are released.");
            await RetainedCleanupFailure(fixtures);
        }
        finally { fixtures.ReleaseGates(); }
    }

    private static async Task QuiescenceSettlement()
    {
        RequireQualifiedProfile();
        using var fixtures = await Fixtures.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(fixtures.Options);
        var entered = fixtures.AddGate("fixture.one.observe-entered");
        var release = fixtures.AddGate("fixture.one.observe-release");
        await using var loaded = await Load(loader, fixtures.One, registry);
        var captured = registry.CaptureSnapshot();
        var dispatch = registry.DispatchObservationsAsync(captured, "fixture.one.notice", JsonData.EmptyObject).AsTask();
        Task<RegistrationQuiescenceLease>? pause = null;
        try
        {
            await Reach(entered.Task, dispatch);
            pause = loaded.QuiesceAsync().AsTask();
            False(pause.IsCompleted); False(loaded.UnloadRequested);
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
            True(Directory.Exists(loaded.SnapshotDirectory));
            await Fails(Load(loader, fixtures.One, registry), PluginLoadFailure.DuplicateOwner);
            Equal(1, fixtures.Count("PublishedFixture.One", "constructor"));
            release.TrySetResult(); await dispatch;
            using (var lease = await pause)
            {
                Equal(loaded.OwnerGeneration, lease.OwnerGeneration);
                Equal(0, fixtures.Count("PublishedFixture.One", "dispose"));
                False(loaded.UnloadRequested); True(Directory.Exists(loaded.SnapshotDirectory));
            }
            Equal(2, registry.CaptureSnapshot().Registrations.Length);
            Equal("one", (await registry.InvokeToolAsync(registry.CaptureSnapshot(), "fixture.one.value", JsonData.EmptyObject))
                .Value.GetProperty("version").GetString());
            using var stopping = await loaded.QuiesceAsync();
            await loaded.DisposeAsync();
            stopping.Dispose();
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
            Equal(1, fixtures.Count("PublishedFixture.One", "dispose"));
            True(loaded.UnloadRequested); False(Directory.Exists(loaded.SnapshotDirectory));
            var inactive = await Throws<PluginLoadException>(() => loaded.QuiesceAsync().AsTask());
            Equal(PluginLoadFailure.InactiveLoader, inactive.Failure); Equal("quiesce", inactive.Operation);
        }
        finally
        {
            fixtures.ReleaseGates();
            try { await dispatch; }
            finally { if (pause is not null) (await pause).Dispose(); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadAndRelease(Fixtures fixtures)
    {
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(fixtures.Options);
        var loaded = await Load(loader, fixtures.Two, registry);
        await registry.InvokeToolAsync(registry.CaptureSnapshot(), "fixture.two.value", JsonData.EmptyObject);
        var weak = loaded.LoadContextReference!;
        await loaded.DisposeAsync();
        return weak;
    }

    private static async Task RetainedCleanupFailure(Fixtures fixtures)
    {
        await using var registry = new ExtensionRegistry();
        var loader = new PluginAssemblyLoader(fixtures.Options with { MaximumOwnedPackages = 1 });
        var expectedCleanupFailure = false;
        try
        {
            var loaded = await Load(loader, fixtures.Two, registry);
            var unexpected = Path.Combine(loaded.SnapshotDirectory!, "unexpected-public-fixture.txt");
            await File.WriteAllTextAsync(unexpected, "An unowned child must not be recursively erased.");
            var failure = await Throws<PluginLoadException>(() => loaded.DisposeAsync().AsTask());
            expectedCleanupFailure = true;
            Equal(PluginLoadFailure.CleanupFailed, failure.Failure); True(failure.RestartMayBeRequired);
            True(File.Exists(unexpected)); True(loaded.UnloadRequested);
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
            await Fails(Load(loader, fixtures.Two, registry), PluginLoadFailure.DuplicateOwner);
            await Fails(Load(loader, fixtures.One, registry), PluginLoadFailure.LimitExceeded);
            // Actual streams are closed even though conservative ownership/allowance remains reserved.
            using (File.Open(unexpected, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Equal(2, fixtures.Count("PublishedFixture.Two", "dispose"));
        }
        finally
        {
            var first = loader.DisposeAsync().AsTask(); var second = loader.DisposeAsync().AsTask();
            True(ReferenceEquals(first, second));
            if (expectedCleanupFailure)
            { await Fails(first, PluginLoadFailure.CleanupFailed); await Fails(second, PluginLoadFailure.CleanupFailed); }
            else { await first; await second; }
        }
    }

    private static Task<LoadedExtension> Load(PluginAssemblyLoader loader, Package package, ExtensionRegistry registry,
        PluginExecutionDecision? execution = null, CancellationToken token = default) =>
        loader.LoadAsync(package.Metadata, package.Root, ExtensionSourceScope.Explicit, "approved-fixture-suite", package.Inspection,
            execution ?? package.Execution, registry, token);
    private static async Task Reach(Task gate, Task operation)
    { await Task.WhenAny(gate, operation).WaitAsync(TimeSpan.FromSeconds(10)); if (!gate.IsCompleted) { await operation; throw new InvalidOperationException("The published operation did not reach its gate."); } await gate; }
    private static async Task Fails(Task task, PluginLoadFailure failure)
    { Equal(failure, (await Throws<PluginLoadException>(() => task)).Failure); }
    private static async Task ResolutionFailure(Func<Task> action, PluginLoadFailure failure)
    {
        var error = await Throws<Exception>(action);
        for (Exception? candidate = error; candidate is not null; candidate = candidate.InnerException)
            if (candidate is PluginLoadException diagnostic) { Equal(failure, diagnostic.Failure); return; }
        throw new InvalidOperationException("The resolver did not return its bounded missing/unsupported dependency diagnostic.");
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static T ThrowsSync<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void True(bool value, string message = "Expected true.") { if (!value) throw new InvalidOperationException(message); }
    private static void False(bool value, string message = "Expected false.") => True(!value, message);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }

    private sealed class ExistingOwner : PiSharp.Extensions.IPiSharpExtension
    {
        public ValueTask InitializeAsync(PiSharp.Extensions.IExtensionRegistry registry, CancellationToken cancellationToken)
        {
            registry.RegisterTool(new("host-existing", "host-existing", "Existing owner stays active", JsonData.EmptyObject,
                (input, _, _) => ValueTask.FromResult(input)));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Package(string root, string id, string assembly)
    {
        internal string Root { get; } = root;
        internal string EntryPath => Path.Combine(Root, assembly);
        internal string RuntimePath => EntryPath[..^4] + ".runtimeconfig.json";
        internal string DepsPath => EntryPath[..^4] + ".deps.json";
        internal JsonData Metadata { get; private set; } = JsonData.EmptyObject;
        private ExtensionManifestReadResult Read => new ExtensionManifestReader().Read(Metadata);
        internal ExtensionTrustDecision Inspection => new(ExtensionTrustDisposition.ApproveMetadataInspection, Root,
            Read.ManifestValueSha256!, ExtensionSourceScope.Explicit, "approved-fixture-suite", "experimental-policy-0");
        internal PluginExecutionDecision Execution => new(PluginExecutionDisposition.ApprovePublishedFixtureExecution, Root,
            Read.ManifestValueSha256!, Read.Manifest!.ArtifactHashes, ExtensionSourceScope.Explicit, "approved-fixture-suite", "experimental-policy-0", 1);
        internal async Task RefreshAsync(string[]? requiredFeatures = null)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                hashes.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))).ToLowerInvariant());
            Metadata = JsonData.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 0, id, packageVersion = "0.0.1", hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" },
                runtimeKind = "native", assembly, entryType = "PublishedFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = requiredFeatures ?? ["owned-descriptor-callbacks"], declaredCapabilities = new[] { "tools", "observations" },
                resourcePaths = hashes.Keys.Where(path => path != assembly).ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes
            }));
        }
    }

    private sealed class Fixtures : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "PiSharp-Published-" + Guid.NewGuid().ToString("N"));
        private readonly string? previousMarkers = Environment.GetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS");
        private readonly object? previousGates = AppContext.GetData("PiSharp.PublishedFixture.Gates");
        private readonly Dictionary<string, TaskCompletionSource> gates = new(StringComparer.Ordinal);
        internal string Snapshots => Path.Combine(root, "snapshots");
        private string Markers => Path.Combine(root, "markers");
        internal PluginAssemblyLoaderOptions Options => new() { SnapshotParentDirectory = Snapshots };
        internal Package One { get; private set; } = null!;
        internal Package Two { get; private set; } = null!;
        internal Package Fail { get; private set; } = null!;
        internal Package Future { get; private set; } = null!;
        internal int MarkerCount => Directory.GetFiles(Markers).Sum(path => File.ReadAllLines(path).Length);
        internal int Count(string assembly, string stage)
        { var path = Path.Combine(Markers, assembly + ".markers"); return File.Exists(path) ? File.ReadAllLines(path).Count(line => line == stage) : 0; }
        internal TaskCompletionSource AddGate(string key) { var value = Gate(); gates[key] = value; return value; }
        internal void ReleaseGates() { foreach (var value in gates.Values) value.TrySetResult(); gates.Clear(); }
        internal static async Task<Fixtures> CreateAsync()
        {
            var fixtures = new Fixtures();
            Directory.CreateDirectory(fixtures.Snapshots); Directory.CreateDirectory(fixtures.Markers);
            Environment.SetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS", fixtures.Markers);
            AppContext.SetData("PiSharp.PublishedFixture.Gates", fixtures.gates);
            try
            {
                var source = Environment.GetEnvironmentVariable("PISHARP_PUBLISHED_EXTENSION_FIXTURES");
                if (source is null)
                {
                    var directory = new DirectoryInfo(AppContext.BaseDirectory);
                    while (!File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent ?? throw new InvalidOperationException("Cannot locate published fixture outputs.");
                    source = Path.Combine(directory.FullName, "artifacts", "extensions", "published-fixtures");
                }
                fixtures.One = await fixtures.CopyAsync(source, "one", "PublishedFixture.One.dll");
                fixtures.Two = await fixtures.CopyAsync(source, "two", "PublishedFixture.Two.dll");
                fixtures.Fail = await fixtures.CopyAsync(source, "fail", "PublishedFixture.Fail.dll");
                fixtures.Future = await fixtures.CopyAsync(source, "future", "PublishedFixture.Future.dll");
                return fixtures;
            }
            catch { fixtures.Dispose(); throw; }
        }
        private async Task<Package> CopyAsync(string sourceRoot, string name, string assembly)
        {
            var source = Path.Combine(sourceRoot, name);
            if (!File.Exists(Path.Combine(source, assembly))) throw new InvalidOperationException("Parent must publish approved fixture project: " + name + ".");
            var target = Path.Combine(root, name); Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            False(File.Exists(Path.Combine(target, "PiSharp.Extensions.Abstractions.dll")), "Published fixtures must exclude the host ABI runtime copy.");
            False(File.Exists(Path.Combine(target, "PiSharp.Contracts.dll")), "Published fixtures must exclude the host payload contract runtime copy.");
            var package = new Package(target, "fixture." + name, assembly); await package.RefreshAsync(); return package;
        }
        public void Dispose()
        {
            ReleaseGates();
            Environment.SetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS", previousMarkers);
            AppContext.SetData("PiSharp.PublishedFixture.Gates", previousGates);
            var full = Path.GetFullPath(root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temporary, StringComparison.Ordinal) || !Path.GetFileName(full).StartsWith("PiSharp-Published-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup is outside its owned temporary directory.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
