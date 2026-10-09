// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/cli/file-processor.ts (processFileArguments),
// packages/coding-agent/src/cli/initial-message.ts (buildInitialMessage) and packages/coding-agent/src/main.ts (readPipedStdin,
// prepareInitialMessage).
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Images;

namespace PiSharp.Cli.Pi;

internal sealed record PiImageContent(string MimeType, string Data)
{
    internal JsonData ToJson() => JsonData.Parse(JsonSerializer.Serialize(new { type = "image", data = Data, mimeType = MimeType }));
}

/// <summary>An <c>@file</c> argument that cannot be used: the message is printed as <c>Error: …</c> and the run exits 1.</summary>
internal sealed class PiFileArgumentException(string message) : Exception(message);

internal static class PiInitialMessage
{
    /// <summary>Source processFileArguments: text files wrapped in <c>&lt;file name="…"&gt;</c>, images attached with an empty (or hint)
    /// reference. Empty files are skipped. Images are not resized here; the session resizes them for the request model.</summary>
    internal static async Task<(string Text, ImmutableArray<PiImageContent> Images)> ProcessFileArgumentsAsync(IEnumerable<string> fileArgs,
        string cwd, string home, bool autoResizeImages = false, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder(); var images = ImmutableArray.CreateBuilder<PiImageContent>();
        var resolver = new PathResolver(cwd, home, new LocalFileOperations());
        foreach (var fileArg in fileArgs)
        {
            string absolute;
            try { absolute = Path.GetFullPath(await resolver.ResolveReadAsync(fileArg, cancellationToken).ConfigureAwait(false)); }
            catch (ArgumentException) { absolute = Path.GetFullPath(Path.Combine(cwd, fileArg)); }
            if (!Path.Exists(absolute)) throw new PiFileArgumentException($"File not found: {absolute}");
            var info = new FileInfo(absolute);
            if (info.Exists && info.Length == 0) continue;
            string? mime = null;
            if (info.Exists)
            {
                try { mime = await ImageMime.DetectSupportedImageMimeTypeFromFileAsync(absolute, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { mime = null; }
            }
            if (mime is not null)
            {
                var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
                var processed = ImageProcessor.Process(bytes, mime, autoResizeImages, codec: PiSharp.Tools.Skia.SkiaImageCodec.Instance);
                if (!processed.Ok) { text.Append($"<file name=\"{absolute}\">{processed.Message}</file>\n"); continue; }
                images.Add(new(processed.MimeType!, processed.Data!));
                text.Append(processed.Hints.Length > 0 ? $"<file name=\"{absolute}\">{string.Join("\n", processed.Hints)}</file>\n" : $"<file name=\"{absolute}\"></file>\n");
            }
            else
            {
                try
                {
                    var content = PiPaths.StripBom(await File.ReadAllTextAsync(absolute, new UTF8Encoding(false, false), cancellationToken).ConfigureAwait(false));
                    text.Append($"<file name=\"{absolute}\">\n{content}\n</file>\n");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new PiFileArgumentException($"Could not read file {absolute}: {error.Message}"); }
            }
        }
        return (text.ToString(), images.ToImmutable());
    }

    /// <summary>Source buildInitialMessage: stdin, then the @file text, then the first CLI message (removed from the queue), joined
    /// without separators; images only when there are some.</summary>
    internal static (string? Message, ImmutableArray<PiImageContent> Images) Build(List<string> messages, string? fileText,
        ImmutableArray<PiImageContent> fileImages, string? stdinContent)
    {
        var parts = new List<string>();
        if (stdinContent is not null) parts.Add(stdinContent);
        if (!string.IsNullOrEmpty(fileText)) parts.Add(fileText);
        if (messages.Count > 0) { parts.Add(messages[0]); messages.RemoveAt(0); }
        return (parts.Count > 0 ? string.Concat(parts) : null, fileImages.IsDefaultOrEmpty ? [] : fileImages);
    }

    /// <summary>Source readPipedStdin: all of piped stdin, trimmed; null when it is empty (a terminal stdin is never read).</summary>
    internal static async Task<string?> ReadPipedStdinAsync(TextReader input, CancellationToken cancellationToken)
    {
        var data = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var trimmed = PiArgs.JsTrim(data);
        return trimmed.Length == 0 ? null : trimmed;
    }
}
