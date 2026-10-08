// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/session-share.ts (UI-free), with
// src/config.ts (getShareViewerUrl), core/radius.ts and packages/ai/src/providers/radius-config.ts (DEFAULT_RADIUS_GATEWAY),
// cli/auth-command.ts (getAuthCredential) and packages/tui/src/terminal-image.ts (hyperlink).
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent.Export;

/// <summary>A resolved provider auth (AuthResult.auth): an API key or request headers.</summary>
public sealed record ShareProviderAuth(string? ApiKey, IReadOnlyDictionary<string, string?>? Headers = null);

/// <summary>The model runtime's Radius view: whether the provider is registered and its auth with a minimum OAuth validity.</summary>
public interface IRadiusShareAuthentication
{
    /// <summary>modelRuntime.getProvider("radius") is defined.</summary>
    bool HasProvider { get; }
    /// <summary>modelRuntime.getAuth("radius", { minOAuthValidityMs }); null when there is no auth. Failures propagate.</summary>
    ValueTask<ShareProviderAuth?> GetAuthAsync(long minimumOAuthValidityMilliseconds, CancellationToken cancellationToken);
}

/// <summary>A finished child process (spawnSync/spawn): a null exit code means it did not run (e.g. ENOENT).</summary>
public sealed record ShareProcessResult(int? ExitCode, string Stdout, string Stderr);

/// <summary>Runs <c>gh</c>. Cancellation kills the process (loader abort). Exceptions mean the spawn itself threw.</summary>
public interface IShareProcessRunner
{
    Task<ShareProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>The session being shared (AgentSession): its session manager view, agent state and HTML export.</summary>
public sealed record SessionShareSession(Func<SessionExportSource> Source, Func<SessionExportAgentState> State,
    Func<string, CancellationToken, Task> ExportToHtmlAsync, IRadiusShareAuthentication? Radius = null);

/// <summary>
/// The /share command's UI seam (SessionShareContext without TUI): status and error lines, and the bordered loader that
/// <see cref="ShowLoader"/> opens and <see cref="RestoreEditor"/> closes. Aborting the loader is the cancellation token.
/// </summary>
public sealed record SessionShareUi(Action<string> ShowStatus, Action<string> ShowError, Action<string>? ShowLoader = null, Action? RestoreEditor = null);

/// <summary>shareSession: upload the branch to Radius when signed in, otherwise a private GitHub gist of the HTML export.</summary>
public sealed class SessionShare
{
    /// <summary>radius-config.ts DEFAULT_RADIUS_GATEWAY (share uses it directly; PI_RADIUS_GATEWAY is not consulted).</summary>
    public const string DefaultRadiusGateway = "https://radius.pi.dev";
    public const string RadiusProviderId = "radius";
    /// <summary>config.ts DEFAULT_SHARE_VIEWER_URL.</summary>
    public const string DefaultShareViewerUrl = "https://pi.dev/session/";
    public const long RadiusMinimumOAuthValidityMilliseconds = 5 * 60_000;

    public IShareProcessRunner Processes { get; init; } = new SystemShareProcessRunner();
    public HttpMessageInvoker? Http { get; init; }
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    /// <summary>os.tmpdir().</summary>
    public string TempRoot { get; init; } = Path.GetTempPath();
    /// <summary>crypto.randomUUID().</summary>
    public Func<string> RandomUuid { get; init; } = () => Guid.NewGuid().ToString("D");
    /// <summary>pi-tui hyperlink(text, url): an OSC 8 link.</summary>
    public Func<string, string, string> Hyperlink { get; init; } = (text, url) => $"\x1b]8;;{url}\x1b\\{text}\x1b]8;;\x1b\\";
    /// <summary>The temp directory of the last share, for diagnostics; removed when the share settles.</summary>
    public string? LastTempDirectory { get; private set; }

    /// <summary>getShareViewerUrl(gistId).</summary>
    public string GetShareViewerUrl(string gistId)
    {
        var configured = Environment("PI_SHARE_VIEWER_URL");
        return (string.IsNullOrEmpty(configured) ? DefaultShareViewerUrl : configured) + "#" + gistId;
    }

