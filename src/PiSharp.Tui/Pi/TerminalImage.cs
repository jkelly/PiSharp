// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/terminal-image.ts and packages/tui/src/terminal-colors.ts.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

public enum ImageProtocol { None, Kitty, ITerm2 }
public sealed record TerminalCapabilities(ImageProtocol Images, bool TrueColor, bool Hyperlinks);
public readonly record struct CellDimensions(int WidthPx, int HeightPx);
public readonly record struct ImageDimensions(int WidthPx, int HeightPx);
public sealed record ImageRenderOptions(int? MaxWidthCells = null, int? MaxHeightCells = null, bool? PreserveAspectRatio = null, int? ImageId = null, bool? MoveCursor = null);
/// <summary>Partial overrides for detected capabilities (the <c>terminal</c> settings).</summary>
public sealed record TerminalCapabilityOverrides(ImageProtocol? Images = null, bool? TrueColor = null, bool? Hyperlinks = null);

/// <summary>Terminal capability detection and inline image encoding (Kitty graphics and iTerm2).</summary>
public static partial class TerminalImage
{
    private static readonly object Gate = new();
    private static TerminalCapabilities? cached;
    private static TerminalCapabilityOverrides overrides = new();
    private static CellDimensions cellDimensions = new(9, 18);
    /// <summary>Environment lookup used for detection (tests substitute it).</summary>
    public static Func<string, string?> Environment { get; set; } = System.Environment.GetEnvironmentVariable;

    public static CellDimensions GetCellDimensions() { lock (Gate) return cellDimensions; }
    public static void SetCellDimensions(CellDimensions value) { lock (Gate) cellDimensions = value; }

