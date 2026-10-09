// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/tools-manager.ts (TOOLS, getToolPath,
// getLatestVersion, downloadTool, ensureTool) and packages/coding-agent/src/utils/management-http.ts (fetchWithRetry).
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;

namespace PiSharp.Cli.Pi;

internal sealed record PiToolStatus(string Type, string Message);

/// <summary>Source tools-manager.ts: fd and ripgrep from <c>&lt;agentDir&gt;/bin</c>, then PATH, else the latest GitHub release asset
/// for this platform downloaded into the bin directory on first use (never with <c>PI_OFFLINE</c>). As upstream, a download is
/// checked by its HTTP status and by finding the binary in the archive; GitHub publishes no checksums the source verifies.</summary>
internal sealed class PiToolsManager
{
    internal const int NetworkTimeoutMs = 10_000, DownloadTimeoutMs = 120_000;
    private static readonly HashSet<int> RetryableStatusCodes = [408, 425, 429, 500, 502, 503, 504];

    internal sealed record ToolConfig(string Name, string Repo, string BinaryName, string[] SystemBinaryNames, string TagPrefix,
        Func<string, string, string, string?> AssetName);

    internal static readonly IReadOnlyDictionary<string, ToolConfig> Tools = new Dictionary<string, ToolConfig>(StringComparer.Ordinal)
    {
        ["fd"] = new("fd", "sharkdp/fd", "fd", ["fd", "fdfind"], "v", (version, platform, architecture) =>
        {
            var arch = architecture == "arm64" ? "aarch64" : "x86_64";
            return platform switch
            {
                "darwin" => $"fd-v{version}-{arch}-apple-darwin.tar.gz", "linux" => $"fd-v{version}-{arch}-unknown-linux-musl.tar.gz",
                "win32" => $"fd-v{version}-{arch}-pc-windows-msvc.zip", _ => null
            };
        }),
        ["rg"] = new("ripgrep", "BurntSushi/ripgrep", "rg", ["rg"], "", (version, platform, architecture) =>
        {
            var arch = architecture == "arm64" ? "aarch64" : "x86_64";
            return platform switch
            {
                "darwin" => $"ripgrep-{version}-{arch}-apple-darwin.tar.gz", "linux" => $"ripgrep-{version}-{arch}-unknown-linux-musl.tar.gz",
                "win32" => $"ripgrep-{version}-{arch}-pc-windows-msvc.zip", _ => null
            };
        })
    };

    private readonly string binDirectory;
    private readonly Func<string, string?> environment;
    private readonly Func<HttpMessageHandler> createHandler;
    private readonly string githubBase;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal string Platform { get; init; } = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : "unknown";
    internal string Architecture { get; init; } = RuntimeInformation.OSArchitecture switch { System.Runtime.InteropServices.Architecture.Arm64 => "arm64", System.Runtime.InteropServices.Architecture.X64 => "x64", var other => other.ToString().ToLowerInvariant() };

    /// <param name="createHandler">The HTTP handler downloads use (a fake release server in tests); redirects are handled here.</param>
    internal PiToolsManager(string binDirectory, Func<string, string?> environment, Func<HttpMessageHandler>? createHandler = null, string githubBase = "https://github.com")
    {
        this.binDirectory = binDirectory; this.environment = environment;
        this.createHandler = createHandler ?? (() => new SocketsHttpHandler { AllowAutoRedirect = false });
        this.githubBase = githubBase.TrimEnd('/');
    }

