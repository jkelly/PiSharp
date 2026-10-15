// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/images/index.ts
// Upstream prepares images with Photon (Rust compiled to WebAssembly). PiSharp has no Photon: the decode, orient, resample and encode steps
// are PiSharp.Tools' image codec seam (IImageCodec), the backend the coding agent's read tool already uses in place of Photon. The host picks
// the codec: PiSharp.Tools.Skia's SkiaImageCodec decodes what Photon decodes (PNG, JPEG, GIF's first frame, WebP, BMP) and encodes PNG and
// JPEG; PiSharp.Tools' dependency-free BuiltinImageCodec decodes only PNG and BMP and encodes only PNG. Mapping:
// - PhotonImage.new_from_byteslice (a decode failure is undefined): IImageCodec.ProbeDimensions, which decodes the image with Skia (and
//   reads only the header with the built-in codec), oriented;
// - exifOrientation decides the pass-through, as upstream; the codec turns the pixels upright with PiSharp.Tools' ExifOrientation, the
//   coding agent's reader of the same tag (fliph/flipv and the transposes of orientations 5 to 8);
// - resize(..., SamplingFilter.Lanczos3): the codec's Lanczos3 resampling (PiSharp.Tools RasterImage, Photon's filter);
// - get_bytes() and get_bytes_jpeg(quality): IImageCodec.Encode, which returns PNG and JPEG at each requested quality (JPEG_QUALITIES) for one
//   size; they are tried in upstream's order (PNG first, or the JPEG qualities first for a JPEG source).
// Photon's memory notes (WebAssembly memory that never shrinks, free()) have no counterpart: the codec's buffers are managed memory.
using PiSharp.Tools.Images;

namespace PiSharp.Durable.Tools.Images;

/// <summary>Image processors for <c>CodingTools.CreateCodingTools(new ReadToolOptions { Images = ... })</c> (upstream <c>createPhotonImages</c>
/// and <c>createNodePhotonImages</c>).</summary>
public static class ImageProcessors
{
    /// <summary>
    /// An <see cref="IImageProcessor"/> over <paramref name="codec"/> (upstream <c>createPhotonImages</c>). An image already upright, in a
    /// format providers take inline, and within the limits is returned as it is, once it decodes. Otherwise it is turned upright by its EXIF
    /// orientation, scaled to the maximum width and height, and encoded as PNG or JPEG at falling qualities (JPEG first for JPEG sources),
    /// then at smaller dimensions, 3/4 at a time, until the base64 fits. Formats providers do not take inline (BMP) become PNG. The work runs
    /// on the calling thread; <see cref="CreateThreaded"/> moves it to a worker thread.
    /// </summary>
    public static IImageProcessor Create(IImageCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new CodecImageProcessor(codec);
    }

    /// <summary>
    /// <see cref="Create"/> on a worker thread that exits after <see cref="ThreadedImagesOptions.IdleMs"/> without images (upstream
    /// <c>createNodePhotonImages</c>; see Node.cs).
    /// </summary>
    public static IImageProcessor CreateThreaded(IImageCodec codec, ThreadedImagesOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return options?.Worker == false ? Create(codec) : new ThreadedImageProcessor(codec, options?.IdleMs ?? ThreadedImagesOptions.DefaultIdleMs);
    }
}

/// <summary>Upstream's <c>prepare</c> over an <see cref="IImageCodec"/>; see the file header.</summary>
internal sealed class CodecImageProcessor(IImageCodec codec) : IImageProcessor
{
    /// <summary>JPEG qualities tried in order.</summary>
    private static readonly int[] JpegQualities = [80, 70, 55, 40];

    public Task<PreparedImage?> PrepareAsync(ReadOnlyMemory<byte> bytes, string mimeType, ImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        ArgumentNullException.ThrowIfNull(limits);
        return Task.FromResult(Prepare(bytes.Span, mimeType, limits));
    }

    internal PreparedImage? Prepare(ReadOnlySpan<byte> bytes, string mimeType, ImageLimits limits)
    {
        var orientation = Exif.Orientation(bytes);
        var inline = ImageFormats.Inline.Contains(mimeType);
        try
        {
            // Decoded even when it is returned as it is: a damaged file is an error, not an image the provider rejects.
            if (codec.ProbeDimensions(bytes, mimeType) is not { } dimensions) return null;
            var (width, height) = dimensions;
            if (inline && orientation == 1 && width <= limits.MaxWidth && height <= limits.MaxHeight &&
                ImageFormats.Base64Length(bytes.Length) <= limits.MaxBytes)
                return new PreparedImage(ImageFormats.ToBase64(bytes), mimeType);
            // The dimensions are the upright image's: the codec turns it upright before resampling.
            var scale = Math.Min(1, Math.Min((double)limits.MaxWidth / width, (double)limits.MaxHeight / height));
            var target = new ImageSize(Math.Max(1, JsRound(width * scale)), Math.Max(1, JsRound(height * scale)));
            var source = bytes.ToArray();
            for (;;)
            {
                if (EncodeWithin(source, mimeType, target, limits.MaxBytes, jpegFirst: mimeType == "image/jpeg") is not { } encoded) return null;
                if (encoded.Data is not null)
                {
                    return new PreparedImage(encoded.Data, encoded.MimeType)
                    {
                        ConvertedFrom = encoded.MimeType == mimeType ? null : mimeType,
                        Resized = target.Width == width && target.Height == height ? null : new ImageResize(new ImageSize(width, height), target),
                    };
                }
                if (target is { Width: 1, Height: 1 }) return null;
                target = new ImageSize(Math.Max(1, (int)Math.Floor(target.Width * 0.75)), Math.Max(1, (int)Math.Floor(target.Height * 0.75)));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Photon throws for bytes it cannot decode; so may a codec.
            return null;
        }
    }

    /// <summary>
    /// The image at <paramref name="size"/> in the first encoding whose base64 fits <paramref name="maxBytes"/>: PNG, then JPEG at falling
    /// qualities, or the JPEG qualities first when <paramref name="jpegFirst"/>; <c>(null, null)</c> if none fits, and null when the codec
    /// cannot decode the image (Photon throws).
    /// </summary>
    private (string? Data, string MimeType)? EncodeWithin(byte[] source, string mimeType, ImageSize size, int maxBytes, bool jpegFirst)
    {
        if (codec.Encode(source, mimeType, size.Width, size.Height, JpegQualities) is not { } candidates) return null;
        var png = candidates.Where(candidate => candidate.MimeType == "image/png");
        var jpegs = candidates.Where(candidate => candidate.MimeType == "image/jpeg");
        foreach (var (bytes, candidateMimeType) in jpegFirst ? jpegs.Concat(png) : png.Concat(jpegs))
            if (ImageFormats.Base64Length(bytes.Length) <= maxBytes) return (ImageFormats.ToBase64(bytes), candidateMimeType);
        return (null, "");
    }

    /// <summary>JavaScript's <c>Math.round</c> for the non-negative values here.</summary>
    private static int JsRound(double value) => (int)Math.Floor(value + 0.5);
}