    private static bool ProbeTmuxHyperlinks()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("tmux", ["display-message", "-p", "#{client_termfeatures}"])
            { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false });
            if (process is null) return false;
            process.StandardInput.Close();
            if (!process.WaitForExit(250)) { try { process.Kill(); } catch { } return false; }
            return process.StandardOutput.ReadToEnd().Split(',').Select(feature => feature.Trim()).Contains("hyperlinks");
        }
        catch { return false; }
    }

    private static TerminalCapabilities DetectFromEnvironment(Func<bool> tmuxForwardsHyperlinks)
    {
        string Env(string name) => (Environment(name) ?? "").ToLowerInvariant();
        bool Has(string name) => !string.IsNullOrEmpty(Environment(name));
        var termProgram = Env("TERM_PROGRAM"); var terminalEmulator = Env("TERMINAL_EMULATOR"); var term = Env("TERM"); var colorTerm = Env("COLORTERM");
        var trueColorHint = colorTerm is "truecolor" or "24bit" || term.EndsWith("-direct", StringComparison.Ordinal);
        if (Has("TMUX") || term.StartsWith("tmux", StringComparison.Ordinal)) return new(ImageProtocol.None, trueColorHint, tmuxForwardsHyperlinks());
        if (term.StartsWith("screen", StringComparison.Ordinal)) return new(ImageProtocol.None, trueColorHint, false);
        if (termProgram == "herdr") return new(ImageProtocol.None, trueColorHint, true);
        if (Has("KITTY_WINDOW_ID") || termProgram == "kitty") return new(ImageProtocol.Kitty, true, true);
        if (termProgram == "ghostty" || term.Contains("ghostty", StringComparison.Ordinal) || Has("GHOSTTY_RESOURCES_DIR")) return new(ImageProtocol.Kitty, true, true);
        if (Has("WEZTERM_PANE") || termProgram == "wezterm") return new(ImageProtocol.Kitty, true, true);
        if (termProgram == "warpterminal" || Has("WARP_SESSION_ID") || Has("WARP_TERMINAL_SESSION_UUID")) return new(ImageProtocol.Kitty, true, true);
        if (Has("ITERM_SESSION_ID") || termProgram == "iterm.app") return new(ImageProtocol.ITerm2, true, true);
        if (Has("WT_SESSION")) return new(ImageProtocol.None, true, true);
        if (termProgram is "alacritty" or "vscode" or "zed") return new(ImageProtocol.None, true, true);
        if (terminalEmulator == "jetbrains-jediterm") return new(ImageProtocol.None, true, false);
        if (OperatingSystem.IsWindows()) return new(ImageProtocol.None, true, false);
        return new(ImageProtocol.None, trueColorHint, false);
    }

    private static bool? BooleanOverride(string? value) => value == "1" ? true : value == "0" ? false : null;

    public static TerminalCapabilities DetectCapabilities(Func<bool>? tmuxForwardsHyperlinks = null)
    {
        var hyperlinks = BooleanOverride(Environment("PI_HYPERLINKS"));
        var detected = DetectFromEnvironment(hyperlinks is { } forced ? () => forced : tmuxForwardsHyperlinks ?? ProbeTmuxHyperlinks);
        var protocol = Environment("PI_IMAGE_PROTOCOL")?.ToLowerInvariant();
        ImageProtocol? images = protocol switch { "kitty" => ImageProtocol.Kitty, "iterm2" => ImageProtocol.ITerm2, "none" or "0" => ImageProtocol.None, _ => null };
        var trueColor = BooleanOverride(Environment("PI_TRUE_COLOR"));
        return new(images ?? detected.Images, trueColor ?? detected.TrueColor, hyperlinks ?? detected.Hyperlinks);
    }

    public static TerminalCapabilities GetCapabilities()
    {
        lock (Gate)
        {
            if (cached is null)
            {
                var detected = DetectCapabilities(overrides.Hyperlinks is { } h ? () => h : null);
                cached = new(overrides.Images ?? detected.Images, overrides.TrueColor ?? detected.TrueColor, overrides.Hyperlinks ?? detected.Hyperlinks);
            }
            return cached;
        }
    }
    public static void ResetCapabilitiesCache() { lock (Gate) cached = null; }
    public static void SetCapabilityOverrides(TerminalCapabilityOverrides value) { lock (Gate) { if (overrides == value) return; overrides = value; cached = null; } }
    public static void SetCapabilities(TerminalCapabilities value) { lock (Gate) cached = value; }

    private const string KittyPrefix = "\u001b_G", ITerm2Prefix = "\u001b]1337;File=";
    public static bool IsImageLine(string line) => line.Contains(KittyPrefix, StringComparison.Ordinal) || line.Contains(ITerm2Prefix, StringComparison.Ordinal);

    public static int AllocateImageId() => Random.Shared.Next(1, int.MaxValue);

    public static string EncodeKitty(string base64, int? columns = null, int? rows = null, int? imageId = null, bool? moveCursor = null)
    {
        const int chunkSize = 4096;
        var parameters = new List<string> { "a=T", "f=100", "q=2" };
        if (moveCursor == false) parameters.Add("C=1");
        if (columns is > 0) parameters.Add("c=" + columns.Value.ToString(CultureInfo.InvariantCulture));
        if (rows is > 0) parameters.Add("r=" + rows.Value.ToString(CultureInfo.InvariantCulture));
        if (imageId is > 0) parameters.Add("i=" + imageId.Value.ToString(CultureInfo.InvariantCulture));
        if (base64.Length <= chunkSize) return "\u001b_G" + string.Join(',', parameters) + ";" + base64 + "\u001b\\";
        var result = new StringBuilder(); var offset = 0; var first = true;
        while (offset < base64.Length)
        {
            var chunk = base64.Substring(offset, Math.Min(chunkSize, base64.Length - offset));
            var last = offset + chunkSize >= base64.Length;
            if (first) { result.Append("\u001b_G").Append(string.Join(',', parameters)).Append(",m=1;").Append(chunk).Append("\u001b\\"); first = false; }
            else if (last) result.Append("\u001b_Gm=0;").Append(chunk).Append("\u001b\\");
            else result.Append("\u001b_Gm=1;").Append(chunk).Append("\u001b\\");
            offset += chunkSize;
        }
        return result.ToString();
    }

    public static string DeleteKittyImage(long imageId) => "\u001b_Ga=d,d=I,i=" + imageId.ToString(CultureInfo.InvariantCulture) + ",q=2\u001b\\";
    public static string DeleteAllKittyImages() => "\u001b_Ga=d,d=A,q=2\u001b\\";
    public static string DeleteAllKittyPlacements() => "\u001b_Ga=d,d=a,q=2\u001b\\";

    private static int Base64ByteLength(string base64)
    {
        var length = base64.TrimEnd('=').Length;
        return length * 3 / 4;
    }

    public static string EncodeITerm2(string base64, string? width = null, string? height = null, string? name = null, bool preserveAspectRatio = true, bool inline = true)
    {
        var parameters = new List<string> { "inline=" + (inline ? "1" : "0"), "size=" + Base64ByteLength(base64).ToString(CultureInfo.InvariantCulture) };
        if (width is not null) parameters.Add("width=" + width);
        if (height is not null) parameters.Add("height=" + height);
        if (!string.IsNullOrEmpty(name)) parameters.Add("name=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name)));
        if (!preserveAspectRatio) parameters.Add("preserveAspectRatio=0");
        return "\u001b]1337;File=" + string.Join(';', parameters) + ":" + base64 + "\u0007";
    }

    public readonly record struct ImageCellSize(int Columns, int Rows);
    public sealed record KittyImageMetadata(long ImageId, int Columns, int Rows, int WidthPx, int HeightPx);
    private sealed record RegisteredKitty(KittyImageMetadata Metadata, long Generation);
    private static readonly Dictionary<long, RegisteredKitty> KittyMetadata = new();
    private static readonly LinkedList<long> KittyOrder = new();
    private static long kittyGeneration;

    public static void RegisterKittyImageMetadata(KittyImageMetadata metadata)
    {
        lock (Gate)
        {
            kittyGeneration++;
            if (KittyMetadata.Remove(metadata.ImageId)) KittyOrder.Remove(metadata.ImageId);
            KittyMetadata[metadata.ImageId] = new(metadata, kittyGeneration); KittyOrder.AddLast(metadata.ImageId);
            if (KittyMetadata.Count > 1000 && KittyOrder.First is { } oldest) { KittyMetadata.Remove(oldest.Value); KittyOrder.RemoveFirst(); }
        }
    }

    [GeneratedRegex(@"(?:^|,)i=(\d+)(?:,|$)")] private static partial Regex KittyIdControl();
    [GeneratedRegex(@"(?:^|,)r=(\d+)(?:,|$)")] private static partial Regex KittyRowsControl();
    [GeneratedRegex("\u001b_G([^;]*);")] private static partial Regex KittyControls();
    private static RegisteredKitty? RegisteredFromControls(string controls)
    {
        var match = KittyIdControl().Match(controls);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var id)) return null;
        lock (Gate) return KittyMetadata.TryGetValue(id, out var value) ? value : null;
    }
    public static KittyImageMetadata? GetKittyImageMetadata(string line)
    {
        var match = KittyControls().Match(line);
        return match.Success ? RegisteredFromControls(match.Groups[1].Value)?.Metadata : null;
    }
    private static int? ExplicitRows(string controls)
    {
        var match = KittyRowsControl().Match(controls);
        return match.Success && int.TryParse(match.Groups[1].Value, out var rows) && rows > 0 ? rows : null;
    }
    public static int? GetKittyImagePlacementRows(string line)
    {
        var match = KittyControls().Match(line);
        if (!match.Success) return null;
        return ExplicitRows(match.Groups[1].Value) ?? RegisteredFromControls(match.Groups[1].Value)?.Metadata.Rows;
    }

    public sealed record KittyImagePlacement(long ImageId, long TransmissionGeneration, int TransmissionBytes, long EstimatedDecodedBytes, int Rows, string Sequence, string ReplacementLine);
    private static readonly HashSet<string> PlacementKeys = ["i", "p", "x", "y", "w", "h", "X", "Y", "c", "r", "C", "U", "z", "P", "Q", "H", "V"];
    [GeneratedRegex(@"(?:^|,)m=1(?:,|$)")] private static partial Regex MoreChunks();
    public static KittyImagePlacement? GetKittyImagePlacement(string line)
    {
        var match = KittyControls().Match(line);
        if (!match.Success) return null;
        var registered = RegisteredFromControls(match.Groups[1].Value);
        if (registered is null) return null;
        var commandStart = match.Index; var commandControls = match.Groups[1].Value; int transmissionEnd;
        while (true)
        {
            var terminator = line.IndexOf("\u001b\\", commandStart + KittyPrefix.Length, StringComparison.Ordinal);
            if (terminator == -1) return null;
            transmissionEnd = terminator + 2;
            if (!MoreChunks().IsMatch(commandControls)) break;
            commandStart = transmissionEnd;
            if (!line.AsSpan(commandStart).StartsWith(KittyPrefix)) return null;
            var controlsEnd = line.IndexOf(';', commandStart + KittyPrefix.Length);
            if (controlsEnd == -1) return null;
            commandControls = line[(commandStart + KittyPrefix.Length)..controlsEnd];
        }
        var controls = match.Groups[1].Value.Split(',').Where(control => PlacementKeys.Contains(control.Split('=', 2)[0]));
        var sequence = "\u001b_Ga=p,q=2," + string.Join(',', controls) + "\u001b\\";
        var metadata = registered.Metadata;
        return new(metadata.ImageId, registered.Generation, transmissionEnd - match.Index, (long)metadata.WidthPx * metadata.HeightPx * 4,
            ExplicitRows(match.Groups[1].Value) ?? metadata.Rows, sequence, line[..match.Index] + sequence + line[transmissionEnd..]);
    }

    public static string CropKittyImageLine(string line, int hiddenRows, int visibleRows)
    {
        var metadata = GetKittyImageMetadata(line);
        var match = KittyControls().Match(line);
        if (metadata is null || !match.Success || hiddenRows < 0 || hiddenRows >= metadata.Rows || visibleRows <= 0) return line;
        var croppedRows = Math.Min(visibleRows, metadata.Rows - hiddenRows);
        if (hiddenRows == 0 && croppedRows == metadata.Rows) return line;
        var sourceY = (int)Math.Floor((double)metadata.HeightPx * hiddenRows / metadata.Rows);
        var sourceEnd = (int)Math.Ceiling((double)metadata.HeightPx * (hiddenRows + croppedRows) / metadata.Rows);
        var sourceHeight = Math.Max(1, Math.Min(metadata.HeightPx, sourceEnd) - sourceY);
        var controls = match.Groups[1].Value.Split(',').Where(control => !(control.Length >= 2 && control[1] == '=' && control[0] is 'y' or 'h' or 'r')).ToList();
        controls.Add("y=" + sourceY.ToString(CultureInfo.InvariantCulture)); controls.Add("h=" + sourceHeight.ToString(CultureInfo.InvariantCulture));
        controls.Add("r=" + croppedRows.ToString(CultureInfo.InvariantCulture));
        return line[..match.Index] + "\u001b_G" + string.Join(',', controls) + ";" + line[(match.Index + match.Length)..];
    }

    private static int LessDistorted(int upper, double ideal)
    {
        if (upper <= 1) return upper;
        var lower = upper - 1;
        var upperDistortion = Math.Max(upper / ideal, ideal / upper);
        var lowerDistortion = Math.Max(lower / ideal, ideal / lower);
        return lowerDistortion < upperDistortion ? lower : upper;
    }

    public static ImageCellSize CalculateImageCellSize(ImageDimensions image, double maxWidthCells, double? maxHeightCells = null, CellDimensions? cell = null, bool optimizeAspectRatio = false)
    {
        var cells = cell ?? new CellDimensions(9, 18);
        var maxWidth = Math.Max(1, (int)Math.Floor(maxWidthCells));
        int? maxHeight = maxHeightCells is { } mh ? Math.Max(1, (int)Math.Floor(mh)) : null;
        var imageWidth = Math.Max(1, image.WidthPx); var imageHeight = Math.Max(1, image.HeightPx);
        var widthScale = (double)maxWidth * cells.WidthPx / imageWidth;
        var heightScale = maxHeight is { } h ? (double)h * cells.HeightPx / imageHeight : widthScale;
        var scale = Math.Min(widthScale, heightScale);
        var columns = Math.Max(1, Math.Min(maxWidth, (int)Math.Ceiling(imageWidth * scale / cells.WidthPx)));
        var rows = Math.Max(1, (int)Math.Ceiling(imageHeight * scale / cells.HeightPx));
        if (maxHeight is { } limit) rows = Math.Min(limit, rows);
        if (!optimizeAspectRatio) return new(columns, rows);
        if (widthScale <= heightScale) rows = LessDistorted(rows, (double)columns * cells.WidthPx * imageHeight / ((double)imageWidth * cells.HeightPx));
        else columns = LessDistorted(columns, (double)rows * cells.HeightPx * imageWidth / ((double)imageHeight * cells.WidthPx));
        return new(columns, rows);
    }

    public static int CalculateImageRows(ImageDimensions image, int targetWidthCells, CellDimensions? cell = null) =>
        CalculateImageCellSize(image, targetWidthCells, null, cell).Rows;

    private static byte[]? Decode(string base64) { try { return Convert.FromBase64String(base64); } catch (FormatException) { return null; } }

    public static ImageDimensions? GetPngDimensions(string base64)
    {
        var b = Decode(base64);
        if (b is null || b.Length < 24 || b[0] != 0x89 || b[1] != 0x50 || b[2] != 0x4e || b[3] != 0x47) return null;
        return new((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20)));
    }
    public static ImageDimensions? GetJpegDimensions(string base64)
    {
        var b = Decode(base64);
        if (b is null || b.Length < 2 || b[0] != 0xff || b[1] != 0xd8) return null;
        var offset = 2;
        while (offset < b.Length - 9)
        {
            if (b[offset] != 0xff) { offset++; continue; }
            var marker = b[offset + 1];
            if (marker is >= 0xc0 and <= 0xc2)
                return new(BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset + 7)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset + 5)));
            if (offset + 3 >= b.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset + 2));
            if (length < 2) return null;
            offset += 2 + length;
        }
        return null;
    }
    public static ImageDimensions? GetGifDimensions(string base64)
    {
        var b = Decode(base64);
        if (b is null || b.Length < 10) return null;
        var signature = Encoding.ASCII.GetString(b, 0, 6);
        if (signature is not ("GIF87a" or "GIF89a")) return null;
        return new(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)));
    }
    public static ImageDimensions? GetWebpDimensions(string base64)
    {
        var b = Decode(base64);
        if (b is null || b.Length < 30 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WEBP") return null;
        var chunk = Encoding.ASCII.GetString(b, 12, 4);
        if (chunk == "VP8 ") return new(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26)) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28)) & 0x3fff);
        if (chunk == "VP8L") { var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21)); return new((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1); }
        if (chunk == "VP8X") return new((b[24] | b[25] << 8 | b[26] << 16) + 1, (b[27] | b[28] << 8 | b[29] << 16) + 1);
        return null;
    }
    public static ImageDimensions? GetImageDimensions(string base64, string mimeType) => mimeType switch
    {
        "image/png" => GetPngDimensions(base64), "image/jpeg" => GetJpegDimensions(base64),
        "image/gif" => GetGifDimensions(base64), "image/webp" => GetWebpDimensions(base64), _ => null
    };

    public sealed record RenderedImage(string Sequence, int Columns, int Rows, int? ImageId);
    public static RenderedImage? RenderImage(string base64, ImageDimensions dimensions, ImageRenderOptions? options = null)
    {
        options ??= new();
        var caps = GetCapabilities();
        if (caps.Images == ImageProtocol.None) return null;
        var size = CalculateImageCellSize(dimensions, options.MaxWidthCells ?? 80, options.MaxHeightCells, GetCellDimensions(), caps.Images == ImageProtocol.Kitty);
        if (caps.Images == ImageProtocol.Kitty)
        {
            if (options.ImageId is { } id) RegisterKittyImageMetadata(new(id, size.Columns, size.Rows, dimensions.WidthPx, dimensions.HeightPx));
            return new(EncodeKitty(base64, size.Columns, size.Rows, options.ImageId, options.MoveCursor), size.Columns, size.Rows, options.ImageId);
        }
        return new(EncodeITerm2(base64, size.Columns.ToString(CultureInfo.InvariantCulture), "auto", preserveAspectRatio: options.PreserveAspectRatio ?? true), size.Columns, size.Rows, null);
    }

    /// <summary>Wraps text in an OSC 8 hyperlink.</summary>
    public static string Hyperlink(string text, string url) => "\u001b]8;;" + url + "\u001b\\" + text + "\u001b]8;;\u001b\\";

    private static string ShortenImagePath(string filename)
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && (filename == home || filename.StartsWith(home + "/", StringComparison.Ordinal) || filename.StartsWith(home + "\\", StringComparison.Ordinal)))
            return "~" + filename[home.Length..];
        return filename;
    }

    /// <summary>Text shown when the terminal cannot render an inline image.</summary>
    public static string ImageFallback(string mimeType, ImageDimensions? dimensions = null, string? filename = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(filename))
        {
            var display = ShortenImagePath(filename);
            parts.Add(GetCapabilities().Hyperlinks && Path.IsPathRooted(filename) ? Hyperlink(display, new Uri(filename).AbsoluteUri) : display);
        }
        parts.Add("[" + mimeType + "]");
        if (dimensions is { } d) parts.Add(d.WidthPx.ToString(CultureInfo.InvariantCulture) + "x" + d.HeightPx.ToString(CultureInfo.InvariantCulture));
        return "[Image: " + string.Join(' ', parts) + "]";
    }
}