    private HttpClient? client;
    /// <summary>One client per manager (redirects are followed by <see cref="SendAsync"/>, which also owns the timeouts).</summary>
    private HttpClient Client => client ??= new HttpClient(createHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    private string Extension => Platform == "win32" ? ".exe" : "";

    /// <summary>Source getToolPath: the bin directory's copy, else the first system name found on PATH (absolute here, since
    /// PiSharp launches executables by path), else null.</summary>
    internal string? GetToolPath(string tool)
    {
        if (!Tools.TryGetValue(tool, out var config)) return null;
        var local = Path.Join(binDirectory, config.BinaryName + Extension);
        if (File.Exists(local)) return local;
        var host = PiSharp.Tools.Processes.ShellHost.Current with { GetEnvironmentVariable = environment };
        foreach (var name in config.SystemBinaryNames)
            if (PiSharp.Tools.Processes.ShellDiscovery.FindExecutableOnPath(name + Extension, host) is { } onPath) return onPath;
        return null;
    }

    /// <summary>Source ensureTool: an existing path, else (unless offline) the downloaded one; status messages as upstream.</summary>
    internal async Task<string?> EnsureToolAsync(string tool, Action<PiToolStatus>? onStatus, CancellationToken cancellationToken)
    {
        if (GetToolPath(tool) is { } existing) return existing;
        if (!Tools.TryGetValue(tool, out var config)) return null;
        if (PiCommand.IsTruthyEnvFlag(environment("PI_OFFLINE")))
        {
            onStatus?.Invoke(new("warning", $"{config.Name} not found. Offline mode enabled, skipping download."));
            return null;
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (GetToolPath(tool) is { } raced) return raced;
            onStatus?.Invoke(new("info", $"{config.Name} not found. Downloading..."));
            try
            {
                var path = await DownloadToolAsync(config, cancellationToken).ConfigureAwait(false);
                onStatus?.Invoke(new("info", $"{config.Name} installed to {path}"));
                return path;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var messages = new List<string>();
                for (Exception? current = error; current is not null && messages.Count < 5; current = current.InnerException)
                    if (!messages.Contains(current.Message)) messages.Add(current.Message);
                onStatus?.Invoke(new("warning", $"Failed to download {config.Name}: {string.Join(": ", messages)}"));
                return null;
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Source getLatestVersion: the tag the <c>releases/latest</c> page redirects to (no API quota), without its <c>v</c>.</summary>
    internal async Task<string> GetLatestVersionAsync(string repo, CancellationToken cancellationToken)
    {
        using var response = await SendAsync($"{githubBase}/{repo}/releases/latest", NetworkTimeoutMs, followRedirects: false, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var location = status is >= 300 and < 400 ? response.Headers.Location?.OriginalString : null;
        if (location is null) throw new IOException($"Failed to resolve latest {repo} release: HTTP {status} without redirect");
        var resolved = new Uri(new Uri("https://github.com"), location);
        var tag = resolved.AbsolutePath.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(tag) || !location.Contains("/releases/tag/", StringComparison.Ordinal))
            throw new IOException($"Failed to resolve latest {repo} release: unexpected redirect to {location}");
        var decoded = Uri.UnescapeDataString(tag);
        return decoded.StartsWith('v') ? decoded[1..] : decoded;
    }

    /// <summary>Source downloadTool: the platform asset into the bin directory, extracted in a unique temporary directory, the binary
    /// moved into place (made executable off Windows), the archive and extraction directory removed.</summary>
    private async Task<string> DownloadToolAsync(ToolConfig config, CancellationToken cancellationToken)
    {
        var version = config.BinaryName == "fd" && Platform == "darwin" && Architecture == "x64" ? "10.3.0"
            : await GetLatestVersionAsync(config.Repo, cancellationToken).ConfigureAwait(false);
        var assetName = config.AssetName(version, Platform, Architecture) ?? throw new IOException($"Unsupported platform: {Platform}/{Architecture}");
        Directory.CreateDirectory(binDirectory);
        var url = $"{githubBase}/{config.Repo}/releases/download/{config.TagPrefix}{version}/{assetName}";
        var archive = Path.Join(binDirectory, assetName);
        var binaryPath = Path.Join(binDirectory, config.BinaryName + Extension);
        using (var response = await SendAsync(url, DownloadTimeoutMs, followRedirects: true, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) throw new IOException($"Download failed with HTTP {(int)response.StatusCode}: {url}");
            await using var file = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        var extractDir = Path.Join(binDirectory, $"extract_tmp_{config.BinaryName}_{Environment.ProcessId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(extractDir);
        try
        {
            if (assetName.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                try
                {
                    await using var source = File.OpenRead(archive);
                    await using var gzip = new GZipStream(source, CompressionMode.Decompress);
                    await TarFile.ExtractToDirectoryAsync(gzip, extractDir, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
                { throw new IOException($"Failed to extract {assetName}: {error.Message}", error); }
            }
            else if (assetName.EndsWith(".zip", StringComparison.Ordinal))
            {
                try { ZipFile.ExtractToDirectory(archive, extractDir, overwriteFiles: true); }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
                { throw new IOException($"Failed to extract {assetName}: {error.Message}", error); }
            }
            else throw new IOException($"Unsupported archive format: {assetName}");
            var binaryFileName = config.BinaryName + Extension;
            var nested = Path.Join(extractDir, assetName.EndsWith(".tar.gz", StringComparison.Ordinal) ? assetName[..^7] : assetName[..^4]);
            var extracted = new[] { Path.Join(nested, binaryFileName), Path.Join(extractDir, binaryFileName) }.FirstOrDefault(File.Exists) ??
                Directory.EnumerateFiles(extractDir, binaryFileName, SearchOption.AllDirectories).FirstOrDefault() ??
                throw new IOException($"Binary not found in archive: expected {binaryFileName} under {extractDir}");
            File.Move(extracted, binaryPath, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        finally
        {
            try { File.Delete(archive); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(extractDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return binaryPath;
    }

    /// <summary>Source fetchWithRetry with its defaults: two retries after the first attempt for transport failures and the
    /// retryable statuses, within one overall timeout. Redirects are followed here when asked (GitHub asset downloads redirect).</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, int timeoutMs, bool followRedirects, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        var client = Client;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var target = new Uri(url);
                for (var hops = 0; ; hops++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, target);
                    request.Headers.TryAddWithoutValidation("User-Agent", $"{PiConfig.AppName}-coding-agent");
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (followRedirects && status is >= 300 and < 400 && response.Headers.Location is { } next && hops < 10)
                    { target = new Uri(target, next); response.Dispose(); continue; }
                    if (RetryableStatusCodes.Contains(status) && attempt < 2) { response.Dispose(); break; }
                    return response;
                }
            }
            catch (Exception error) when (error is HttpRequestException or IOException && attempt < 2 && !timeout.IsCancellationRequested) { }
        }
    }
}
