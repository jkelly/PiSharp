// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/clipboard-image.ts.
// Photon (loadPhoton/convertToPng) is replaced by ClipboardEnvironment.ImageCodec (SkiaSharp by default, as the read tool uses).
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Tools.Images;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>Source <c>ClipboardImage</c>.</summary>
internal sealed record ClipboardImage(byte[] Bytes, string MimeType);

internal static partial class ClipboardImageReader
{
    private static readonly string[] SupportedImageMimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];
    private const int DefaultListTimeoutMs = 1000;
    private const int DefaultPowerShellTimeoutMs = 5000;

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex LineBreak();

    public static bool IsWaylandSession(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return !string.IsNullOrEmpty(env("WAYLAND_DISPLAY")) || env("XDG_SESSION_TYPE") == "wayland";
    }

    private static string BaseMimeType(string mimeType) => PiSharp.Tui.Pi.TextUtils.JsTrim(mimeType.Split(';')[0]).ToLowerInvariant();

    public static string? ExtensionForImageMimeType(string mimeType) => BaseMimeType(mimeType) switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        "image/gif" => "gif",
        _ => null,
    };

    private static string? SelectPreferredImageMimeType(IEnumerable<string> mimeTypes)
    {
        var normalized = mimeTypes.Select(PiSharp.Tui.Pi.TextUtils.JsTrim).Where(t => t.Length > 0).Select(t => (Raw: t, Base: BaseMimeType(t))).ToList();
        foreach (var preferred in SupportedImageMimeTypes)
        {
            foreach (var t in normalized) if (t.Base == preferred) return t.Raw;
        }
        foreach (var t in normalized) if (t.Base.StartsWith("image/", StringComparison.Ordinal)) return t.Raw;
        return null;
    }

    private static bool IsSupportedImageMimeType(string mimeType) => SupportedImageMimeTypes.Contains(BaseMimeType(mimeType));

    private static List<string> SplitLines(byte[] bytes) =>
        [.. LineBreak().Split(Encoding.UTF8.GetString(bytes)).Select(PiSharp.Tui.Pi.TextUtils.JsTrim).Where(t => t.Length > 0)];

    /// <summary>Convert unsupported image formats to PNG (Photon upstream). Null if conversion is unavailable or fails.</summary>
    private static byte[]? ConvertToPng(ClipboardEnvironment environment, byte[] bytes, string mimeType)
    {
        try
        {
            var detected = ImageMime.DetectSupportedImageMimeType(bytes) ?? BaseMimeType(mimeType);
            return environment.ImageCodec.ConvertToPng(bytes, detected);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Unavailable (Available = false) means the backend failed; Image = null means it has no image. An empty
    // Wayland clipboard must not fall through to stale X11 clipboard contents.
    private static async Task<(bool Available, ClipboardImage? Image)> ReadClipboardImageViaWlPaste(ClipboardEnvironment environment)
    {
        var list = await environment.Run("wl-paste", ["--list-types"], new(TimeoutMs: DefaultListTimeoutMs)).ConfigureAwait(false);
        if (list is null) return (false, null);
        var selectedType = SelectPreferredImageMimeType(SplitLines(list));
        if (selectedType is null) return (true, null);
        var data = await environment.Run("wl-paste", ["--type", selectedType, "--no-newline"], null).ConfigureAwait(false);
        if (data is null) return (false, null);
        if (data.Length == 0) return (true, null);
        return (true, new(data, BaseMimeType(selectedType)));
    }

    /// <summary>On WSL, the Linux clipboard (Wayland/X11) does not receive image data from Windows screenshots (Win+Shift+S).
    /// PowerShell can access the Windows clipboard directly, so it is the fallback.</summary>
    private static async Task<ClipboardImage?> ReadClipboardImageViaPowerShell(ClipboardEnvironment environment)
    {
        var tmpFile = Path.Combine(environment.TempDirectory, $"pi-wsl-clip-{Guid.NewGuid()}.png");
        try
        {
            var winPathResult = await environment.Run("wslpath", ["-w", tmpFile], new(TimeoutMs: DefaultListTimeoutMs)).ConfigureAwait(false);
            if (winPathResult is null) return null;
            var winPath = PiSharp.Tui.Pi.TextUtils.JsTrim(Encoding.UTF8.GetString(winPathResult));
            if (winPath.Length == 0) return null;
            var psQuotedWinPath = winPath.Replace("'", "''", StringComparison.Ordinal);
            var psScript = string.Join("; ",
                "Add-Type -AssemblyName System.Windows.Forms",
                "Add-Type -AssemblyName System.Drawing",
                $"$path = '{psQuotedWinPath}'",
                "$img = [System.Windows.Forms.Clipboard]::GetImage()",
                "if ($img) { $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); Write-Output 'ok' } else { Write-Output 'empty' }");
            var result = await environment.Run("powershell.exe", ["-NoProfile", "-Command", psScript], new(TimeoutMs: DefaultPowerShellTimeoutMs)).ConfigureAwait(false);
            if (result is null) return null;
            if (PiSharp.Tui.Pi.TextUtils.JsTrim(Encoding.UTF8.GetString(result)) != "ok") return null;
            var bytes = await File.ReadAllBytesAsync(tmpFile).ConfigureAwait(false);
            return bytes.Length == 0 ? null : new(bytes, "image/png");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(tmpFile); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<(bool Available, ClipboardImage? Image)> ReadClipboardImageViaXclip(ClipboardEnvironment environment)
    {
        var targets = await environment.Run("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"], new(TimeoutMs: DefaultListTimeoutMs)).ConfigureAwait(false);
        if (targets is null) return (false, null);
        var preferred = SelectPreferredImageMimeType(SplitLines(targets));
        if (preferred is null) return (true, null);
        var data = await environment.Run("xclip", ["-selection", "clipboard", "-t", preferred, "-o"], null).ConfigureAwait(false);
        if (data is null) return (false, null);
        if (data.Length == 0) return (true, null);
        return (true, new(data, BaseMimeType(preferred)));
    }

    private static async Task<(bool Available, ClipboardImage? Image)> ReadClipboardImageViaNativeClipboard(ClipboardEnvironment environment)
    {
        var native = environment.GetNativeClipboard();
        if (native is null) return (false, null);
        var (available, bytes) = await native.GetImageAsync().ConfigureAwait(false);
        if (!available) return (false, null);
        if (bytes is not { Length: > 0 }) return (true, null);
        return (true, new(bytes, ImageMime.DetectSupportedImageMimeType(bytes) ?? "application/octet-stream"));
    }

    /// <summary>Source <c>readClipboardImage</c>. Native transfer errors propagate without fallback.</summary>
    public static async Task<ClipboardImage?> ReadClipboardImage(ClipboardEnvironment? environment = null)
    {
        environment ??= ClipboardEnvironment.Default;
        if (environment.HasEnv("TERMUX_VERSION")) return null;

        (bool Available, ClipboardImage? Image) image = (false, null);
        if (environment.Platform == "linux")
        {
            var wsl = Wsl.IsWSL(environment.Env, environment.ReadProcVersion);
            if (IsWaylandSession(environment.Env) || wsl) image = await ReadClipboardImageViaWlPaste(environment).ConfigureAwait(false);
            if (!image.Available) image = await ReadClipboardImageViaXclip(environment).ConfigureAwait(false);
            // Preserve Linux's empty/unavailable distinction if Windows has no image.
            if (image.Image is null && wsl && await ReadClipboardImageViaPowerShell(environment).ConfigureAwait(false) is { } windows)
                image = (true, windows);
            if (!image.Available) image = await ReadClipboardImageViaNativeClipboard(environment).ConfigureAwait(false);
        }
        else
        {
            image = await ReadClipboardImageViaNativeClipboard(environment).ConfigureAwait(false);
        }

        if (image.Image is not { } found) return null;

        // Convert unsupported formats (e.g., Windows DIB data wrapped as BMP) to PNG.
        if (!IsSupportedImageMimeType(found.MimeType))
        {
            var pngBytes = ConvertToPng(environment, found.Bytes, found.MimeType);
            return pngBytes is null ? null : new(pngBytes, "image/png");
        }
        return found;
    }
}
