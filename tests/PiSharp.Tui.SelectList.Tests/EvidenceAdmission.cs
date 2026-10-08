using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

// Complete central receipt only; neither legacy unscoped DLL set grants admission.
internal sealed class EvidenceAdmission
{
    private const string Scope = "selector", Project = "tests/PiSharp.Tui.SelectList.Tests/PiSharp.Tui.SelectList.Tests.csproj",
        OutputRoot = "tests/PiSharp.Tui.SelectList.Tests/bin/Release/net10.0", EntryName = "PiSharp.Tui.SelectList.Tests";
    private static readonly string[] BindingNames = ["PiSharp.Agent", "PiSharp.AI", "PiSharp.Cli", "PiSharp.CodingAgent", "PiSharp.Contracts",
        "PiSharp.Extensions.Abstractions", "PiSharp.Extensions.Agent", "PiSharp.Extensions.Runtime", "PiSharp.Rpc", "PiSharp.Sessions",
        "PiSharp.Tools", "PiSharp.Tui", "PiSharp.CodingAgent.Tests"];
    private static readonly string[] ExecutingNames = ["PiSharp.Tui", "PiSharp.Tui.SelectList.Tests"];
    internal string Candidate { get; }
    internal string Tree { get; }
    internal string ReviewRoot { get; }
    internal string ManifestSha256 { get; }
    internal string ReceiptSha256 { get; }
    internal int SourceFilesVerified { get; }
    internal object[] Assemblies { get; }
    internal object Host { get; }
    internal object Sdk { get; }

