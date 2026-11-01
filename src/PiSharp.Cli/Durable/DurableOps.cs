// PiSharp-only (decision 0010, M6). The operator commands that work on a device's data directory rather than through its API. `doctor`
// checks read-only (DeviceDoctor plus the device's configuration and token file), calls the listener only with --probe, and changes
// something only with --repair. `service` writes a per-user service definition that runs `pisharp durable serve` (a systemd user unit, a
// launchd agent, or a Windows scheduled task: the device needs the user's home, Pi settings and model credentials, so it runs as that
// user); writing one installs nothing, and the commands that would are printed. `backup` creates, verifies and restores quiesced copies.
// No command prints the device token: doctor reports whether it is present and strong, a backup holds it only with --include-secrets.
using System.Globalization;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Server;

namespace PiSharp.Cli.Durable;

/// <summary><c>pisharp durable attach | doctor | service | backup</c>.</summary>
internal static class DurableOps
{
    /// <summary>The commands, their usage and summary, for help.</summary>
    internal static IReadOnlyList<(string Name, string Usage, string Summary)> Commands { get; } =
    [
        ("attach", DurableAttach.Usage, "A line-mode terminal on a session's agent: transcript, questions, plan reviews, job approvals (/help lists its commands)."),
        ("doctor", "[--probe] [--verify-blobs] [--repair] [--data-dir <dir>]",
            "Checks the local device read-only; --probe also calls its listener, --repair migrates and collects (device stopped). Exit 1 when a check fails."),
        ("service", "systemd|launchd|windows [--out <dir>] [--data-dir <dir>] [--exec <path>]",
            "Writes a per-user service definition running `pisharp durable serve`. It installs nothing; it prints the commands that would."),
        ("backup create", "--out <dir> [--include-secrets] [--data-dir <dir>]",
            "Copies the stopped local device into an empty directory with a manifest of hashes (the token only with --include-secrets)."),
        ("backup verify", "<dir>", "Checks a backup's manifest, every file's hash and every database's integrity."),
        ("backup restore", "<dir> --data-dir <dir>", "Verifies a backup, then restores it into an empty data directory."),
    ];