public readonly record struct RgbColor(int R, int G, int B);
public enum TerminalColorScheme { Dark, Light }
/// <summary>Colors the terminal reports for its theme: OSC 10/11 defaults and the 16-color palette when complete.</summary>
public sealed record TerminalColors(RgbColor? Foreground, RgbColor? Background, IReadOnlyList<RgbColor>? Palette);

public static partial class TerminalColorParsing
{
    [GeneratedRegex(@"^\x1b\](?:(1[01])|4;(\d{1,3}));([^\x07\x1b]*)(?:\x07|\x1b\\)$", RegexOptions.IgnoreCase)] private static partial Regex OscColor();
    [GeneratedRegex(@"^(?:\x1b\[\?997;(1|2)n)+$")] private static partial Regex SchemeReport();

    /// <summary>The OSC 10/11/4 target ("foreground", "background" or the palette index) and color of a reply.</summary>
    public static (object Target, RgbColor? Rgb)? ParseOscColorResponse(string data)
    {
        var match = OscColor().Match(data);
        if (!match.Success) return null;
        object target = match.Groups[1].Value == "10" ? "foreground" : match.Groups[1].Value == "11" ? "background" : int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return (target, ParseColorValue(match.Groups[3].Value));
    }

    private static int? HexChannel(string channel)
    {
        if (channel.Length == 0 || channel.Length > 7 || channel.Any(c => !Uri.IsHexDigit(c))) return null;
        var max = Math.Pow(16, channel.Length) - 1;
        return (int)Math.Round(Convert.ToInt64(channel, 16) / max * 255, MidpointRounding.AwayFromZero);
    }

    private static RgbColor? ParseColorValue(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 6 && hex.All(Uri.IsHexDigit)) return new(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16));
            if (hex.Length == 12 && hex.All(Uri.IsHexDigit))
            {
                var r = HexChannel(hex[..4]); var g = HexChannel(hex[4..8]); var b = HexChannel(hex[8..12]);
                return r is not null && g is not null && b is not null ? new(r.Value, g.Value, b.Value) : null;
            }
            return null;
        }
        var rgb = Regex.Replace(value, "^rgba?:", "", RegexOptions.IgnoreCase).Split('/');
        if (rgb.Length < 3) return null;
        var red = HexChannel(rgb[0]); var green = HexChannel(rgb[1]); var blue = HexChannel(rgb[2]);
        return red is not null && green is not null && blue is not null ? new(red.Value, green.Value, blue.Value) : null;
    }

    public static TerminalColorScheme? ParseTerminalColorSchemeReport(string data)
    {
        var match = SchemeReport().Match(data);
        if (!match.Success) return null;
        var last = match.Groups[1].Captures[^1].Value;
        return last == "2" ? TerminalColorScheme.Light : TerminalColorScheme.Dark;
    }
}
