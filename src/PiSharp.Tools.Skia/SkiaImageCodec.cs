// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/image-resize-core.ts (tryEncodings),
// utils/image-convert.ts (encodePng) and utils/exif-orientation.ts, over SkiaSharp instead of Photon.
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using PiSharp.Tools.Images;
using SkiaSharp;

namespace PiSharp.Tools.Skia;

/// <summary>
/// The Photon backend of the source read tool, on SkiaSharp. Every format Photon reads is decoded (PNG, JPEG, GIF first
/// frame, WebP, BMP) into straight RGBA, oriented with the source EXIF rules, resampled with Lanczos3 and encoded as PNG and
/// as JPEG at each requested quality, as tryEncodings does. A file Skia cannot decode is null, as a Photon decode failure is.
/// </summary>
public sealed class SkiaImageCodec : IImageCodec
{
    public static SkiaImageCodec Instance { get; } = new();

    /// <summary>Photon decodes the whole image before measuring it, so an undecodable body is null here too.</summary>
    public (int Width, int Height)? ProbeDimensions(ReadOnlySpan<byte> bytes, string mimeType)
    {
        var raster = Decode(bytes);
        return raster is null ? null : (raster.Width, raster.Height);
    }

    /// <summary>Source convertImageBytesToPng: decode, apply EXIF orientation, encode PNG.</summary>
    public byte[]? ConvertToPng(ReadOnlySpan<byte> bytes, string mimeType)
    {
        var raster = Decode(bytes);
        return raster is null ? null : Encode(raster, SKEncodedImageFormat.Png, 100);
    }

    /// <summary>Source tryEncodings: one Lanczos3 resize, then PNG followed by JPEG at each quality, in order.</summary>
    public ImmutableArray<(byte[] Bytes, string MimeType)>? Encode(ReadOnlySpan<byte> bytes, string mimeType, int width, int height,
        IReadOnlyList<int> jpegQualities)
    {
        ArgumentNullException.ThrowIfNull(jpegQualities);
        var raster = Decode(bytes);
        if (raster is null || width < 1 || height < 1 || (long)width * height > RasterImage.MaximumPixels) return null;
        var resized = raster.Resize(width, height);
        var candidates = ImmutableArray.CreateBuilder<(byte[] Bytes, string MimeType)>(1 + jpegQualities.Count);
        var png = Encode(resized, SKEncodedImageFormat.Png, 100);
        if (png is null) return null;
        candidates.Add((png, "image/png"));
        foreach (var quality in jpegQualities)
        {
            var jpeg = Encode(resized, SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 0, 100));
            if (jpeg is null) return null;
            candidates.Add((jpeg, "image/jpeg"));
        }
        return candidates.MoveToImmutable();
    }

    /// <summary>Photon PhotonImage.new_from_byteslice plus applyExifOrientation: straight 8-bit RGBA of the first frame.</summary>
    internal static RasterImage? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        var width = codec.Info.Width; var height = codec.Info.Height;
        if (width < 1 || height < 1 || (long)width * height > RasterImage.MaximumPixels) return null;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[(long)width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            // Frame 0 is the first GIF/WebP frame, as Photon decodes it.
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, new SKCodecOptions(0));
            if (result != SKCodecResult.Success) return null;
        }
        finally { handle.Free(); }
        var raster = new RasterImage(width, height, pixels);
        return ExifOrientation.Apply(raster, ExifOrientation.Read(bytes));
    }

    private static byte[]? Encode(RasterImage raster, SKEncodedImageFormat format, int quality)
    {
        var info = new SKImageInfo(raster.Width, raster.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(raster.Pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            // JPEG has no alpha: the encoder ignores it, as Photon's get_bytes_jpeg drops it.
            using var encoded = format == SKEncodedImageFormat.Jpeg
                ? pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
                : pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6));
            return encoded?.ToArray();
        }
        finally { handle.Free(); }
    }
}
