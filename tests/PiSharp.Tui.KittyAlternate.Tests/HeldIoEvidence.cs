using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Tui.Input;

internal sealed record HeldIoAdmission(bool Pass, object Boundary, string[] Errors);

internal static class HeldIoEvidence
{
    internal static HeldFilePin Pin(string file)
    { var data = File.ReadAllBytes(file); return new(Path.GetFullPath(file), data.LongLength, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()); }

    // All runtime source reads resolve immutable relative paths in the supplied review
    // checkout. Producer absolute paths are displayed only as historical provenance.
    internal static HeldIoAdmission Admit(string reviewedRoot, string manifestFile, string manifestSha,
        string buildReceiptFile, string buildReceiptSha, string originalSourceFile)
    {
        var errors = new List<string>(); var sourcePins = new List<object>(); var fixturePins = new List<object>(); var dllPins = new List<object>();
        string? root = null; HeldFilePin? manifestPin = null, buildReceiptPin = null; JsonElement? manifestIdentity = null, sourceDeclaration = null, receipt = null;
        JsonDocument ReadPinned(string file, string sha, out HeldFilePin actual)
        {
            var data = File.ReadAllBytes(file);
            actual = new(Path.GetFullPath(file), data.LongLength, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit) || !actual.sha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Supplied immutable artifact SHA differs: " + actual.path);
            return JsonDocument.Parse(data);
        }
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reviewedRoot));
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Reviewed checkout missing: " + root);
            using var manifest = ReadPinned(manifestFile, manifestSha, out var actualManifest); manifestPin = actualManifest;
            using var build = ReadPinned(buildReceiptFile, buildReceiptSha, out var actualReceipt); buildReceiptPin = actualReceipt;
            var m = manifest.RootElement; var b = build.RootElement; receipt = b.Clone();
            var candidate = m.GetProperty("candidate").GetString()!; var tree = m.GetProperty("tree").GetString()!;
            manifestIdentity = JsonSerializer.SerializeToElement(new { candidate, tree, producerRepository = m.GetProperty("repository").GetString() });
            if (b.GetProperty("schemaVersion").GetInt32() != 1 || !b.GetProperty("policyPermittedNativeBoundary").GetBoolean() ||
                b.GetProperty("candidate").GetString() != candidate || b.GetProperty("tree").GetString() != tree ||
                b.GetProperty("sourceManifestSha256").GetString() != manifestPin.sha256 ||
                !Path.TrimEndingDirectorySeparator(Path.GetFullPath(b.GetProperty("reviewedRepositoryRoot").GetString()!)).Equals(root, StringComparison.OrdinalIgnoreCase))
                errors.Add("Permitted build receipt does not associate this checkout, candidate/tree and immutable source manifest");
            var files = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var expected in m.GetProperty("files").EnumerateArray())
            {
                var relative = expected.GetProperty("relative").GetString()!;
                if (!files.TryAdd(relative.Replace('\\', '/'), expected.Clone())) { errors.Add("Duplicate manifest path: " + relative); continue; }
                CheckFile(expected, Resolve(root, relative), sourcePins, errors);
            }
            const string fixturePrefix = "tests/PiSharp.Tui.KittyAlternate.Tests/fixtures/";
            foreach (var (relative, expected) in files.Where(p => p.Key.StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase) && p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                CheckFile(expected, Path.Combine(AppContext.BaseDirectory, "fixtures", relative[fixturePrefix.Length..]), fixturePins, errors);
            CheckFile(files[fixturePrefix + "alternate-key-source-observations.json"], originalSourceFile, fixturePins, errors);
            using var declaration = JsonDocument.Parse(File.ReadAllText(Resolve(root, fixturePrefix + "held-io-source-boundary.json")));
            sourceDeclaration = declaration.RootElement.Clone();
            foreach (var expected in declaration.RootElement.GetProperty("files").EnumerateArray())
            {
                var relative = expected.GetProperty("relative").GetString()!.Replace('\\', '/');
                if (!files.TryGetValue(relative, out var sealedPin) || sealedPin.GetProperty("bytes").GetInt64() != expected.GetProperty("bytes").GetInt64() ||
                    sealedPin.GetProperty("sha256").GetString() != expected.GetProperty("sha256").GetString())
                    errors.Add("Boundary relative pin differs from immutable candidate manifest: " + relative);
            }
            var expectedDlls = b.GetProperty("dlls").EnumerateArray().ToArray();
            var assemblies = new[] { typeof(TerminalChatInput).Assembly, typeof(TerminalInputDecoder).Assembly, Assembly.GetExecutingAssembly() };
            if (expectedDlls.Length != assemblies.Length) errors.Add("Build receipt must pin all three actual host/TUI/consumer assemblies exactly once");
            foreach (var assembly in assemblies)
            {
                var name = assembly.GetName().Name; var actual = Pin(assembly.Location);
                var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                var matching = expectedDlls.Where(p => p.GetProperty("name").GetString() == name).ToArray();
                var pass = matching.Length == 1 && IsWithin(root, actual.path) &&
                    Path.GetFullPath(matching[0].GetProperty("path").GetString()!).Equals(actual.path, StringComparison.OrdinalIgnoreCase) &&
                    matching[0].GetProperty("bytes").GetInt64() == actual.bytes && matching[0].GetProperty("sha256").GetString() == actual.sha256 &&
                    matching[0].GetProperty("informationalVersion").GetString() == version;
                dllPins.Add(new { name, actual, informationalVersion = version, expected = matching.Select(p => p.Clone()).ToArray(), pass });
                if (!pass) errors.Add("Executing assembly is not the reviewed-checkout DLL pinned by the permitted build receipt: " + name);
            }
        }
        catch (Exception error) { errors.Add("Boundary admission: " + error); }
        return new(errors.Count == 0, new { reviewedRepositoryRoot = root, suppliedRepositoryRoot = reviewedRoot,
            sourceManifest = manifestPin, suppliedManifestSha256 = manifestSha, manifestIdentity, sourceDeclaration, sourcePins, fixturePins,
            buildReceipt = buildReceiptPin, suppliedBuildReceiptSha256 = buildReceiptSha, receipt, dllPins,
            producerPathsUsedForAdmission = false, scenariosStarted = 0, boundaryAdmissionPass = errors.Count == 0 }, errors.ToArray());
    }
    private static void CheckFile(JsonElement expected, string file, List<object> pins, List<string> errors)
    {
        HeldFilePin? actual = null; string? failure = null; var pass = false;
        try { actual = Pin(file); pass = actual.bytes == expected.GetProperty("bytes").GetInt64() && actual.sha256 == expected.GetProperty("sha256").GetString(); }
        catch (Exception error) { failure = error.ToString(); }
        pins.Add(new { relative = expected.GetProperty("relative").GetString(), producerPath = expected.GetProperty("path").GetString(), actual, pass, error = failure });
        if (!pass) errors.Add("Reviewed source or copied fixture pin failed: " + file + (failure is null ? "" : " " + failure));
    }
    private static string Resolve(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relative) || parts.Any(p => p.Length == 0 || p is "." or "..")) throw new InvalidOperationException("Invalid immutable relative path: " + relative);
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(root, resolved)) throw new InvalidOperationException("Manifest path leaves reviewed checkout: " + relative);
        return resolved;
    }
    private static bool IsWithin(string root, string file) => Path.GetFullPath(file).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