    /// <summary>The first words of the commands.</summary>
    internal static IReadOnlySet<string> Names { get; } = new HashSet<string>(Commands.Select(command => command.Name.Split(' ')[0]), StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Help for the operator commands.</summary>
    internal static string Help(string? name = null)
    {
        var text = new StringBuilder();
        foreach (var (command, usage, summary) in Commands.Where(command => name is null || command.Name.Split(' ')[0] == name))
            text.AppendLine($"  pisharp durable {command} {usage}".TrimEnd() + $"\n      {summary}");
        if (name == "attach") text.AppendLine(DurableAttach.Help);
        return text.ToString();
    }

    /// <summary>Runs an operator command; <paramref name="args"/> start with its name.</summary>
    internal static async Task<int> RunAsync(string[] args, PiHost host, CancellationToken cancellationToken)
    {
        if (args[0] == "attach") return await DurableAttach.RunAsync(args[1..], host, cancellationToken).ConfigureAwait(false);
        var json = args.Contains("--json", StringComparer.Ordinal);
        try
        {
            return args switch
            {
                ["doctor", .. var rest] => await DoctorAsync(Parse("doctor", rest), host, json, cancellationToken).ConfigureAwait(false),
                ["service", .. var rest] => await ServiceAsync(Parse("service", rest), host).ConfigureAwait(false),
                ["backup", "create", .. var rest] => await BackupAsync(Parse("backup create", rest), host, json).ConfigureAwait(false),
                ["backup", "verify", .. var rest] => await VerifyAsync(Parse("backup verify", rest), host, json).ConfigureAwait(false),
                ["backup", "restore", .. var rest] => await RestoreAsync(Parse("backup restore", rest), host, json).ConfigureAwait(false),
                _ => throw new RemoteUsageException((args.Length > 1 ? $"Unknown command: {string.Join(' ', args[..2])}\n" : "") + Help(args[0]).TrimEnd()),
            };
        }
        catch (RemoteUsageException usage)
        {
            await host.Stderr.WriteLineAsync(usage.Message).ConfigureAwait(false);
            return DurableExit.Usage;
        }
        catch (DeviceLockedException locked)
        {
            await Error(host, json, "conflict", locked.Message).ConfigureAwait(false);
            return DurableExit.Conflict;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            await Error(host, json, "failed", error.Message).ConfigureAwait(false);
            return DurableExit.Failed;
        }
    }

    private static RemoteArgs Parse(string name, string[] args)
    {
        var parsed = RemoteArgs.Parse(args, UsageShape.Parse(Commands.First(command => command.Name == name).Usage));
        if (parsed.Option("--device") is not null || parsed.Option("--token-file") is not null)
            throw new RemoteUsageException($"pisharp durable {name} works on a local data directory; it takes no --device or --token-file.");
        return parsed;
    }

    private static async Task Error(PiHost host, bool json, string code, string message) =>
        await host.Stderr.WriteLineAsync(json ? new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString() : message)
            .ConfigureAwait(false);

    // ─── doctor ─────────────────────────────────────────────────────────────

    private static async Task<int> DoctorAsync(RemoteArgs args, PiHost host, bool json, CancellationToken cancellationToken)
    {
        var data = DurableRemote.DataDirectory(args.Option("--data-dir"), host);
        IReadOnlyList<string>? repaired = null;
        if (args.Flag("--repair"))
        {
            var actions = new List<string>(DeviceDoctor.Repair(data));
            var token = DeviceConfig.TokenPath(data);
            if (!OperatingSystem.IsWindows() && File.Exists(token) && (File.GetUnixFileMode(token) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                File.SetUnixFileMode(token, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                actions.Add("Made the token file readable by its owner only.");
            }
            repaired = actions;
        }
        var checks = new List<DoctorCheck>
        {
            new("runtime", "info", $"PiSharp {PiConfig.Version}, .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()}"),
        };
        checks.AddRange(DeviceDoctor.Check(data, new DoctorOptions { VerifyBlobs = args.Flag("--verify-blobs") }));
        if (Directory.Exists(data))
        {
            checks.Add(Config(data, out var config));
            checks.Add(Token(data));
            if (args.Flag("--probe")) checks.Add(await ProbeAsync(data, config, cancellationToken).ConfigureAwait(false));
        }
        var failed = checks.Any(check => check.Status == "fail");
        if (json)
        {
            var result = new JsonObject { ["dataDirectory"] = data, ["ok"] = !failed, ["checks"] = new JsonArray([.. checks.Select(check => (JsonNode)check.ToJson())]) };
            if (repaired is not null) result["repaired"] = new JsonArray([.. repaired.Select(action => (JsonNode?)action)]);
            await host.Stdout.WriteLineAsync(result.ToJsonString(Indented)).ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            if (repaired is not null) foreach (var action in repaired) text.AppendLine("repaired: " + action);
            foreach (var check in checks)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{check.Status.ToUpperInvariant(),-4}  {check.Id}: {check.Message}");
                if (check.Fix is not null && check.Status is "warn" or "fail" or "info") text.AppendLine("      → " + check.Fix);
            }
            text.Append(failed ? "Some checks failed." : "No check failed.");
            await host.Stdout.WriteLineAsync(text.ToString()).ConfigureAwait(false);
        }
        return failed ? DurableExit.Failed : DurableExit.Ok;
    }

    private static DoctorCheck Config(string data, out DeviceConfig config)
    {
        config = new DeviceConfig();
        var path = Path.Join(data, "device.json");
        if (!File.Exists(path)) return new DoctorCheck("config", "ok", "No device.json: the device listens on 127.0.0.1:" + config.Port.ToString(CultureInfo.InvariantCulture) + ".");
        try
        {
            config = DeviceConfig.Load(data);
        }
        catch (InvalidDataException error)
        {
            return new DoctorCheck("config", "fail", error.Message, "Fix or remove it; `pisharp durable serve --save` writes a new one.");
        }
        var listener = $"{config.Host}:{config.Port.ToString(CultureInfo.InvariantCulture)}";
        return config.Host is "127.0.0.1" or "localhost" or "::1"
            ? new DoctorCheck("config", "ok", $"Listens on {listener} (loopback).")
            : new DoctorCheck("config", "warn", $"Listens on {listener}, beyond this machine. Anyone who reaches it with the token can run commands as you, and plain HTTP sends the token in clear text.",
                "Keep it on 127.0.0.1 and use an SSH tunnel, or a reverse proxy with TLS (docs/durable-remote-access.md).");
    }

    private static DoctorCheck Token(string data)
    {
        var path = DeviceConfig.TokenPath(data);
        if (!File.Exists(path)) return new DoctorCheck("token", "info", "No token file yet; the device makes one when it starts.");
        string token;
        try { token = File.ReadAllText(path).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new DoctorCheck("token", "fail", "The token file cannot be read: " + error.Message);
        }
        if (!DeviceTokens.IsStrong(token))
            return new DoctorCheck("token", "fail", $"The token is shorter than {DeviceTokens.MinimumLength} characters or has spaces.", "pisharp durable token --rotate");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
            return new DoctorCheck("token", "warn", "The token file is readable by other users.", "pisharp durable doctor --repair makes it owner-only.");
        return new DoctorCheck("token", "ok", "Present and strong (not shown).");
    }

    private static async Task<DoctorCheck> ProbeAsync(string data, DeviceConfig config, CancellationToken cancellationToken)
    {
        var address = config.Host is "0.0.0.0" or "::" or "" ? "127.0.0.1" : config.Host;
        var url = new Uri($"http://{(address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address)}:{config.Port.ToString(CultureInfo.InvariantCulture)}/");
        var tokenPath = DeviceConfig.TokenPath(data);
        if (!File.Exists(tokenPath)) return new DoctorCheck("probe", "warn", $"Not probed: no token file to call {url} with.");
        using var client = new DurableDeviceClient(url, File.ReadAllText(tokenPath).Trim());
        try
        {
            var device = await client.DeviceAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return new DoctorCheck("probe", "ok", $"{url} answers as \"{device.Name}\" (API {device.ApiVersion}, {device.Features?.Count ?? 0} features).");
        }
        catch (DurableApiException error)
        {
            return error.Code == DurableErrorCodes.Unauthorized
                ? new DoctorCheck("probe", "fail", $"{url} refuses the token in {tokenPath}.", "Restart the device after a token rotation, or check --data-dir.")
                : new DoctorCheck("probe", "warn", $"{url} did not answer ({error.Code}): {error.Message}", "Start it with pisharp durable serve, or start its service.");
        }
    }

    // ─── service ────────────────────────────────────────────────────────────

    private static async Task<int> ServiceAsync(RemoteArgs args, PiHost host)
    {
        var kind = args.Positional[0];
        var data = DurableRemote.DataDirectory(args.Option("--data-dir"), host);
        var (executable, prefix) = args.Option("--exec") is { } exec ? (Path.GetFullPath(PiPaths.NormalizePath(exec, host.Home), host.Cwd), Array.Empty<string>()) : Executable();
        var (file, content, install) = ServiceTemplate(kind, executable, [.. prefix, "durable", "serve", "--data-dir", data], data, host.Home);
        if (args.Option("--out") is not { } output)
        {
            await host.Stdout.WriteLineAsync($"# {file}\n{content}\n# Nothing was installed. To install:\n{install}").ConfigureAwait(false);
            return DurableExit.Ok;
        }
        var directory = Path.GetFullPath(PiPaths.NormalizePath(output, host.Home), host.Cwd);
        var path = Path.Join(directory, file);
        if (File.Exists(path)) throw new IOException($"{path} exists; choose another --out or remove it.");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
        await host.Stdout.WriteLineAsync($"Wrote {path}. Nothing was installed. To install:\n{install}").ConfigureAwait(false);
        return DurableExit.Ok;
    }

    /// <summary>The program that runs pisharp: the apphost, or <c>dotnet</c> with the entry assembly.</summary>
    private static (string Executable, string[] Prefix) Executable()
    {
        var process = Environment.ProcessPath ?? "pisharp";
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry
            ? (process, [entry])
            : (process, []);
    }

    private static string LogNote(string data) =>
        $"Logs: {DeviceLog.DefaultPath(data)} (rotated at {DeviceLog.DefaultMaxBytes / (1024 * 1024)} MiB, {DeviceLog.DefaultKeep} kept)";

    /// <summary>A service definition: its file name, content and the install commands (printed, never run).</summary>
    internal static (string File, string Content, string Install) ServiceTemplate(string kind, string executable, IReadOnlyList<string> arguments, string data, string home)
    {
        const string Notice = "Generated by `pisharp durable service`. It runs the device as you, on the listener in device.json (127.0.0.1 unless changed); " +
            "it holds no token: the device reads its token file.";
        switch (kind)
        {
            case "systemd":
            {
                var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(Systemd));
                var content = $"""
                    # {Notice}
                    [Unit]
                    Description=PiSharp durable device
                    After=network-online.target

                    [Service]
                    Type=simple
                    ExecStart={command}
                    Restart=on-failure
                    RestartSec=5
                    TimeoutStopSec=30
                    KillSignal=SIGTERM
                    NoNewPrivileges=true

                    [Install]
                    WantedBy=default.target
                    """;
                return ("pisharp-durable.service", content + "\n", """
                    mkdir -p ~/.config/systemd/user && cp pisharp-durable.service ~/.config/systemd/user/
                    systemctl --user daemon-reload && systemctl --user enable --now pisharp-durable
                    loginctl enable-linger "$USER"   # optional: keep it running after you log out
                    Stop: systemctl --user stop pisharp-durable    Logs: journalctl --user -u pisharp-durable
                    """);
            }
            case "launchd":
            {
                var items = string.Join("\n", new[] { executable }.Concat(arguments).Concat(["--log", DeviceLog.DefaultPath(data)])
                    .Select(argument => $"    <string>{SecurityElement.Escape(argument)}</string>"));
                // The device rotates its own log; launchd's file only gets what the runtime prints outside it, such as a crash.
                var logs = SecurityElement.Escape(Path.Join(data, "logs", "launchd.log"));
                var content = $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <!-- {SecurityElement.Escape(Notice)} -->
                    <plist version="1.0">
                    <dict>
                      <key>Label</key>
                      <string>dev.pisharp.durable</string>
                      <key>ProgramArguments</key>
                      <array>
                    {items}
                      </array>
                      <key>RunAtLoad</key>
                      <true/>
                      <key>KeepAlive</key>
                      <dict>
                        <key>SuccessfulExit</key>
                        <false/>
                      </dict>
                      <key>ExitTimeOut</key>
                      <integer>30</integer>
                      <key>StandardOutPath</key>
                      <string>{logs}</string>
                      <key>StandardErrorPath</key>
                      <string>{logs}</string>
                    </dict>
                    </plist>
                    """;
                return ("dev.pisharp.durable.plist", content + "\n", $"""
                    cp dev.pisharp.durable.plist ~/Library/LaunchAgents/
                    launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/dev.pisharp.durable.plist
                    Stop: launchctl bootout gui/$(id -u)/dev.pisharp.durable    {LogNote(data)}
                    """);
            }
            case "windows":
            {
                var argumentLine = string.Join(' ', arguments.Concat(["--log", DeviceLog.DefaultPath(data)]).Select(Windows));
                var content = $"""
                    # {Notice}
                    # Registers a scheduled task that starts the device when you log on and restarts it if it fails. It runs as you, with your
                    # Pi settings and model credentials, which a Windows service account would not have.
                    $action = New-ScheduledTaskAction -Execute '{executable.Replace("'", "''", StringComparison.Ordinal)}' -Argument '{argumentLine.Replace("'", "''", StringComparison.Ordinal)}'
                    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
                    $settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
                    Register-ScheduledTask -TaskName 'PiSharp durable device' -Action $action -Trigger $trigger -Settings $settings -Description 'PiSharp durable device ({data.Replace("'", "''", StringComparison.Ordinal)})'
                    """;
                return ("pisharp-durable-task.ps1", content + "\n", $"""
                    powershell -ExecutionPolicy Bypass -File .\pisharp-durable-task.ps1
                    Start-ScheduledTask -TaskName 'PiSharp durable device'
                    Stop: Stop-ScheduledTask -TaskName 'PiSharp durable device'    Remove: Unregister-ScheduledTask -TaskName 'PiSharp durable device'
                    {LogNote(data)}
                    """);
            }
            default:
                throw new RemoteUsageException($"{kind} is not one of systemd, launchd, windows.");
        }
    }

    private static string Systemd(string argument) =>
        argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or ':' or '=' or ',')
            ? argument.Replace("%", "%%", StringComparison.Ordinal)
            : "\"" + argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal) + "\"";

    private static string Windows(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // ─── backup ─────────────────────────────────────────────────────────────

    private static async Task<int> BackupAsync(RemoteArgs args, PiHost host, bool json)
    {
        var data = DurableRemote.DataDirectory(args.Option("--data-dir"), host);
        var target = Path.GetFullPath(PiPaths.NormalizePath(args.Required("--out"), host.Home), host.Cwd);
        var manifest = DeviceBackup.Create(data, target, args.Flag("--include-secrets"));
        await host.Stdout.WriteLineAsync(json ? Summary(manifest, target).ToJsonString(Indented) :
            $"Backed up {data} to {target}: catalog version {manifest.CatalogVersion}, {manifest.Sessions.Count} sessions, {manifest.Files.Count} files, " +
            $"{manifest.Files.Sum(file => file.Size)} bytes. {(manifest.IncludesSecrets ? "It includes the device token: keep it private." : "The device token is not included.")}")
            .ConfigureAwait(false);
        return DurableExit.Ok;
    }

    private static async Task<int> VerifyAsync(RemoteArgs args, PiHost host, bool json)
    {
        var root = Path.GetFullPath(PiPaths.NormalizePath(args.Positional[0], host.Home), host.Cwd);
        var check = DeviceBackup.Verify(root);
        if (json)
        {
            var result = check.Manifest is { } manifest ? Summary(manifest, root) : new JsonObject { ["path"] = root };
            result["ok"] = check.Ok;
            result["problems"] = new JsonArray([.. check.Problems.Select(problem => (JsonNode?)problem)]);
            await host.Stdout.WriteLineAsync(result.ToJsonString(Indented)).ConfigureAwait(false);
        }
        else
            await host.Stdout.WriteLineAsync(check.Ok
                ? $"{root} is intact: {check.Manifest!.Files.Count} files match the manifest and every database passes its integrity check."
                : $"{root} is damaged:\n" + string.Join("\n", check.Problems.Select(problem => "  " + problem))).ConfigureAwait(false);
        return check.Ok ? DurableExit.Ok : DurableExit.Invalid;
    }

    private static async Task<int> RestoreAsync(RemoteArgs args, PiHost host, bool json)
    {
        var root = Path.GetFullPath(PiPaths.NormalizePath(args.Positional[0], host.Home), host.Cwd);
        var data = DurableRemote.DataDirectory(args.Required("--data-dir"), host);
        var manifest = DeviceBackup.Restore(root, data);
        await host.Stdout.WriteLineAsync(json ? Summary(manifest, data).ToJsonString(Indented) :
            $"Restored {manifest.Sessions.Count} sessions ({manifest.Files.Count} files) into {data}. Start it with: pisharp durable serve --data-dir \"{data}\"" +
            (manifest.IncludesSecrets ? "" : " (it makes a new token; give hubs the new one)")).ConfigureAwait(false);
        return DurableExit.Ok;
    }

    private static JsonObject Summary(BackupManifest manifest, string path) => new()
    {
        ["path"] = path, ["createdAt"] = manifest.CreatedAt, ["deviceUid"] = manifest.DeviceUid, ["catalogVersion"] = manifest.CatalogVersion,
        ["sessions"] = manifest.Sessions.Count, ["files"] = manifest.Files.Count, ["bytes"] = manifest.Files.Sum(file => file.Size), ["includesSecrets"] = manifest.IncludesSecrets,
    };
}
