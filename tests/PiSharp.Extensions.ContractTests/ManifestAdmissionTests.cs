using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime.Discovery;

internal static class ManifestAdmissionTests
{
    internal static string? LinkFixtureGap { get; private set; }
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("manifest preflight hashes real bytes without loading or constructing code", RealBytesAndNonexecution),
        ("matching hashes require explicit root/manifest/scope/policy-bound trust", ExplicitTrustBinding),
        ("unsupported API, runtime, TFM, RID, feature, capability and override reject before reads", CompatibilityAdmission),
        ("manifest retained syntax, Unicode, shape and inclusive character/byte budgets", StrictJsonAndBudgets),
        ("package paths reject traversal, devices, UNC, ADS, missing and reparse artifacts", PackagePaths),
        ("cancellation and read faults join actual stream cleanup before settlement", CancellationAndCleanup),
        ("caller cancellation during opened and held closing joins cleanup without publishing admission", CancellationAlone),
        ("noncanceled closing faults retain sanitized denial and unexpected exception policy", CleanupFailureAlone),
        ("caller cancellation wins over closing faults only after owned cleanup settles", CancellationPrecedesClosingFailure)
    ];

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task RealBytesAndNonexecution()
    {
        using var package = await Package.CreateAsync();
        var reader = new ExtensionManifestReader();
        var parsed = reader.Read(package.Metadata);
        True(parsed.IsValid);
        var manifest = parsed.Manifest ?? throw new InvalidOperationException("The public fixture has no parsed manifest.");
        True(ReferenceEquals(package.Metadata, manifest.Metadata));
        Equal("0.0.1", manifest.PackageVersion);
        Equal("Fixtures.ForbiddenConstructionWitness", manifest.EntryType);
        var result = await Inspect(reader, package, Approve(reader, package));
        if (!OperatingSystem.IsWindows()) { Rejected(result, ExtensionManifestFailure.UnsupportedFilesystemPlatform); return; }
        True(result.MetadataPreflightPassed);
        False(result.IsLoadAuthorized);
        True(result.RequiredBeforeLoading.Contains("atomic-path-and-open-handle-identity"));
        Equal(2, result.VerifiedArtifacts.Length);
        Equal(package.TotalBytes, result.VerifiedArtifacts.Sum(item => item.Bytes));
        Equal(package.PluginHash, result.VerifiedArtifacts[0].Sha256);
        Equal(Path.Combine(package.Root, "Plugin.dll"), result.VerifiedArtifacts[0].FullPath);
        Equal(0, ForbiddenConstructionWitness.Calls);
        False(File.Exists(Path.Combine(package.Root, "executed.marker")));
        // The .dll deliberately contains public non-PE text. Success cannot imply entrypoint resolution/loading.
        await File.WriteAllBytesAsync(Path.Combine(package.Root, "Plugin.dll"), Encoding.UTF8.GetBytes("tampered public artifact"));
        var tampered = await Inspect(reader, package, Approve(reader, package));
        Rejected(tampered, ExtensionManifestFailure.IntegrityMismatch);
        Equal(0, tampered.VerifiedArtifacts.Length);
        Equal(0, ForbiddenConstructionWitness.Calls);
    }

    private static async Task ExplicitTrustBinding()
    {
        using var package = await Package.CreateAsync();
        var opened = 0;
        var reader = new ExtensionManifestReader(observer: (stage, _, _) =>
        { if (stage == ExtensionArtifactReadStage.Opened) opened++; return ValueTask.CompletedTask; });
        Rejected(await Inspect(reader, package, null), ExtensionManifestFailure.TrustRequired);
        var approved = Approve(reader, package);
        Rejected(await Inspect(reader, package, approved with { Disposition = ExtensionTrustDisposition.Deny }), ExtensionManifestFailure.TrustDenied);
        foreach (var changed in new[]
        {
            approved with { CanonicalPackageRoot = package.Root + "-different" },
            approved with { CanonicalPackageRoot = package.Root.ToUpperInvariant() },
            approved with { ManifestValueSha256 = new string('0', 64) },
            approved with { SourceScope = ExtensionSourceScope.User },
            approved with { EffectiveScopeId = "other-project" },
            approved with { PolicyRevision = "obsolete-policy" }
        }) Rejected(await Inspect(reader, package, changed), ExtensionManifestFailure.TrustBindingMismatch);
        var permissionChange = Change(package.Metadata, "declaredCapabilities", "[\"commands\"]");
        Rejected(await reader.InspectAsync(permissionChange, package.Root, ExtensionSourceScope.Project, "fixture-project", approved),
            ExtensionManifestFailure.TrustBindingMismatch);
        Equal(0, opened);
        var alias = await reader.InspectAsync(package.Metadata, package.Root + Path.DirectorySeparatorChar, ExtensionSourceScope.Project, "fixture-project", approved);
        if (!OperatingSystem.IsWindows()) { Rejected(alias, ExtensionManifestFailure.UnsupportedFilesystemPlatform); return; }
        True(alias.MetadataPreflightPassed);
        Equal(package.Root, alias.CanonicalPackageRoot);
        Equal(2, opened);
        False(alias.IsLoadAuthorized);
    }

    private static async Task CompatibilityAdmission()
    {
        using var package = await Package.CreateAsync();
        var opened = 0;
        var reader = new ExtensionManifestReader(observer: (stage, _, _) =>
        { if (stage == ExtensionArtifactReadStage.Opened) opened++; return ValueTask.CompletedTask; });
        foreach (var probe in new (string Field, string Value, ExtensionManifestFailure Failure)[]
        {
            ("schemaVersion", "1", ExtensionManifestFailure.UnsupportedSchema),
            ("runtimeKind", "\"node\"", ExtensionManifestFailure.UnsupportedRuntimeKind),
            ("tfm", "\"net9.0\"", ExtensionManifestFailure.UnsupportedTfm),
            ("hostApiRange", "{\"minimum\":\"0.1.0\",\"maximumExclusive\":\"0.2.0\"}", ExtensionManifestFailure.UnsupportedHostApi),
            ("rids", "[\"moon-x64\"]", ExtensionManifestFailure.UnsupportedRid),
            ("rids", "[\"linux-x64\"]", ExtensionManifestFailure.UnsupportedRid),
            ("requiredFeatures", "[\"nested-tools\"]", ExtensionManifestFailure.UnsupportedFeature),
            ("declaredCapabilities", "[\"providers\"]", ExtensionManifestFailure.UnsupportedCapability),
            ("explicitOverrides", "[{\"kind\":\"tool\",\"target\":\"read\"}]", ExtensionManifestFailure.UnsupportedOverride)
        }) Rejected(await reader.InspectAsync(Change(package.Metadata, probe.Field, probe.Value), package.Root,
            ExtensionSourceScope.Project, "fixture-project", null), probe.Failure);
        Equal(0, opened);
        Equal(0, ForbiddenConstructionWitness.Calls);
    }

    private static async Task StrictJsonAndBudgets()
    {
        using var package = await Package.CreateAsync();
        var raw = package.Metadata.ToString();
        var reader = new ExtensionManifestReader();
        foreach (var permissive in new[]
        {
            raw.Insert(1, "/* retained root comment */"), raw[..^1] + ",}",
            raw.Replace("[\"win-x64\"]", "[\"win-x64\",]", StringComparison.Ordinal),
            raw.Replace("\"hostApiRange\":{", "\"hostApiRange\":{/* nested retained comment */", StringComparison.Ordinal)
        })
        {
            using var document = JsonDocument.Parse(permissive, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var retained = JsonData.FromElement(document.RootElement);
            var owned = retained.ToString();
            Equal(ExtensionManifestFailure.InvalidJson, reader.Read(retained).Diagnostics.Single().Failure);
            Equal(owned, retained.ToString());
        }
        using var malformedUnicode = JsonDocument.Parse(raw.Replace("\"id\":\"example.plugin\"", "\"id\":\"\\ud800\"", StringComparison.Ordinal));
        Equal(ExtensionManifestFailure.InvalidJson, reader.Read(JsonData.FromElement(malformedUnicode.RootElement)).Diagnostics.Single().Failure);
        Equal(ExtensionManifestFailure.InvalidManifest, reader.Read(Change(package.Metadata, "id", "null")).Diagnostics.Single().Failure);
        Equal(ExtensionManifestFailure.InvalidManifest, reader.Read(Change(package.Metadata, "requiredFeatures",
            "[\"transactional-registration\",\"transactional-registration\"]")).Diagnostics.Single().Failure);
        Equal(ExtensionManifestFailure.InvalidManifest, reader.Read(Add(package.Metadata, "unknown", "true")).Diagnostics.Single().Failure);
        Equal(ExtensionManifestFailure.InvalidJson, reader.Read(Add(package.Metadata, "unknown", "1e400")).Diagnostics.Single().Failure);
        var deep = "0";
        for (var index = 0; index < 9; index++) deep = "[" + deep + "]";
        Equal(ExtensionManifestFailure.InvalidJson, reader.Read(Add(package.Metadata, "unknown", deep)).Diagnostics.Single().Failure);
        using var duplicate = JsonDocument.Parse("{\"id\":\"one\",\"id\":\"two\"}");
        Throws<JsonException>(() => JsonData.FromElement(duplicate.RootElement));
        True(new ExtensionManifestReader(new() { MaximumManifestCharacters = raw.Length }).Read(package.Metadata).IsValid);
        Equal(ExtensionManifestFailure.ManifestTooLarge,
            new ExtensionManifestReader(new() { MaximumManifestCharacters = raw.Length - 1 }).Read(package.Metadata).Diagnostics.Single().Failure);

        var exact = new ExtensionManifestReader(new()
        { MaximumArtifactBytes = package.LargestArtifactBytes, MaximumTotalArtifactBytes = package.TotalBytes });
        var exactResult = await Inspect(exact, package, Approve(exact, package));
        if (!OperatingSystem.IsWindows()) { Rejected(exactResult, ExtensionManifestFailure.UnsupportedFilesystemPlatform); return; }
        True(exactResult.MetadataPreflightPassed);
        var tooSmall = new ExtensionManifestReader(new() { MaximumArtifactBytes = package.LargestArtifactBytes - 1 });
        Rejected(await Inspect(tooSmall, package, Approve(tooSmall, package)), ExtensionManifestFailure.ArtifactTooLarge);
        var totalSmall = new ExtensionManifestReader(new() { MaximumTotalArtifactBytes = package.TotalBytes - 1 });
        Rejected(await Inspect(totalSmall, package, Approve(totalSmall, package)), ExtensionManifestFailure.ArtifactBudgetExceeded);
        var oneArtifact = new ExtensionManifestReader(new() { MaximumArtifacts = 1 });
        Equal(ExtensionManifestFailure.ArtifactCountExceeded, oneArtifact.Read(package.Metadata).Diagnostics.Single().Failure);
    }

    private static async Task PackagePaths()
    {
        using var package = await Package.CreateAsync();
        var reader = new ExtensionManifestReader();
        foreach (var path in new[]
        {
            "../outside.dll", "C:/outside.dll", "/outside.dll", "//server/outside.dll", "folder\\outside.dll",
            "NUL.dll", "COM1.dll", "COM\u00b9.dll", "Plugin.dll:stream", "folder/./p.dll", "folder//p.dll", "p.dll ", "p.dll."
        }) Equal(ExtensionManifestFailure.InvalidArtifactPath,
            reader.Read(Change(package.Metadata, "assembly", JsonSerializer.Serialize(path))).Diagnostics.Single().Failure);
        foreach (var root in new[] { "relative/package", "\\\\server\\share\\package", "\\\\?\\C:\\package", package.Root + ":stream", package.Root + "/../outside" })
            Rejected(await reader.InspectAsync(package.Metadata, root, ExtensionSourceScope.Project, "fixture-project", null), ExtensionManifestFailure.InvalidPackageRoot);
        var mismatch = Change(package.Metadata, "resourcePaths", "[\"undeclared.json\"]");
        Equal(ExtensionManifestFailure.InvalidManifest, reader.Read(mismatch).Diagnostics.Single().Failure);
        var aliases = Change(package.Metadata, "artifactHashes", "{\"Plugin.dll\":\"" + package.PluginHash +
            "\",\"plugin.dll\":\"" + package.PluginHash + "\",\"data.json\":\"" + package.DataHash + "\"}");
        Equal(ExtensionManifestFailure.InvalidArtifactPath, reader.Read(aliases).Diagnostics.Single().Failure);
        if (!OperatingSystem.IsWindows()) { LinkFixtureGap = "non-Windows filesystem admission remains unsupported"; return; }
        var plugin = Path.Combine(package.Root, "Plugin.dll");
        File.Delete(plugin);
        Rejected(await Inspect(reader, package, Approve(reader, package)), ExtensionManifestFailure.MissingArtifact);
        await File.WriteAllBytesAsync(plugin, package.PluginBytes);
        var target = Path.Combine(package.Root, "real.dll");
        File.Move(plugin, target);
        try { File.CreateSymbolicLink(plugin, target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            LinkFixtureGap = "symbolic-link fixture creation unavailable; reparse qualification remains mandatory";
            File.Move(target, plugin);
            return;
        }
        Rejected(await Inspect(reader, package, Approve(reader, package)), ExtensionManifestFailure.ReparsePoint);
    }

    private static async Task CancellationAndCleanup()
    {
        using var package = await Package.CreateAsync();
        var opened = Gate();
        var closing = Gate();
        var cleanupRelease = Gate();
        var starts = 0;
        var closes = 0;
        var reader = new ExtensionManifestReader(observer: async (stage, _, token) =>
        {
            if (stage == ExtensionArtifactReadStage.Opened)
            {
                starts++;
                opened.SetResult();
                await Gate().Task.WaitAsync(token);
            }
            else
            {
                closes++;
                False(token.CanBeCanceled, "Cleanup observation is not abandoned with the operation token.");
                closing.SetResult();
                await cleanupRelease.Task;
            }
        });
        using var cancellation = new CancellationTokenSource();
        var running = Inspect(reader, package, Approve(reader, package), cancellation.Token).AsTask();
        if (!OperatingSystem.IsWindows()) { Rejected(await running, ExtensionManifestFailure.UnsupportedFilesystemPlatform); return; }
        await Task.WhenAny(opened.Task, running);
        if (running.IsCompleted) throw new InvalidOperationException("The real read did not reach its owned open gate: " + (await running).Diagnostics.Single().Failure);
        try
        {
            await opened.Task;
            cancellation.Cancel();
            await closing.Task;
            False(running.IsCompleted, "Cancellation must join the closing observation and owned FileStream disposal.");
            Throws<IOException>(() => { using var exclusive = File.Open(Path.Combine(package.Root, "Plugin.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        }
        finally
        {
            cancellation.Cancel();
            cleanupRelease.TrySetResult();
            try { await running; } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        await ThrowsAsync<OperationCanceledException>(() => running);
        Equal(1, starts);
        Equal(1, closes);
        using (File.Open(Path.Combine(package.Root, "Plugin.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        await ThrowsAsync<OperationCanceledException>(() => Inspect(reader, package, Approve(reader, package), cancellation.Token).AsTask());
        Equal(1, starts);

        var faultClosing = false;
        var faulty = new ExtensionManifestReader(observer: (stage, _, _) =>
        {
            if (stage == ExtensionArtifactReadStage.Opened) throw new IOException("authored public read observer failure");
            faultClosing = true;
            return ValueTask.CompletedTask;
        });
        Rejected(await Inspect(faulty, package, Approve(faulty, package)), ExtensionManifestFailure.ArtifactReadFailed);
        True(faultClosing);
        using (File.Open(Path.Combine(package.Root, "Plugin.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Equal(0, ForbiddenConstructionWitness.Calls);
    }

    private enum InspectionCancellationPoint { None, Opened, Closing }

    private static async Task CancellationAlone()
    {
        await InspectCleanupComposition(InspectionCancellationPoint.Opened, closingFailure: null);
        await InspectCleanupComposition(InspectionCancellationPoint.Closing, closingFailure: null);
    }

    private static async Task CleanupFailureAlone()
    {
        await InspectCleanupComposition(InspectionCancellationPoint.None, new IOException("private-owned-close-fault"));
        await InspectCleanupComposition(InspectionCancellationPoint.None, new InvalidOperationException("private-unexpected-close-fault"));
    }

    private static async Task CancellationPrecedesClosingFailure()
    {
        await InspectCleanupComposition(InspectionCancellationPoint.Opened, new IOException("private-owned-close-fault"));
        await InspectCleanupComposition(InspectionCancellationPoint.Closing, new IOException("private-owned-close-fault"));
    }

    private static async Task InspectCleanupComposition(InspectionCancellationPoint cancellationPoint, Exception? closingFailure)
    {
        using var package = await Package.CreateAsync();
        var pluginPath = Path.Combine(package.Root, "Plugin.dll");
        var dataPath = Path.Combine(package.Root, "data.json");
        var originalPlugin = await File.ReadAllBytesAsync(pluginPath);
        var originalData = await File.ReadAllBytesAsync(dataPath);
        var originalMetadata = package.Metadata.ToString();
        using var cancellation = new CancellationTokenSource();
        var opened = Gate();
        var closing = Gate();
        var cleanupRelease = Gate();
        var starts = 0;
        var closes = 0;
        var observedCallerToken = false;
        var observedNoncancelableCleanup = false;
        ExtensionManifestAdmission? published = null;
        var reader = new ExtensionManifestReader(observer: async (stage, _, token) =>
        {
            if (stage == ExtensionArtifactReadStage.Opened)
            {
                starts++;
                observedCallerToken = token == cancellation.Token;
                opened.TrySetResult();
                if (cancellationPoint == InspectionCancellationPoint.Opened) await Gate().Task.WaitAsync(token);
            }
            else
            {
                closes++;
                observedNoncancelableCleanup = token == CancellationToken.None;
                closing.TrySetResult();
                await cleanupRelease.Task;
                if (closingFailure is not null) throw closingFailure;
            }
        });
        async Task<ExtensionManifestAdmission> RunAsync()
        {
            var result = await Inspect(reader, package, Approve(reader, package), cancellation.Token);
            published = result;
            return result;
        }
        var running = RunAsync();
        if (!OperatingSystem.IsWindows()) { Rejected(await running, ExtensionManifestFailure.UnsupportedFilesystemPlatform); return; }
        try
        {
            await Reached(opened.Task, running);
            if (cancellationPoint == InspectionCancellationPoint.Opened) cancellation.Cancel();
            await Reached(closing.Task, running);
            if (cancellationPoint == InspectionCancellationPoint.Closing) cancellation.Cancel();
            Equal(1, starts);
            Equal(1, closes);
            True(observedCallerToken);
            True(observedNoncancelableCleanup);
            False(running.IsCompleted, "Inspection must remain pending while its owned closing callback is held.");
            True(published is null, "No admission may be published before owned cleanup settles.");
            Throws<IOException>(() => { using var exclusive = File.Open(pluginPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            cleanupRelease.TrySetResult();
            if (cancellationPoint != InspectionCancellationPoint.None)
            {
                var error = await ThrowsAsync<OperationCanceledException>(() => running);
                True(cancellation.IsCancellationRequested);
                Equal(cancellation.Token, error.CancellationToken);
                True(error.InnerException is null, "Caller cancellation must not carry a private cleanup exception.");
                False(error.ToString().Contains("private-", StringComparison.Ordinal));
                False(error.ToString().Contains(package.Root, StringComparison.Ordinal));
                True(published is null, "Canceled inspection must not return partial artifacts or load authority.");
            }
            else if (closingFailure is IOException)
            {
                var denied = await running;
                False(cancellation.IsCancellationRequested);
                Rejected(denied, ExtensionManifestFailure.ArtifactReadFailed);
                False(denied.Diagnostics.Single().Message.Contains("private-", StringComparison.Ordinal));
                False(denied.Diagnostics.Single().Message.Contains(package.Root, StringComparison.Ordinal));
            }
            else
            {
                var error = await ThrowsAsync<InvalidOperationException>(() => running);
                False(cancellation.IsCancellationRequested);
                True(ReferenceEquals(closingFailure, error), "An unexpected noncanceled host fault keeps its existing propagation policy.");
                True(published is null);
            }
            Equal(1, starts);
            Equal(1, closes);
            using (File.Open(pluginPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using (File.Open(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            var finalPlugin = await File.ReadAllBytesAsync(pluginPath);
            var finalData = await File.ReadAllBytesAsync(dataPath);
            True(originalPlugin.SequenceEqual(finalPlugin));
            True(originalData.SequenceEqual(finalData));
            Equal(originalMetadata, package.Metadata.ToString());
            Equal(0, ForbiddenConstructionWitness.Calls);
            False(File.Exists(Path.Combine(package.Root, "executed.marker")));
        }
        finally
        {
            cancellation.Cancel();
            cleanupRelease.TrySetResult();
            // Even a failed assertion releases and joins the actual owned read before fixture removal.
            try { await running; } catch (Exception) { }
        }
    }

    private static async Task Reached(Task gate, Task running)
    {
        await Task.WhenAny(gate, running).WaitAsync(TimeSpan.FromSeconds(10));
        if (!gate.IsCompleted)
        {
            await running;
            throw new InvalidOperationException("The real artifact read settled before its required ownership gate.");
        }
        await gate;
    }

    private static ValueTask<ExtensionManifestAdmission> Inspect(ExtensionManifestReader reader, Package package,
        ExtensionTrustDecision? decision, CancellationToken token = default) =>
        reader.InspectAsync(package.Metadata, package.Root, ExtensionSourceScope.Project, "fixture-project", decision, token);
    private static ExtensionTrustDecision Approve(ExtensionManifestReader reader, Package package) =>
        new(ExtensionTrustDisposition.ApproveMetadataInspection, package.Root, reader.Read(package.Metadata).ManifestValueSha256!,
            ExtensionSourceScope.Project, "fixture-project", "experimental-policy-0");
    private static JsonData Change(JsonData source, string field, string replacement) => Rewrite(source, field, JsonData.Parse(replacement), add: false);
    private static JsonData Add(JsonData source, string field, string replacement) => Rewrite(source, field, JsonData.Parse(replacement), add: true);
    private static JsonData Rewrite(JsonData source, string field, JsonData replacement, bool add)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject();
            foreach (var property in source.Value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                (property.Name == field && !add ? replacement.Value : property.Value).WriteTo(writer);
            }
            if (add) { writer.WritePropertyName(field); replacement.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(memory.ToArray()));
    }
    private static void Rejected(ExtensionManifestAdmission decision, ExtensionManifestFailure failure)
    { False(decision.MetadataPreflightPassed); False(decision.IsLoadAuthorized); Equal(failure, decision.Diagnostics.Single().Failure); Equal(0, decision.VerifiedArtifacts.Length); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void True(bool value, string message = "Expected true.") { if (!value) throw new InvalidOperationException(message); }
    private static void False(bool value, string message = "Expected false.") => True(!value, message);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }

    private sealed class ForbiddenConstructionWitness
    {
        internal static int Calls;
        public ForbiddenConstructionWitness() => Interlocked.Increment(ref Calls);
    }

    private sealed class Package : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-manifest-" + Guid.NewGuid().ToString("N"));
        internal byte[] PluginBytes { get; } = Encoding.UTF8.GetBytes("public metadata fixture; these bytes cannot execute as a managed assembly");
        private byte[] DataBytes { get; } = Encoding.UTF8.GetBytes("{\"public\":true}");
        internal string PluginHash => Convert.ToHexString(SHA256.HashData(PluginBytes)).ToLowerInvariant();
        internal string DataHash => Convert.ToHexString(SHA256.HashData(DataBytes)).ToLowerInvariant();
        internal long TotalBytes => PluginBytes.LongLength + DataBytes.LongLength;
        internal long LargestArtifactBytes => Math.Max(PluginBytes.LongLength, DataBytes.LongLength);
        internal JsonData Metadata { get; private set; } = JsonData.EmptyObject;
        internal static async Task<Package> CreateAsync()
        {
            var package = new Package();
            Directory.CreateDirectory(package.Root);
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(package.Root, "Plugin.dll"), package.PluginBytes);
                await File.WriteAllBytesAsync(Path.Combine(package.Root, "data.json"), package.DataBytes);
                package.Metadata = JsonData.Parse(JsonSerializer.Serialize(new
                {
                    schemaVersion = 0, id = "example.plugin", packageVersion = "0.0.1",
                    hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native",
                    assembly = "Plugin.dll", entryType = "Fixtures.ForbiddenConstructionWitness", tfm = "net10.0", rids = new[] { "win-x64" },
                    requiredFeatures = new[] { "owned-descriptor-callbacks" }, declaredCapabilities = new[] { "tools" },
                    resourcePaths = new[] { "data.json" }, explicitOverrides = Array.Empty<object>(),
                    artifactHashes = new Dictionary<string, string> { ["Plugin.dll"] = package.PluginHash, ["data.json"] = package.DataHash }
                }));
                return package;
            }
            catch { package.Dispose(); throw; }
        }
        public void Dispose()
        {
            var root = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(root).StartsWith("PiSharp-manifest-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup target is outside its owned temporary directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