    /// <summary>createShareTrailingEntries: a <c>pi.share</c> custom entry with the system prompt and tool schemas.</summary>
    public IReadOnlyList<object?> CreateShareTrailingEntries(SessionExportAgentState state, string? parentId, string timestamp)
    {
        ArgumentNullException.ThrowIfNull(state);
        var data = new JsObject().Set("systemPrompt", state.SystemPrompt ?? (object)Js.Undefined)
            .Set("tools", (state.Tools ?? []).Select(tool => (object?)new JsObject().Set("name", tool.Name).Set("description", tool.Description)
                .Set("parameters", tool.Parameters)).ToList());
        return [new JsObject().Set("type", "custom").Set("customType", "pi.share").Set("id", RandomUuid()[..8]).Set("parentId", parentId)
            .Set("timestamp", timestamp).Set("data", data)];
    }

    /// <summary>exportSessionForShare: the branch plus the pi.share entry as JSONL.</summary>
    public void ExportSessionForShare(string filePath, SessionShareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = session.State();
        SessionJsonlExport.ExportSessionToJsonl(session.Source(), filePath, (parentId, timestamp) => CreateShareTrailingEntries(state, parentId, timestamp), Clock);
    }

    /// <summary>getAuthCredential: the API key, else the token of an <c>Authorization: Bearer</c> header.</summary>
    public static string? GetAuthCredential(ShareProviderAuth? auth)
    {
        if (!string.IsNullOrEmpty(auth?.ApiKey)) return auth.ApiKey;
        var authorization = auth?.Headers?.FirstOrDefault(pair => pair.Key.ToLowerInvariant() == "authorization").Value;
        if (authorization is null) return null;
        var match = Regex.Match(authorization, @"^Bearer\s+(.+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && !match.Groups[1].Value.Contains('\n') && !match.Groups[1].Value.Contains('\r') ? match.Groups[1].Value : null;
    }

    private string MakeTempDirectory()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        for (var attempt = 0; ; attempt++)
        {
            var suffix = string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)]));
            var path = Path.Combine(TempRoot, "pi-share-" + suffix);
            if (Directory.Exists(path) && attempt < 100) continue;
            Directory.CreateDirectory(path); return path;
        }
    }

    /// <summary>shareSession. Cancelling <paramref name="abort"/> while a loader is open is the loader's abort (Esc).</summary>
    public async Task ShareSessionAsync(SessionShareSession session, SessionShareUi ui, CancellationToken abort = default)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(ui);
        var tempDir = MakeTempDirectory(); LastTempDirectory = tempDir;
        var jsonlFile = Path.Combine(tempDir, "session.jsonl"); var htmlFile = Path.Combine(tempDir, "session.html");
        try
        {
            try { ExportSessionForShare(jsonlFile, session); }
            catch (Exception error) { ui.ShowError("Failed to export session: " + error.Message); return; }
            if (await TryShareViaRadiusAsync(jsonlFile, session, ui, abort).ConfigureAwait(false)) return;
            try
            {
                var auth = await Processes.RunAsync("gh", ["auth", "status"], CancellationToken.None).ConfigureAwait(false);
                if (auth.ExitCode != 0) { ui.ShowError("GitHub CLI is not logged in. Run 'gh auth login' first."); return; }
            }
            catch { ui.ShowError("GitHub CLI (gh) is not installed. Install it from https://cli.github.com/"); return; }
            try { await session.ExportToHtmlAsync(htmlFile, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { ui.ShowError("Failed to export session: " + error.Message); return; }
            await ShareViaGistAsync(htmlFile, ui, abort).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>A loader whose abort restores the editor and reports "Share cancelled" once, until it is restored normally.</summary>
    private sealed class Loader : IDisposable
    {
        private readonly SessionShareUi ui; private readonly CancellationToken abort; private CancellationTokenRegistration registration; private int restored;
        public Loader(SessionShareUi ui, string message, CancellationToken abort)
        {
            this.ui = ui; this.abort = abort; ui.ShowLoader?.Invoke(message);
            registration = abort.Register(Aborted);
        }
        private void Aborted() { if (Restore()) ui.ShowStatus("Share cancelled"); }
        public bool Restore() { if (Interlocked.Exchange(ref restored, 1) != 0) return false; ui.RestoreEditor?.Invoke(); return true; }
        // The abort handler runs exactly once even when the aborted operation settles before the token callback is reached.
        public void Dispose() { registration.Dispose(); if (abort.IsCancellationRequested) Aborted(); }
    }

    private async Task<bool> TryShareViaRadiusAsync(string tmpFile, SessionShareSession session, SessionShareUi ui, CancellationToken abort)
    {
        if (session.Radius is not { HasProvider: true } radius) return false;
        var token = GetAuthCredential(await radius.GetAuthAsync(RadiusMinimumOAuthValidityMilliseconds, CancellationToken.None).ConfigureAwait(false));
        if (string.IsNullOrEmpty(token)) return false;
        using var loader = new Loader(ui, "Uploading to Radius...", abort);
        try
        {
            var body = await File.ReadAllBytesAsync(tmpFile, CancellationToken.None).ConfigureAwait(false);
            var url = new Uri(new Uri(DefaultRadiusGateway), "/v1/artifacts?visibility=organization&title=Pi+session");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-ndjson");
            request.Content.Headers.ContentLength = body.Length;
            using var owned = Http is null ? new HttpClient() : null;
            using var response = await (Http ?? owned!).SendAsync(request, abort).ConfigureAwait(false);
            if (abort.IsCancellationRequested) return true;
            object? json;
            try
            {
                var text = await response.Content.ReadAsStringAsync(abort).ConfigureAwait(false);
                json = Js.ParseJson(text.StartsWith((char)0xFEFF) ? text[1..] : text);
            }
            catch (Exception) when (!abort.IsCancellationRequested) { json = null; }
            if (abort.IsCancellationRequested) return true;
            loader.Restore();
            var obj = json as JsObject;
            var artifact = obj is null ? Js.Undefined : obj["artifact"];
            if (!response.IsSuccessStatusCode || !Js.Truthy(artifact))
            {
                var error = obj is null ? Js.Undefined : obj["error"];
                var statusText = response.ReasonPhrase ?? "";
                ui.ShowError("Failed to upload Radius artifact: " + (Js.Truthy(error) ? Js.ToJsString(error) :
                    statusText.Length > 0 ? statusText : ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return true;
            }
            var shareUrl = Js.ToJsString(artifact is JsObject value ? value["canonical_url"] : Js.Undefined);
            ui.ShowStatus("Share URL: " + Hyperlink(shareUrl, shareUrl));
            return true;
        }
        catch (Exception error)
        {
            if (!abort.IsCancellationRequested)
            {
                loader.Restore();
                ui.ShowError("Failed to upload Radius artifact: " + error.Message);
            }
            return true;
        }
    }

    private async Task ShareViaGistAsync(string tmpFile, SessionShareUi ui, CancellationToken abort)
    {
        using var loader = new Loader(ui, "Creating gist...", abort);
        try
        {
            var result = await Processes.RunAsync("gh", ["gist", "create", "--public=false", tmpFile], abort).ConfigureAwait(false);
            if (abort.IsCancellationRequested) return;
            loader.Restore();
            if (result.ExitCode != 0)
            {
                var stderr = Js.Trim(result.Stderr);
                ui.ShowError("Failed to create gist: " + (stderr.Length > 0 ? stderr : "Unknown error"));
                return;
            }
            var gistUrl = Js.Trim(result.Stdout);
            var gistId = gistUrl.Split('/')[^1];
            if (gistId.Length == 0) { ui.ShowError("Failed to parse gist ID from gh output"); return; }
            var previewUrl = GetShareViewerUrl(gistId);
            ui.ShowStatus($"Share URL: {Hyperlink(previewUrl, previewUrl)}\nGist: {Hyperlink(gistUrl, gistUrl)}");
        }
        catch (Exception error)
        {
            if (!abort.IsCancellationRequested)
            {
                loader.Restore();
                ui.ShowError("Failed to create gist: " + error.Message);
            }
        }
    }

}

/// <summary>spawn/spawnSync of a program on PATH without a shell. A program that cannot start yields a null exit code, as spawnSync's
/// status does for ENOENT; cancellation kills the process tree.</summary>
public sealed class SystemShareProcessRunner : IShareProcessRunner
{
    public async Task<ShareProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new Win32Exception(2); }
        catch (Win32Exception) { return new(null, "", ""); }
        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using (cancellationToken.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } }))
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
    }
}
