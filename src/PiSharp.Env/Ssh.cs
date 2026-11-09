// Adapted from Pi v1.5.0 packages/env/src/ssh.ts (MIT); native .NET process transport and trusted artifact catalog.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Env;

public record SshTarget
{
    public required string Host { get; init; }
    public string? User { get; init; }
    public int? Port { get; init; }
    public string? IdentityFile { get; init; }
    public required string KnownHostsFile { get; init; }
    public required string HostKeyAlias { get; init; }
    public string Ssh { get; init; } = "ssh";
    public string? ConfigFile { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record SshConnectOptions : SshTarget
{
    public required DaemonCatalog Catalog { get; init; }
    public bool LoginShell { get; init; }
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public Action<string>? OnLog { get; init; }
}

public sealed record RemotePlatform(string Platform, string Arch, string Home, string? Shell = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public class SshException(string message, int? exitCode, string diagnostics) : IOException(message)
{
    public int? ExitCode { get; } = exitCode;
    public string Diagnostics { get; } = diagnostics;
}
public sealed class HostKeyUnknownException() : SshException("The SSH host key is not trusted; verify and accept it before connecting.", null, "");
public sealed class HostKeyChangedException() : SshException("The SSH host key changed; verify it independently before replacing the trusted key.", null, "");

/// <summary>Lazy SSH connection. Detection is cached; the binary is hash-checked at every reconnect.</summary>
public sealed class SshConnection : IAsyncDisposable
{
    public Connection Connection { get; }
    public RemotePlatform? Remote { get; private set; }
    public static async Task<SshConnection> ConnectAsync(SshConnectOptions options, CancellationToken cancellationToken = default)
    {
        var connection = new SshConnection(options);
        try { await connection.Connection.InfoAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public SshConnection(SshConnectOptions options)
    {
        Ssh.Arguments(options); // Validate before constructing a lazy connection.
        ArgumentNullException.ThrowIfNull(options.Catalog);
        DaemonArtifact? selected = null;
        Connection = new Connection(new()
        {
            CommandFactory = async cancellationToken =>
            {
                if (Remote is null)
                {
                    Remote = await Ssh.DetectPlatformAsync(options, cancellationToken);
                    foreach (var warning in Remote.Warnings)
                        try { options.OnLog?.Invoke(warning + "\n"); } catch { /* Diagnostics never own connection lifetime. */ }
                }
                var artifact = options.Catalog.Select(Remote.Platform, Remote.Arch);
                selected = artifact;
                var path = await Ssh.DeployDaemonAsync(options, Remote, options.Catalog, artifact, options.UploadTimeout, cancellationToken);
                return [options.Ssh, .. Ssh.Arguments(options), Ssh.LaunchCommand(Remote, path, options.LoginShell)];
            },
            OnLog = options.OnLog,
            ValidateHello = info =>
            {
                if (selected is null || info.Version != selected.Version)
                    throw new InvalidDataException("Daemon hello version does not match the verified artifact catalog.");
                var platform = RemotePlatformNames.NodePlatform(info);
                if ((platform.Platform == "win32" ? "windows" : platform.Platform) != selected.Platform || platform.Arch != selected.Arch)
                    throw new InvalidDataException("Daemon hello platform does not match the verified artifact catalog.");
            },
            // Includes detection, verification/upload and protocol hello.
            StartupTimeout = options.CommandTimeout + options.CommandTimeout + options.UploadTimeout + TimeSpan.FromSeconds(60),
        });
    }
    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

public static class Ssh
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> KnownHostsLocks = new(StringComparer.OrdinalIgnoreCase);
    private const string ProbeMarker = "PI-ENV-PROBE";
    private const string PosixProbe = "sh -c 'echo PI-ENV-PROBE; uname -s; uname -m; uname -o 2>/dev/null || echo -; printf \"%s\\n\" \"$HOME\" \"${TMPDIR:--}\" \"${LD_PRELOAD:--}\"'";
    private const string HashFunction = "hash() { if command -v sha256sum >/dev/null 2>&1; then sha256sum \"$1\" | cut -d' ' -f1; elif command -v shasum >/dev/null 2>&1; then shasum -a 256 \"$1\" | cut -d' ' -f1; elif command -v openssl >/dev/null 2>&1; then openssl dgst -sha256 \"$1\" | sed 's/.*= //'; else echo none; fi; }";

    public static IReadOnlyList<string> Arguments(SshTarget target)
    {
        Field(target.Host); Field(target.HostKeyAlias);
        if (target.User is not null) Field(target.User);
        if (target.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(target.Port));
        Timeout(target.ConnectTimeout); Timeout(target.CommandTimeout);
        var arguments = new List<string>();
        if (target.ConfigFile is not null) { SafeText(target.ConfigFile); arguments.AddRange(["-F", target.ConfigFile]); }
        arguments.AddRange(["-T", "-a", "-x"]);
        void Option(string value) => arguments.AddRange(["-o", value]);
        foreach (var setting in new[] { "BatchMode=yes", "ClearAllForwardings=yes", "ForwardAgent=no", "ForwardX11=no", "ControlMaster=no", "ControlPath=none", "RemoteCommand=none", "PermitLocalCommand=no", "SendEnv=-*", "ServerAliveInterval=15", "StrictHostKeyChecking=yes", "GlobalKnownHostsFile=none", "HashKnownHosts=no" }) Option(setting);
        Option("ConnectTimeout=" + Math.Ceiling(target.ConnectTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Option("UserKnownHostsFile=" + ConfigPath(target.KnownHostsFile));
        Option("HostKeyAlias=" + target.HostKeyAlias);
        if (target.User is not null) arguments.AddRange(["-l", target.User]);
        if (target.Port is { } port) arguments.AddRange(["-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (target.IdentityFile is not null) { Option("IdentityFile=" + ConfigPath(target.IdentityFile)); Option("IdentitiesOnly=yes"); }
        arguments.AddRange(["--", target.Host]);
        return arguments;
    }

    public static async Task<RemotePlatform> DetectPlatformAsync(SshTarget target, CancellationToken cancellationToken = default)
    {
        string[] probe;
        async Task<string[]> Windows() => ParseProbe(await RunAsync(target, PowerShell("'PI-ENV-PROBE'; 'Windows'; [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(); '-'; $HOME; '-'; '-'"), cancellationToken: cancellationToken));
        try { probe = ParseProbe(await RunAsync(target, PosixProbe, cancellationToken: cancellationToken)); }
        catch (Exception error) when (error is not HostKeyUnknownException and not HostKeyChangedException and not OperationCanceledException && error is SshException or InvalidDataException)
        { probe = await Windows(); }
        if (Regex.IsMatch(probe[0], "^(MINGW|MSYS|CYGWIN)", RegexOptions.CultureInvariant)) probe = await Windows();
        var arch = probe[1].ToLowerInvariant() switch { "x86_64" or "amd64" or "x64" => "x64", "aarch64" or "arm64" => "arm64", _ => throw new PlatformNotSupportedException("Unsupported remote architecture.") };
        SafeText(probe[3]);
        if (probe[3].Length == 0) throw new InvalidDataException("Remote home is empty.");
        if (probe[0] == "Windows") return new("windows", arch, probe[3], (await RunAsync(target, "echo %OS%", cancellationToken: cancellationToken)).Contains("Windows_NT", StringComparison.Ordinal) ? "cmd" : "powershell");
        if (probe[0] == "Darwin") return new("darwin", arch, probe[3]);
        if (probe[0] == "Linux")
        {
            if (probe[2] != "Android") return new("linux", arch, probe[3]);
            var warnings = new List<string>();
            if (probe[4] == "-") warnings.Add("TMPDIR is not set; Termux sshd normally sets it to $PREFIX/tmp.");
            if (!probe[5].Contains("termux-exec", StringComparison.Ordinal)) warnings.Add("termux-exec is not loaded; scripts using /usr/bin/env shebangs may fail.");
            warnings.Add("Android may suspend Termux; use termux-wake-lock on the device for persistent connections.");
            return new("android", arch, probe[3]) { Warnings = warnings };
        }
        throw new PlatformNotSupportedException("Unsupported remote system.");
    }

    public static async Task<string> DeployDaemonAsync(SshTarget target, RemotePlatform remote, DaemonCatalog catalog, DaemonArtifact artifact,
        TimeSpan? uploadTimeout = null, CancellationToken cancellationToken = default)
    {
        DaemonCatalog.ValidatePlatform(remote.Platform, remote.Arch, artifact.Backend == "upstream-rust");
        if (artifact.Platform != remote.Platform || artifact.Arch != remote.Arch) throw new ArgumentException("Daemon artifact does not match remote platform.");
        SafeText(remote.Home);
        var bytes = await catalog.ReadVerifiedAsync(artifact, cancellationToken);
        var hash = artifact.Sha256.ToLowerInvariant();
        var path = remote.Home.TrimEnd('/', '\\') + (remote.Platform == "windows" ? "\\.pi\\mobile\\tools\\pi-env-" + hash + ".exe" : "/.pi/mobile/tools/pi-env-" + hash);
        var check = remote.Platform == "windows"
            ? PowerShell($"$ErrorActionPreference='Stop'; $f={QuotePowerShell(path)}; if ((Test-Path -LiteralPath $f) -and ((Get-FileHash -Algorithm SHA256 -LiteralPath $f).Hash.ToLower() -eq '{hash}')) {{ 'present' }} else {{ 'missing' }}")
            : "sh -c " + QuotePosix($"{HashFunction}; f={QuotePosix(path)}; if [ -f \"$f\" ] && [ \"$(hash \"$f\")\" = {hash} ]; then echo present; elif [ \"$(hash /dev/null)\" = none ]; then echo nohash; else echo missing; fi");
        var state = LastLine(await RunAsync(target, check, cancellationToken: cancellationToken));
        if (state == "present") return path;
        if (state == "nohash") throw new InvalidOperationException("Remote host has no SHA-256 tool; refusing to deploy unverified daemon.");
        if (state != "missing") throw new InvalidDataException("Unexpected daemon verification response.");
        string upload;
        ReadOnlyMemory<byte> input;
        if (remote.Platform == "windows")
        {
            upload = PowerShell($"$ErrorActionPreference='Stop'; $f={QuotePowerShell(path)}; $d=Split-Path -Parent $f; New-Item -ItemType Directory -Force -Path $d | Out-Null; $t=Join-Path $d ('.pi-env-' + [guid]::NewGuid().ToString() + '.tmp'); try {{ $text=New-Object System.Text.StringBuilder; foreach ($line in $input) {{ if ($line -eq 'PI-ENV-END') {{ break }}; [void]$text.Append($line) }}; $bytes=[Convert]::FromBase64String($text.ToString()); $out=[IO.File]::Open($t,'CreateNew','Write','None'); try {{ $out.Write($bytes,0,$bytes.Length) }} finally {{ $out.Dispose() }}; if ((Get-FileHash -Algorithm SHA256 -LiteralPath $t).Hash.ToLower() -ne '{hash}') {{ throw 'pi-env upload is corrupt' }}; Move-Item -Force -LiteralPath $t -Destination $f; 'deployed' }} finally {{ if (Test-Path -LiteralPath $t) {{ Remove-Item -LiteralPath $t -Force }} }}");
            input = Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks) + "\nPI-ENV-END\n");
        }
        else
        {
            upload = "sh -c " + QuotePosix($"set -e\n{HashFunction}\nf={QuotePosix(path)}\nd=$(dirname \"$f\")\nmkdir -p \"$d\"\nchmod 700 \"$d\"\nt=$(mktemp \"$d/.pi-env.XXXXXX\")\ntrap 'rm -f \"$t\"' EXIT HUP INT TERM\ncat > \"$t\"\nif [ \"$(hash \"$t\")\" != {hash} ]; then echo 'pi-env upload is corrupt' >&2; exit 1; fi\nchmod 700 \"$t\"\nmv -f \"$t\" \"$f\"\necho deployed");
            input = bytes;
        }
        if (LastLine(await RunAsync(target, upload, input, uploadTimeout ?? TimeSpan.FromMinutes(5), cancellationToken)) != "deployed")
            throw new InvalidDataException("Daemon deployment did not confirm installation.");
        return path;
    }

    public static string LaunchCommand(RemotePlatform remote, string path, bool loginShell = false)
    {
        SafeText(path);
        if (remote.Platform == "windows")
        {
            if (remote.Shell == "powershell") return "& " + QuotePowerShell(path);
            // cmd expands percent/environment variables even inside quotes. Fail closed for unrepresentable paths.
            if (path.IndexOfAny(['%', '!', '"', '^', '&', '|', '<', '>']) >= 0) throw new ArgumentException("Daemon path cannot be safely represented in cmd.exe.");
            return "\"" + path + "\"";
        }
        return loginShell ? "exec \"$SHELL\" -lc 'exec \"$0\" \"$@\"' " + QuotePosix(path) : QuotePosix(path);
    }

    /// <summary>Capture offered keys without trusting them for future connections. Compare fingerprints out of band.</summary>
    public static async Task<HostKeyScan> ScanHostKeyAsync(SshTarget target, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pi-env-hostkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "known_hosts");
        try
        {
            try { await RunAsync(target with { KnownHostsFile = file }, "exit 0", cancellationToken: cancellationToken, scanKeys: true); }
            catch (SshException error) when (error is not HostKeyChangedException) { }
            if (!File.Exists(file)) throw new InvalidDataException("SSH returned no host key.");
            var lines = (await File.ReadAllLinesAsync(file, cancellationToken)).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (lines.Length == 0) throw new InvalidDataException("SSH returned no host key.");
            var fingerprints = lines.Select(line => ParseKey(target.HostKeyAlias, line)).Select(key => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(key.Key))).TrimEnd('=')).ToArray();
            return new(lines, fingerprints);
        }
        finally { if (File.Exists(file)) File.Delete(file); Directory.Delete(directory); }
    }

    public static Task AcceptHostKeyAsync(SshTarget target, IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        Arguments(target);
        var accepted = lines.Select(line => (Line: line.Trim(), Key: ParseKey(target.HostKeyAlias, line))).ToArray();
        return ChangeKeysAsync(target.KnownHostsFile, existing =>
        {
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in existing)
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 3 && fields[0] == target.HostKeyAlias) keys[fields[1]] = fields[2];
            }
            foreach (var (line, key) in accepted)
            {
                if (keys.TryGetValue(key.Type, out var known)) { if (known != key.Key) throw new HostKeyChangedException(); continue; }
                keys.Add(key.Type, key.Key); existing.Add(line);
            }
            return existing;
        }, cancellationToken);
    }

    public static Task ForgetHostKeyAsync(SshTarget target, CancellationToken cancellationToken = default)
    {
        Arguments(target);
        return ChangeKeysAsync(target.KnownHostsFile, existing => existing.Where(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() != target.HostKeyAlias).ToList(), cancellationToken);
    }

    private static (string Type, string Key) ParseKey(string alias, string line)
    {
        if (line.Any(c => c < 32 && c != '\t' || c == 127)) throw new ArgumentException("Control character in host key line.");
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3 || fields[0] != alias || !Regex.IsMatch(fields[1], "^(ssh-[a-z0-9-]+|ecdsa-sha2-[a-z0-9-]+|sk-[a-z0-9@.-]+)$") || !Regex.IsMatch(fields[2], "^[A-Za-z0-9+/]+={0,2}$"))
            throw new ArgumentException("Invalid host key line or alias.");
        try { if (Convert.FromBase64String(fields[2]).Length == 0) throw new FormatException(); } catch (FormatException) { throw new ArgumentException("Invalid host key data."); }
        return (fields[1], fields[2]);
    }

