using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PiSharp.Evals;

public sealed record CommandSpec(string Executable, string[] Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);
public sealed record CommandResult(int ExitCode, string Stdout, string Stderr, double ElapsedMs);
public interface ICommandExecutor { Task<CommandResult> ExecuteAsync(CommandSpec command, CancellationToken cancellationToken); }
public sealed class ProcessCommandExecutor : ICommandExecutor
{
    public async Task<CommandResult> ExecuteAsync(CommandSpec command, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command.Executable) { WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var item in command.Environment) start.Environment[item.Key] = item.Value;
        using var process = Process.Start(start) ?? throw new IOException("Runtime did not start.");
        process.StandardInput.Close();
        var clock = Stopwatch.StartNew();
        var stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        // Observe capture failures immediately so a noisy process cannot fill a pipe and deadlock.
        var exited = process.WaitForExitAsync(cancellationToken);
        try
        {
            // A capture can fault while the other pipe is still open. Observe each independently.
            var pending = new List<Task> { stdout, stderr, exited };
            while (pending.Count > 0) { var finished = await Task.WhenAny(pending); await finished; pending.Remove(finished); }
            await exited;
            return new(process.ExitCode, stdout.Result, stderr.Result, clock.Elapsed.TotalMilliseconds);
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + read > 16 * 1024 * 1024) throw new IOException("Runtime output exceeded 16 Mi characters.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}
public sealed record StagedRuntimeOptions(string RuntimeDirectory, string EntryPoint, string DocumentationDirectory, string ArtifactDirectory,
    string Provider, string Model, bool Live, string? DotnetExecutable = null, string? ContainerImage = null,
    string ContainerExecutable = "docker", string[]? CredentialEnvironmentNames = null, int TimeoutSeconds = 120);

/// <summary>Executes the current published PiSharp CLI. Process mode isolates configuration but is not an OS sandbox.
/// Container mode uses one immutable image/runtime mount, a fresh container and writable workspace/home/session per arm.</summary>
public sealed class StagedRuntime : IEvalRuntime
{
    private readonly StagedRuntimeOptions options;
    private readonly ICommandExecutor executor;
    private readonly IReadOnlyList<EvalCase> cases;
    private readonly string stagedRuntime;
    private readonly string stagedDocs;
    private readonly string binaryDigest;
    private readonly string docsDigest;
    public string Evidence => $"live {(options.ContainerImage is null ? "process" : "container")}; runtime={binaryDigest}; docs={docsDigest}; image={options.ContainerImage ?? "none"}; answer-check-v1";
    public StagedRuntime(StagedRuntimeOptions options, ICommandExecutor? executor = null, IReadOnlyList<EvalCase>? cases = null)
    {
        if (!options.Live) throw new ArgumentException("Executable/provider execution requires explicit --live.");
        if (string.IsNullOrWhiteSpace(options.Provider) || string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("Provider and model are required.");
        if (options.TimeoutSeconds < 1) throw new ArgumentException("Timeout must be positive.");
        if (Path.GetFileName(options.EntryPoint) != options.EntryPoint || !File.Exists(Path.Combine(options.RuntimeDirectory, options.EntryPoint)))
            throw new ArgumentException("Entry point must name an existing file inside the published runtime directory.");
        if (options.ContainerImage is not null && !System.Text.RegularExpressions.Regex.IsMatch(options.ContainerImage, @"^[^\s]+@sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Container image must be pinned by sha256 digest.");
        if (options.EntryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && options.ContainerImage is null && !File.Exists(options.DotnetExecutable))
            throw new ArgumentException("DLL process execution requires an explicit dotnet executable.");
        this.options = options with { ArtifactDirectory = Path.GetFullPath(options.ArtifactDirectory) };
        this.executor = executor ?? new ProcessCommandExecutor();
        this.cases = EvalPlan.ValidateCases(cases ?? EvalPlan.BuiltInCases);
        if (Directory.Exists(options.ArtifactDirectory) || File.Exists(options.ArtifactDirectory)) throw new ArgumentException("Artifact directory must be fresh.");
        if (Within(this.options.ArtifactDirectory, Path.GetFullPath(options.RuntimeDirectory)) ||
            Within(this.options.ArtifactDirectory, Path.GetFullPath(options.DocumentationDirectory))) throw new ArgumentException("Artifacts must be outside input trees.");
        stagedRuntime = Path.Combine(this.options.ArtifactDirectory, "runtime");
        stagedDocs = Path.Combine(this.options.ArtifactDirectory, "documentation");
        CopyTree(options.RuntimeDirectory, stagedRuntime);
        CopyTree(options.DocumentationDirectory, stagedDocs);
        binaryDigest = DigestTree(stagedRuntime); docsDigest = DigestTree(stagedDocs);
        File.WriteAllText(Path.Combine(this.options.ArtifactDirectory, "runtime-manifest.json"), EvalJson.Serialize(new { binaryDigest, docsDigest, options.EntryPoint, options.ContainerImage }));
    }
    public Task<IReadOnlyList<EvalCase>> DiscoverAsync(DocumentationArm arm, CancellationToken cancellationToken)
    {
        // Manifest discovery is independent of docs and never launches a provider. Both arms receive the same validated cohort.
        return Task.FromResult(cases);
    }
    public async Task<EvalResult> RunAsync(EvalTask task, CancellationToken cancellationToken)
    {
        if (task.Model != options.Provider + "/" + options.Model) throw new ArgumentException("Task model differs from configured runtime.");
        if (DigestTree(stagedRuntime) != binaryDigest || DigestTree(stagedDocs) != docsDigest) throw new IOException("Staged inputs changed.");
        var root = Path.Combine(options.ArtifactDirectory, "tasks", task.ArtifactId);
        if (Directory.Exists(root)) throw new IOException("Task already has artifacts; refusing session reuse.");
        var workspace = Path.Combine(root, "workspace"); var home = Path.Combine(root, "home"); var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(workspace); Directory.CreateDirectory(home); Directory.CreateDirectory(sessions);
        var agentDir = Path.Combine(home, "agent"); Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "settings.json"), "{\"enableSkills\":false}");
        if (task.Arm == DocumentationArm.with_docs) CopyTree(stagedDocs, Path.Combine(workspace, "docs"));
        var container = options.ContainerImage is not null;
        var runtimeRoot = container ? "/runtime" : stagedRuntime;
        var sessionRoot = container ? "/sessions" : sessions;
        var prompt = task.Case.Prompt + (task.Arm == DocumentationArm.with_docs ? "\nYou may read the supplied PiSharp documentation in ./docs before answering." : "\nAnswer using your existing knowledge; no PiSharp documentation is supplied.");
        var args = new List<string> { "--print", "--mode", "json", "--provider", options.Provider, "--model", options.Model,
            "--session-dir", sessionRoot, "--no-extensions", "--no-skills", "--no-prompt-templates", "--no-mcp", prompt };
        var isDll = options.EntryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var executable = isDll ? (container ? "dotnet" : options.DotnetExecutable!) : Path.Combine(runtimeRoot, options.EntryPoint);
        if (isDll) args.Insert(0, container ? "/runtime/" + options.EntryPoint : Path.Combine(stagedRuntime, options.EntryPoint));
        var env = HostEnvironment();
        foreach (var key in options.CredentialEnvironmentNames ?? [])
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0')) throw new ArgumentException("Invalid credential environment name.");
            if (key.Equals("HOME", StringComparison.OrdinalIgnoreCase) || key.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("PATH", StringComparison.OrdinalIgnoreCase) || key.StartsWith("PI_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || key.Equals("TMP", StringComparison.OrdinalIgnoreCase) || key.Equals("TEMP", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Credential environment name conflicts with isolated runtime configuration.");
            env[key] = Environment.GetEnvironmentVariable(key) ?? throw new ArgumentException($"Credential variable {key} is absent.");
        }
        if (container)
        {
            var docker = new List<string> { "run", "--rm", "--read-only", "--pull=never", "--cap-drop=ALL", "--security-opt=no-new-privileges", "--tmpfs", "/tmp:rw,exec,mode=1777", "--workdir", "/workspace" };
            Mount(stagedRuntime, "/runtime", true); Mount(workspace, "/workspace", false); Mount(home, "/home/eval", false); Mount(sessions, "/sessions", false);
            foreach (var entry in new Dictionary<string, string> { ["HOME"] = "/home/eval", ["PI_CODING_AGENT_DIR"] = "/home/eval/agent", ["DOTNET_CLI_HOME"] = "/home/eval" }) docker.AddRange(["--env", entry.Key + "=" + entry.Value]);
            foreach (var key in options.CredentialEnvironmentNames ?? []) docker.AddRange(["--env", key]);
            docker.AddRange(["--entrypoint", executable, options.ContainerImage!]); docker.AddRange(args);
            args = docker; executable = options.ContainerExecutable;
            void Mount(string source, string target, bool readOnly) { if (source.Contains(',')) throw new ArgumentException("Container mount path cannot contain commas."); docker.AddRange(["--mount", $"type=bind,source={source},target={target}" + (readOnly ? ",readonly" : "")]); }
        }
        else { env["HOME"] = home; env["USERPROFILE"] = home; env["PI_CODING_AGENT_DIR"] = agentDir; env["DOTNET_CLI_HOME"] = home; env["TMP"] = Path.Combine(home, "tmp"); env["TEMP"] = env["TMP"]; Directory.CreateDirectory(env["TMP"]); }
        File.WriteAllText(Path.Combine(root, "task.json"), EvalJson.Serialize(task));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        CommandResult execution;
        try { execution = await executor.ExecuteAsync(new(executable, args.ToArray(), workspace, env), timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Save(new(task, EvalStatus.errored, Error: "Runtime timed out.")); }
        catch (Exception error) when (error is not OperationCanceledException) { return Save(new(task, EvalStatus.errored, Error: error.Message)); }
        File.WriteAllText(Path.Combine(root, "stdout.jsonl"), execution.Stdout); File.WriteAllText(Path.Combine(root, "stderr.txt"), execution.Stderr);
        if (DigestTree(stagedRuntime) != binaryDigest || DigestTree(stagedDocs) != docsDigest) return Save(new(task, EvalStatus.errored, Error: "Staged inputs changed during execution."));
        return Save(Parse(task, execution));
        EvalResult Save(EvalResult result) { File.WriteAllText(Path.Combine(root, "result.json"), EvalJson.Serialize(result)); return result; }
    }
    public static EvalResult Parse(EvalTask task, CommandResult execution)
    {
        if (execution.ExitCode != 0) return new(task, EvalStatus.errored, Metrics: new(TotalMs: execution.ElapsedMs), Error: $"Runtime exited {execution.ExitCode}.");
        try
        {
            string? answer = null; var tools = 0; var assistantMessages = 0; var usage = new double?[6];
            var missingUsage = new bool[6]; bool failed = false;
            foreach (var line in execution.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var json = JsonDocument.Parse(line); var e = json.RootElement;
                if (!e.TryGetProperty("type", out var type)) continue;
                if (type.GetString() == "tool_execution_start") tools++;
                if (type.GetString() != "message_end" || !e.TryGetProperty("message", out var message) || !message.TryGetProperty("role", out var role) || role.GetString() != "assistant") continue;
                assistantMessages++;
                if (message.TryGetProperty("stopReason", out var reason) && reason.GetString() is "error" or "aborted") failed = true;
                answer = message.TryGetProperty("content", out var content) ? string.Concat(content.EnumerateArray().Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text").Select(c => c.GetProperty("text").GetString())) : null;
                if (message.TryGetProperty("usage", out var u))
                {
                    string[] fields = ["input", "output", "cacheRead", "cacheWrite", "totalTokens"];
                    for (var i = 0; i < fields.Length; i++)
                        if (u.TryGetProperty(fields[i], out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetDouble(out var number)) usage[i] = (usage[i] ?? 0) + number;
                        else missingUsage[i] = true;
                    if (u.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object && cost.TryGetProperty("total", out var total) && total.ValueKind != JsonValueKind.Null && total.TryGetDouble(out var dollars)) usage[5] = (usage[5] ?? 0) + dollars;
                    else missingUsage[5] = true;
                }
                else Array.Fill(missingUsage, true);
            }
            for (var i = 0; i < usage.Length; i++) if (missingUsage[i]) usage[i] = null;
            var metrics = new EvalMetrics(usage[0], usage[1], usage[2], usage[3], usage[4], assistantMessages == 0 ? null : tools, execution.ElapsedMs, usage[5]);
            if (!metrics.Valid || failed) return new(task, EvalStatus.errored, Error: "Runtime emitted invalid metrics or a failed assistant message.");
            return answer is null ? new(task, EvalStatus.unscored, Metrics: metrics) : new(task, EvalStatus.scored,
                string.Equals(answer.Trim(), task.Case.ExpectedText, StringComparison.Ordinal) ? 1 : 0, metrics);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { return new(task, EvalStatus.errored, Error: "Malformed runtime JSON: " + error.Message); }
    }
    private static Dictionary<string, string> HostEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "DOTNET_ROOT", "COMSPEC" }) if (Environment.GetEnvironmentVariable(key) is { } value) result[key] = value;
        result["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"; result["NO_COLOR"] = "1"; return result;
    }
    private static bool Within(string child, string parent) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> Files(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked input directory is not allowed.");
        foreach (var item in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked staging inputs are not allowed.");
            if ((attributes & FileAttributes.Directory) != 0) { foreach (var file in Files(item)) yield return file; }
            else yield return item;
        }
    }
    private static void CopyTree(string source, string target)
    {
        var files = Files(Path.GetFullPath(source)).ToArray(); Directory.CreateDirectory(target);
        foreach (var file in files) { var destination = Path.Combine(target, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, false); }
    }
    private static string DigestTree(string root) => EvalJson.Hash(string.Join('\n', Files(root).Select(file =>
        Path.GetRelativePath(root, file).Replace('\\', '/') + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))))));
}
