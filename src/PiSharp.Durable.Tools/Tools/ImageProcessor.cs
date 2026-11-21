// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/src/tools/image-processor.ts
// toBase64 is btoa over a binary string built 8 KiB at a time (v1.3.0: so a 3 MiB image no longer takes about 140 MB of V8 heap); .NET's
// Convert.ToBase64String is the same standard base64, sized once, with no intermediate string.
// Pi v1.6.0: imageLimits(model) is ImageLimits.Of (and CodingTools.ImageLimitsOf, the `/tools` export); preparedNotes is
// ImageFormats.PreparedNotes; fromBase64 is ImageFormats.FromBase64, which decodes as `atob` does (the forgiving-base64 decode of the
// HTML standard: ASCII whitespace is skipped and padding is optional) and throws a FormatException with atob's message for anything else,
// where Convert.FromBase64String alone would refuse unpadded data that atob takes.
using System.Collections.Frozen;
using System.Globalization;

namespace PiSharp.Durable.Tools;

/// <summary>Limits an image the model sees must keep (upstream <c>ImageLimits</c>); the defaults are the coding agent's.</summary>
/// <param name="MaxWidth">Pixels. Default 2000.</param>
/// <param name="MaxHeight">Pixels. Default 2000.</param>
/// <param name="MaxBytes">Bytes of base64. Default 4.5 MiB, below Anthropic's 5 MB.</param>
public sealed record ImageLimits(int MaxWidth, int MaxHeight, int MaxBytes)
{
    /// <summary>Upstream <c>DEFAULT_IMAGE_LIMITS</c>: 2000x2000 pixels and 4.5 MiB of base64.</summary>
    public static ImageLimits Default { get; } = new(2000, 2000, 4_718_592); // 4.5 * 1024 * 1024

    /// <summary>The limits of images sent to <paramref name="model"/>: its <c>inputLimits.images.resize</c>, each absent one
    /// <see cref="Default"/>'s (upstream <c>imageLimits</c>). Without a model, the defaults.</summary>
    public static ImageLimits Of(DurableModel? model)
    {
        var resize = model?.InputLimits?.Images?.Resize;
        return new ImageLimits(resize?.MaxWidth ?? Default.MaxWidth, resize?.MaxHeight ?? Default.MaxHeight, resize?.MaxBytes ?? Default.MaxBytes);
    }
}

/// <summary>An image the model can take: a supported format, within the limits, as base64 (upstream <c>PreparedImage</c>).</summary>
/// <param name="Data">Standard base64.</param>
/// <param name="MimeType">The format of <paramref name="Data"/>.</param>
public sealed record PreparedImage(string Data, string MimeType)
{
    /// <summary>Set when the image was resized: its size before and after, for the model to map coordinates back.</summary>
    public ImageResize? Resized { get; init; }
    /// <summary>The original format, set when it differs from <see cref="MimeType"/>: BMP becomes PNG, a large PNG may become JPEG.</summary>
    public string? ConvertedFrom { get; init; }
}

/// <summary>An image's size before and after a resize.</summary>
public sealed record ImageResize(ImageSize From, ImageSize To);

/// <summary>Width and height in pixels.</summary>
public readonly record struct ImageSize(int Width, int Height);

/// <summary>
/// Decodes, orients, resizes, and re-encodes images so the model can take them (upstream <c>ImageProcessor</c>). <c>read</c> uses one when
/// given; without one, it passes supported images within the limits through as they are. <see cref="Images.ImageProcessors"/> provides one
/// over an image codec.
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// The image, prepared to fit <paramref name="limits"/>, or null when it cannot be decoded or made to fit. A task, so a processor can do
    /// the work off the calling thread.
    /// </summary>
    Task<PreparedImage?> PrepareAsync(ReadOnlyMemory<byte> bytes, string mimeType, ImageLimits limits);
}

/// <summary>The helpers of image-processor.ts.</summary>
internal static class ImageFormats
{
    /// <summary>Upstream <c>INLINE_IMAGE_TYPES</c>: formats every provider takes inline; others, such as BMP, need converting.</summary>
    public static readonly FrozenSet<string> Inline = FrozenSet.Create(StringComparer.Ordinal, "image/png", "image/jpeg", "image/gif", "image/webp");

    /// <summary>Upstream <c>toBase64</c>: standard base64.</summary>
    public static string ToBase64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

    /// <summary>What preparing changed, for the model: the format, and the size with the factor that maps coordinates back (upstream
    /// <c>preparedNotes</c>).</summary>
    public static List<string> PreparedNotes(PreparedImage image)
    {
        var notes = new List<string>();
        if (image.ConvertedFrom is { } convertedFrom) notes.Add($"Converted from {convertedFrom} to {image.MimeType}.");
        if (image.Resized is { } resized)
        {
            var (from, to) = (resized.From, resized.To);
            // toFixed(2).
            var scale = ((double)from.Width / to.Width).ToString("F2", CultureInfo.InvariantCulture);
            notes.Add($"Resized from {from.Width}x{from.Height} to {to.Width}x{to.Height}. Multiply coordinates by {scale} to map them to the original.");
        }
        return notes;
    }

    /// <summary>
    /// Upstream <c>fromBase64</c>: the bytes of standard base64 <paramref name="data"/>, decoded as <c>atob</c> decodes. ASCII whitespace is
    /// skipped, one or two trailing <c>=</c> are optional, and anything else that is not base64 (another character, padding in the middle, a
    /// length of 1 modulo 4) throws a <see cref="FormatException"/> with atob's message.
    /// </summary>
    public static byte[] FromBase64(string data)
    {
        const string malformed = "The string to be decoded is not correctly encoded.";
        var text = new System.Text.StringBuilder(data.Length);
        foreach (var character in data)
            if (character is not ('\t' or '\n' or '\f' or '\r' or ' ')) text.Append(character);
        if (text.Length % 4 == 0)
        {
            if (text.Length > 0 && text[^1] == '=') text.Length--;
            if (text.Length > 0 && text[^1] == '=') text.Length--;
        }
        if (text.Length % 4 == 1) throw new FormatException(malformed);
        for (var index = 0; index < text.Length; index++)
            if (!(char.IsAsciiLetterOrDigit(text[index]) || text[index] is '+' or '/')) throw new FormatException(malformed);
        text.Append('=', (4 - text.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(text.ToString());
        }
        catch (FormatException)
        {
            throw new FormatException(malformed);
        }
    }

    /// <summary>Upstream <c>base64Length</c>: the length of the base64 of <paramref name="byteLength"/> bytes.</summary>
    public static long Base64Length(long byteLength) => (byteLength + 2) / 3 * 4;
}