    private static async Task ChangeKeysAsync(string file, Func<List<string>, List<string>> change, CancellationToken cancellationToken)
    {
        file = Path.GetFullPath(file);
        var gate = KnownHostsLocks.GetOrAdd(file, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var existing = File.Exists(file) ? (await File.ReadAllLinesAsync(file, cancellationToken)).ToList() : [];
            var updated = change(existing);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(temporary, updated.Count > 0 ? string.Join('\n', updated) + "\n" : "", new UTF8Encoding(false), cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, file, true);
        }
        finally { File.Delete(temporary); gate.Release(); }
    }

    private static async Task<string> RunAsync(SshTarget target, string command, ReadOnlyMemory<byte> input = default, TimeSpan? timeout = null, CancellationToken cancellationToken = default, bool scanKeys = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Timeout(timeout ?? target.CommandTimeout);
        var start = new ProcessStartInfo(target.Ssh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in Arguments(target)) start.ArgumentList.Add(scanKeys && argument == "StrictHostKeyChecking=yes" ? "StrictHostKeyChecking=accept-new" : argument);
        start.ArgumentList.Add(command);
        using var process = new Process { StartInfo = start };
        try { process.Start(); } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { throw new SshException("Could not start SSH transport.", null, ""); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? target.CommandTimeout);
        using var kill = deadline.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        var stdout = ReadBoundedAsync(process.StandardOutput, 65536, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError, 2000, deadline.Token);
        try
        {
            try { await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token); await process.StandardInput.BaseStream.FlushAsync(deadline.Token); }
            catch (IOException) { /* The exit code and diagnostics describe an early transport failure. */ }
            finally { process.StandardInput.Close(); }
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout; var diagnostics = Sanitize(await stderr);
            if (process.ExitCode == 0) return output;
            if (diagnostics.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal)) throw new HostKeyChangedException();
            if (diagnostics.Contains("Host key verification failed", StringComparison.Ordinal) || Regex.IsMatch(diagnostics, "No .* host key is known")) throw new HostKeyUnknownException();
            throw new SshException("SSH command failed with exit code " + process.ExitCode + (diagnostics.Length > 0 ? ": " + diagnostics : ""), process.ExitCode, diagnostics);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new SshException("SSH command timed out.", null, ""); }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            // Cancellation signals cleanup; it does not relinquish ownership of the process or reader originals.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // A descendant may have inherited a pipe. Cancel reads after the owned process has been reaped.
            deadline.Cancel();
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0) { result.Append(buffer, 0, count); if (result.Length > limit) result.Remove(0, result.Length - limit); }
        return result.ToString();
    }
    private static string Sanitize(string text)
    {
        text = Regex.Replace(text, "\\x1B(?:\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\[[0-?]*[ -/]*[@-~])", "");
        text = new string(text.Replace("\r", "").Where(c => c is '\n' or '\t' || c >= 32 && !(c >= 127 && c <= 159)).ToArray());
        return string.Join('\n', text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(20));
    }
    private static string[] ParseProbe(string text)
    {
        var lines = text.Replace("\r", "").Split('\n'); var index = Array.IndexOf(lines, ProbeMarker);
        if (index < 0 || lines.Length < index + 7) throw new InvalidDataException("Unexpected remote platform probe response.");
        return lines.Skip(index + 1).Take(6).ToArray();
    }
    private static string LastLine(string text) => text.Trim().Split('\n')[^1].Trim();
    private static string QuotePosix(string text) => "'" + text.Replace("'", "'\\''") + "'";
    private static string QuotePowerShell(string text) => "'" + text.Replace("'", "''") + "'";
    private static string PowerShell(string script) => "powershell -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    private static void SafeText(string value) { if (value.Any(c => c < 32 || c == 127)) throw new ArgumentException("Control character in SSH value."); }
    private static void Field(string value) { if (string.IsNullOrEmpty(value) || value.StartsWith('-') || value.Any(c => char.IsWhiteSpace(c) || c < 32 || c == 127)) throw new ArgumentException("Invalid SSH field."); }
    private static string ConfigPath(string value) { SafeText(value); if (value.Length == 0 || value.Contains('"')) throw new ArgumentException("Invalid SSH option path."); return "\"" + value.Replace("%", "%%") + "\""; }
    private static void Timeout(TimeSpan timeout) { if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout)); }
}

public sealed record HostKeyScan(IReadOnlyList<string> Lines, IReadOnlyList<string> Fingerprints);
