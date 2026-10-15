// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/tools/image-processor.ts
// toBase64 is upstream's own encoder because it may not use Buffer or btoa; .NET's Convert.ToBase64String is the same standard base64.
using System.Collections.Frozen;

namespace PiSharp.Durable.Tools;

/// <summary>Limits an image the model sees must keep (upstream <c>ImageLimits</c>); the defaults are the coding agent's.</summary>
/// <param name="MaxWidth">Pixels. Default 2000.</param>
/// <param name="MaxHeight">Pixels. Default 2000.</param>
/// <param name="MaxBytes">Bytes of base64. Default 4.5 MiB, below Anthropic's 5 MB.</param>
public sealed record ImageLimits(int MaxWidth, int MaxHeight, int MaxBytes)
{
    /// <summary>Upstream <c>DEFAULT_IMAGE_LIMITS</c>: 2000x2000 pixels and 4.5 MiB of base64.</summary>
    public static ImageLimits Default { get; } = new(2000, 2000, 4_718_592); // 4.5 * 1024 * 1024
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

    /// <summary>Upstream <c>base64Length</c>: the length of the base64 of <paramref name="byteLength"/> bytes.</summary>
    public static long Base64Length(long byteLength) => (byteLength + 2) / 3 * 4;
}