    internal EvidenceAdmission(string[] args)
    {
        if (args.Length != 6 || File.Exists(args[0]) || Directory.Exists(args[0]))
            throw new ArgumentException("Fresh report, root, manifest/hash and complete permitted receipt/hash required");
        ReviewRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1])); RejectReparse(ReviewRoot);
        using var manifest = PinnedJson(args[2], args[3]); using var receipt = PinnedJson(args[4], args[5]);
        ManifestSha256 = args[3]; ReceiptSha256 = args[5]; var source = manifest.RootElement; var proof = receipt.RootElement;
        Candidate = source.GetProperty("candidate").GetString()!; Tree = source.GetProperty("tree").GetString()!;
        if (!Hex(Candidate, 40) || !Hex(Tree, 40) || source.GetProperty("schemaVersion").GetInt32() != 1 ||
            proof.GetProperty("schemaVersion").GetInt32() != 1 || !proof.GetProperty("executionPermitted").GetBoolean() ||
            !proof.GetProperty("policyPermittedNativeBoundary").GetBoolean() || proof.GetProperty("candidate").GetString() != Candidate ||
            proof.GetProperty("tree").GetString() != Tree || proof.GetProperty("sourceManifestSha256").GetString() != ManifestSha256 ||
            !SameRoot(proof.GetProperty("reviewRoot").GetString()!) || !SameRoot(proof.GetProperty("reviewedRepositoryRoot").GetString()!))
            throw new InvalidDataException("Complete receipt does not bind permitted preparation to exact source/root");
        var pins = Pins(source.GetProperty("files")); SourceFilesVerified = pins.Count;
        ValidateSourceClosure(pins, proof.GetProperty("sourceAdmission"));
        Sdk = ValidateSdkSelection(proof, pins);
        if (!pins.ContainsKey("tools/native-companion-targets.json") || !pins.ContainsKey("PiSharp.slnx")) throw new InvalidDataException("Sealed central registration/solution required");
        using var registry = JsonDocument.Parse(File.ReadAllBytes(InsideRoot("tools/native-companion-targets.json")));
        var targets = registry.RootElement.GetProperty("targets").EnumerateArray().ToArray();
        if (targets.Length != registry.RootElement.GetProperty("requiredCount").GetInt32()) throw new InvalidDataException("Incomplete target registration");
        var target = targets.Single(row => row.GetProperty("project").GetString() == Project);
        if (target.GetProperty("entryAssembly").GetString() != OutputRoot + "/" + EntryName + ".dll" ||
            target.GetProperty("projectSha256").GetString() != pins[Project].GetProperty("sha256").GetString()) throw new InvalidDataException("Scope differs from sealed registration");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Compatibility", "Agent", "Transport", "Sessions", "Tools", "CodingAgent", "Rpc", "Extensions.ContractTests", "ExtensionHost", "Tui" })
            roots.Add(name == "Extensions.ContractTests" ? "tests/PiSharp.Extensions.ContractTests/bin/Release/net10.0" : "tests/PiSharp." + name + ".Tests/bin/Release/net10.0");
        foreach (var row in targets)
        { var entry = row.GetProperty("entryAssembly").GetString()!; InsideRoot(entry); roots.Add(entry[..entry.LastIndexOf('/')]); }
        roots.Add("src/PiSharp.Cli/bin/Release/net10.0");
        foreach (var name in new[] { "one", "two", "fail", "future", "cli", "cli-ui", "system-import", "image", "todo", "checkpoint" }) roots.Add("artifacts/extensions/published-fixtures/" + name);
        var products = Pins(proof.GetProperty("products"));
        foreach (var pair in products)
        { if (!Within(pair.Key, roots)) throw new InvalidDataException("Product outside registered roots"); VerifyFile(InsideRoot(pair.Key), pair.Value); }
        var actualProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(InsideRoot(root))) throw new InvalidDataException("Missing prepared root");
            foreach (var file in Walk(InsideRoot(root)))
                if (!actualProducts.Add(Relative(file)) || !products.TryGetValue(Relative(file), out var pin) ||
                    pin.GetProperty("relative").GetString() != Relative(file)) throw new InvalidDataException("Unlisted/aliased prepared output");
        }
        if (actualProducts.Count != products.Count) throw new InvalidDataException("Prepared output disappeared");
        ValidateAssemblyPins(proof.GetProperty("dlls"), ["PiSharp.Cli", "PiSharp.Tui", "PiSharp.CodingAgent.Tests"], "tests/PiSharp.Tui.KittyAlternate.Tests/bin/Release/net10.0", products);
        ValidateAssemblyPins(proof.GetProperty("assemblies"), BindingNames, "tests/PiSharp.Tui.Keybindings.Tests/bin/Release/net10.0", products);
        var scope = proof.GetProperty(Scope);
        if (scope.GetProperty("schemaVersion").GetInt32() != 1 || scope.GetProperty("project").GetString() != Project ||
            scope.GetProperty("entryAssembly").GetString() != OutputRoot + "/" + EntryName + ".dll") throw new InvalidDataException("Wrong selector scope");
        ValidateRestore(scope.GetProperty("lockedRestore"), proof.GetProperty("actualPreparationSteps"), pins);
        var assemblyPins = ValidateAssemblyPins(scope.GetProperty("assemblies"), ExecutingNames, OutputRoot, products);
        Assemblies = ExecutingNames.Select(name =>
        {
            var assembly = name == Assembly.GetExecutingAssembly().GetName().Name ? Assembly.GetExecutingAssembly() : Assembly.Load(name);
            var pin = assemblyPins[name]; var location = Path.GetFullPath(assembly.Location);
            if (!StringComparer.OrdinalIgnoreCase.Equals(location, InsideRoot(pin.GetProperty("relative").GetString()!))) throw new InvalidDataException("Executing DLL escaped isolated root");
            VerifyFile(location, pin); var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (version != pin.GetProperty("informationalVersion").GetString()) throw new InvalidDataException("Executing informational version differs");
            return (object)new { name, path = location, sha256 = Hash(location), informationalVersion = version };
        }).ToArray();
        var host = proof.GetProperty("host"); var hostPath = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidDataException("Missing host path"));
        if (!StringComparer.OrdinalIgnoreCase.Equals(hostPath, Path.GetFullPath(host.GetProperty("path").GetString()!))) throw new InvalidDataException("Executing host differs");
        RejectReparse(hostPath); VerifyFile(hostPath, host); Host = new { path = hostPath, sha256 = Hash(hostPath), bytes = new FileInfo(hostPath).Length };
        const string fixture = "tests/PiSharp.Tui.SelectList.Tests/fixtures/source-authority.json";
        var output = Path.Combine(AppContext.BaseDirectory, "fixtures/source-authority.json"); RejectReparse(output);
        if (!pins.ContainsKey(fixture) || Hash(InsideRoot(fixture)) != Hash(output)) throw new InvalidDataException("Output authority differs from source");
    }

    private object ValidateSdkSelection(JsonElement proof, Dictionary<string, JsonElement> source)
    {
        const string prefix = "sdk-admission:";
        if (!proof.TryGetProperty("actualPreparationSteps", out var history) || history.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(prefix + "missing-step");
        var matching = history.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object &&
            row.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == "dotnet-sdk-version").ToArray();
        if (matching.Length == 0) throw new InvalidDataException(prefix + "missing-step");
        if (matching.Length != 1) throw new InvalidDataException(prefix + "duplicate-step");
        var step = matching[0];
        if (!step.TryGetProperty("exit", out var exit) || !exit.TryGetInt32(out var code) || code != 0 ||
            !step.TryGetProperty("originalChildReturned", out var joined) || joined.ValueKind != JsonValueKind.True)
            throw new InvalidDataException(prefix + "unsettled-step");
        if (!step.TryGetProperty("log", out var log) || log.ValueKind != JsonValueKind.Object ||
            !log.TryGetProperty("path", out var producerPath) || producerPath.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(prefix + "missing-log");
        var relativeLogPath = Relative(producerPath.GetString()!);
        string logPath;
        try { logPath = InsideRoot(relativeLogPath); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // InsideRoot already checked canonical containment before inspecting attributes.
            // An absent component must not hide a reparse point in the existing path chain.
            var current = Path.GetFullPath(Path.Combine(ReviewRoot, relativeLogPath));
            while (true)
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Reparse path excluded");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                var parent = Path.GetDirectoryName(current);
                if (parent is null || parent == current) break;
                current = parent;
            }
            throw new InvalidDataException(prefix + "missing-log", error);
        }
        if (!File.Exists(logPath)) throw new InvalidDataException(prefix + "missing-log");
        RejectReparse(logPath);
        var bytes = File.ReadAllBytes(logPath);
        if (bytes.Length > 65_536 || !log.TryGetProperty("bytes", out var length) || !length.TryGetInt64(out var expectedBytes) ||
            bytes.LongLength != expectedBytes || !log.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha.GetString())
            throw new InvalidDataException(prefix + "log-pin-mismatch");
        // The original Windows preparation log may carry a UTF-16 BOM. Decode the
        // same captured, hashed bytes with BOM detection, then match the producer's Trim.
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var loggedVersion = reader.ReadToEnd().Trim();
        if (!proof.TryGetProperty("sdkVersion", out var recorded) || recorded.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(prefix + "missing-version");
        var sdkVersion = recorded.GetString()!;
        if (!string.Equals(sdkVersion, loggedVersion, StringComparison.Ordinal))
            throw new InvalidDataException(prefix + "version-mismatch");
        if (!source.TryGetValue("global.json", out var policyPin)) throw new InvalidDataException(prefix + "policy-missing");
        using var policy = PinnedJson(InsideRoot("global.json"), policyPin.GetProperty("sha256").GetString()!);
        var sdk = policy.RootElement.GetProperty("sdk"); var requiredVersion = sdk.GetProperty("version").GetString();
        if (sdk.GetProperty("rollForward").GetString() != "disable" || !Version.TryParse(requiredVersion, out var version) || version.Major != 10 ||
            !string.Equals(sdkVersion, requiredVersion, StringComparison.Ordinal))
            throw new InvalidDataException(prefix + "policy-mismatch");
        return new { sdkVersion, stepId = "dotnet-sdk-version", originalChildReturned = true, log = log.Clone(),
            actualLogPath = logPath, policyRelative = "global.json", policySha256 = policyPin.GetProperty("sha256").GetString(), rollForward = "disable" };
    }

    private void ValidateRestore(JsonElement restore, JsonElement preparation, Dictionary<string, JsonElement> source)
    {
        if (!restore.GetProperty("verified").GetBoolean() || restore.GetProperty("stepId").GetString() != "locked-offline-restore" ||
            restore.GetProperty("projectSha256").GetString() != source[Project].GetProperty("sha256").GetString()) throw new InvalidDataException("Actual restore/project association required");
        var solution = XDocument.Load(InsideRoot("PiSharp.slnx"));
        if (!solution.Descendants("Project").Any(row => ((string?)row.Attribute("Path"))?.Replace('\\', '/') == Project)) throw new InvalidDataException("Scope absent from restored solution");
        var steps = preparation.EnumerateArray().ToArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!ids.Add(step.GetProperty("id").GetString()!) || step.GetProperty("exit").GetInt32() != 0 || !step.GetProperty("originalChildReturned").GetBoolean()) throw new InvalidDataException("Failed/unsettled preparation step");
            var log = step.GetProperty("log"); VerifyFile(InsideRoot(Relative(log.GetProperty("path").GetString()!)), log);
        }
        foreach (var required in new[] { "dotnet-sdk-version", "dotnet-host-info", "locked-offline-restore", "full-solution-build" }.Concat(
            new[] { "one", "two", "fail", "future", "cli", "cli-ui", "system-import", "image", "todo", "checkpoint" }
                .SelectMany(name => new[] { "restore-fixture-" + name, "publish-fixture-" + name })))
            if (!ids.Contains(required)) throw new InvalidDataException("Incomplete actual preparation steps");
        var actual = steps.Single(step => step.GetProperty("id").GetString() == "locked-offline-restore").GetProperty("log"); var recorded = restore.GetProperty("log");
        if (recorded.GetProperty("path").GetString() != actual.GetProperty("path").GetString() || !SamePin(recorded, actual)) throw new InvalidDataException("Restore log differs from actual step");
        var lockPin = restore.GetProperty("lockFile"); var relative = Project[..(Project.LastIndexOf('/') + 1)] + "packages.lock.json";
        if (lockPin.GetProperty("relative").GetString() != relative || !SamePin(lockPin, source[relative])) throw new InvalidDataException("Scoped lock differs from source");
        VerifyFile(InsideRoot(relative), lockPin);
    }

    private Dictionary<string, JsonElement> ValidateAssemblyPins(JsonElement rows, string[] expected, string root, Dictionary<string, JsonElement> products)
    {
        var pins = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pin in rows.EnumerateArray())
        {
            var name = pin.GetProperty("name").GetString()!; var relative = root + "/" + name + ".dll";
            if (!expected.Contains(name, StringComparer.Ordinal) || !pins.TryAdd(name, pin) || pin.GetProperty("relative").GetString() != relative ||
                !products.TryGetValue(relative, out var product) || product.GetProperty("relative").GetString() != relative ||
                !SamePin(pin, product) || pin.GetProperty("informationalVersion").GetString()?.Contains(Candidate, StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidDataException("DLL is not associated with complete products");
        }
        if (pins.Count != expected.Length) throw new InvalidDataException("Exact DLL set required"); return pins;
    }

    private void ValidateSourceClosure(Dictionary<string, JsonElement> pins, JsonElement admission)
    {
        var generatedRoots = new HashSet<string>(pins.Keys.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => new[] { path[..path.LastIndexOf('/')] + "/bin", path[..path.LastIndexOf('/')] + "/obj" }), StringComparer.OrdinalIgnoreCase);
        var protectedArtifacts = new HashSet<string>(pins.Keys.Where(path => path.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase) && path.Split('/').Length >= 3)
            .Select(path => string.Join('/', path.Split('/').Take(2))), StringComparer.OrdinalIgnoreCase);
        if (admission.GetProperty("schemaVersion").GetInt32() != 1 || admission.GetProperty("policy").GetString() != "exact-checkout-source-v1" ||
            !admission.GetProperty("freshCheckoutVerified").GetBoolean() || admission.GetProperty("sourceFilesVerified").GetInt32() != pins.Count ||
            !SameSet(admission.GetProperty("generatedRoots"), generatedRoots) || !SameSet(admission.GetProperty("protectedArtifactSourceRoots"), protectedArtifacts) ||
            admission.GetProperty("evidenceRoot").GetString() != "artifacts" || pins.Keys.Any(path => Within(path, generatedRoots))) throw new InvalidDataException("Exact source/generated admission required");
        var generated = Pins(admission.GetProperty("generatedFiles")); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var generatedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, string Relative, int Kind)>(); pending.Push((ReviewRoot, "", 0));
        while (pending.TryPop(out var directory)) foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path))
        {
            RejectReparse(entry); var relative = directory.Relative.Length == 0 ? Path.GetFileName(entry) : directory.Relative + "/" + Path.GetFileName(entry);
            if (relative == ".git") continue;
            var isDirectory = Directory.Exists(entry); var kind = directory.Kind;
            if (directory.Relative.Equals("artifacts", StringComparison.OrdinalIgnoreCase) && isDirectory && !protectedArtifacts.Contains(relative)) kind = 2;
            else if (generatedRoots.Contains(relative)) kind = 1;
            if (isDirectory) { pending.Push((entry, relative, kind)); continue; }
            if (kind == 2) continue;
            var expected = kind == 1 ? generated : pins; var observed = kind == 1 ? generatedSeen : seen;
            if (!expected.TryGetValue(relative, out var pin) || pin.GetProperty("relative").GetString() != relative || !observed.Add(relative)) throw new InvalidDataException("Unlisted/aliased checkout input");
            VerifyFile(entry, pin);
        }
        if (seen.Count != pins.Count || generatedSeen.Count != generated.Count) throw new InvalidDataException("Missing source/generated input");
    }

    private Dictionary<string, JsonElement> Pins(JsonElement rows)
    {
        var pins = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in rows.EnumerateArray())
        {
            var relative = pin.GetProperty("relative").GetString()!; InsideRoot(relative);
            if (!pins.TryAdd(relative, pin) || !Hex(pin.GetProperty("sha256").GetString()!, 64) || pin.GetProperty("bytes").GetInt64() < 0 ||
                relative.Equals(".git", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid/duplicate pin");
        }
        if (pins.Count == 0) throw new InvalidDataException("Empty complete pin set"); return pins;
    }
    private static bool SameSet(JsonElement values, HashSet<string> expected)
    { var actual = values.EnumerateArray().Select(value => value.GetString()!).ToArray(); return actual.Length == expected.Count && actual.Distinct(StringComparer.OrdinalIgnoreCase).Count() == actual.Length && expected.SetEquals(actual); }
    private static bool Within(string relative, HashSet<string> roots) => roots.Any(root => relative.Equals(root, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    private static bool SamePin(JsonElement first, JsonElement second) => first.GetProperty("bytes").GetInt64() == second.GetProperty("bytes").GetInt64() && first.GetProperty("sha256").GetString() == second.GetProperty("sha256").GetString();
    private bool SameRoot(string path) => StringComparer.OrdinalIgnoreCase.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), ReviewRoot);
    private string Relative(string path) => Path.GetRelativePath(ReviewRoot, Path.GetFullPath(path)).Replace('\\', '/');
    private string InsideRoot(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(part => part is "" or "." or ".." || part.TrimEnd('.', ' ') != part)) throw new InvalidDataException("Canonical relative path required");
        var full = Path.GetFullPath(Path.Combine(ReviewRoot, relative));
        if (!full.StartsWith(ReviewRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes root"); RejectReparse(full); return full;
    }
    private static IEnumerable<string> Walk(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory)) foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        { RejectReparse(entry); if (Directory.Exists(entry)) pending.Push(entry); else yield return entry; }
    }
    private static void RejectReparse(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse path excluded");
            var parent = Path.GetDirectoryName(current); if (parent is null || parent == current) break; current = parent;
        }
    }
    internal static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool Hex(string value, int length) => value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static JsonDocument PinnedJson(string path, string expected)
    {
        RejectReparse(path); if (!Hex(expected, 64) || Hash(path) != expected) throw new InvalidDataException("Evidence pin differs");
        var document = JsonDocument.Parse(File.ReadAllBytes(path));
        try { RejectDuplicateProperties(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON evidence property"); RejectDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) RejectDuplicateProperties(child);
    }
    private static void VerifyFile(string path, JsonElement pin)
    { if (new FileInfo(path).Length != pin.GetProperty("bytes").GetInt64() || Hash(path) != pin.GetProperty("sha256").GetString()) throw new InvalidDataException("Physical pin differs: " + path); }
}