// Append-only receipts survive a hang at the final ownership join. A durable
// diagnostic explicitly reports incomplete ownership and never counts as a pass.
internal sealed class HeldIoJournal : IDisposable
{
    private readonly FileStream stream; private readonly StreamWriter writer; private long sequence; private bool closed;
    private readonly object sync = new();
    internal readonly List<string> Errors = [];
    internal string Path { get; }
    internal HeldIoJournal(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        stream = new(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
    }
    internal bool Record(string kind, object evidence)
    {
        lock (sync) return RecordCore(kind, evidence);
    }
    private bool RecordCore(string kind, object evidence)
    {
        var receipt = new { schemaVersion = 1, sequence = ++sequence, kind, evidence };
        try { writer.WriteLine(JsonSerializer.Serialize(receipt)); writer.Flush(); stream.Flush(flushToDisk: true); return true; }
        catch (Exception error)
        {
            Errors.Add("Diagnostic persistence failed: " + error);
            try { Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "diagnostic-persistence-failed", error = error.ToString(), receipt })); }
            catch (Exception secondary) { Errors.Add("Diagnostic fallback failed: " + secondary); }
            return false; // The caller still owns and awaits its original tasks.
        }
    }
    internal object Seal()
    {
        Dispose(); HeldFilePin? file = null;
        try { file = HeldIoEvidence.Pin(Path); } catch (Exception error) { Errors.Add("Diagnostic seal failed: " + error); }
        return new { file, errors = Errors.ToArray(), appendOnly = true, complete = closed && Errors.Count == 0 };
    }
    public void Dispose()
    {
        if (closed) return;
        try { writer.Flush(); stream.Flush(flushToDisk: true); } catch (Exception error) { Errors.Add("Diagnostic final flush failed: " + error); }
        try { writer.Dispose(); } catch (Exception error) { Errors.Add("Diagnostic writer close failed: " + error); }
        try { stream.Dispose(); } catch (Exception error) { Errors.Add("Diagnostic stream close failed: " + error); }
        closed = true;
    }
}
