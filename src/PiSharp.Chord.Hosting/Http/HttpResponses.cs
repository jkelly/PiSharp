using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Plugins;

namespace PiSharp.Chord.Hosting.Http;

internal static class HttpResponses
{
    internal static async Task WriteAsync(HttpContext native, HttpRequestContext request, HttpResult result, CallContext context, IReadOnlyList<Delegate> dependencies, IReadOnlyList<object?> declaringOwners)
    {
        if (result.Status is < 100 or > 599) throw new InvalidDataException("invalid response status");
        native.Response.StatusCode = result is HttpResult.Redirect && result.Status == 200 ? 302 : result.Status;
        foreach (var (name, values) in result.Headers)
        {
            if (string.IsNullOrEmpty(name) || name.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '-') || values.Any(x => x.Any(c => c is '\r' or '\n' or '\0'))) throw new InvalidDataException("invalid response header");
            native.Response.Headers[name] = new StringValues(values);
        }
        if (native.Response.StatusCode is 204 or 205 or 304) return;
        switch (result)
        {
            case HttpResult.Empty: return;
            case HttpResult.Redirect redirect:
                if (redirect.Location.Any(c => c is '\r' or '\n' or '\0')) throw new InvalidDataException("invalid redirect location");
                native.Response.Headers.Location = redirect.Location; return;
            case HttpResult.Json json: await Text(json.Value?.ToJsonString() ?? "null", "application/json; charset=utf-8"); return;
            case HttpResult.Html html: await Text(html.Body, "text/html; charset=utf-8"); return;
            case HttpResult.Text text: await Text(text.Body, text.Mime); return;
            case HttpResult.File file:
                var path = Path.GetFullPath(file.Path);
                if (file.Root is { } root)
                {
                    var relative = Path.GetRelativePath(Path.GetFullPath(root), path);
                    try { path = Plugins.PluginScanner.ContainedPath(root, relative); } catch (ArgumentException) { throw new HttpFailure(403); }
                }
                if (!System.IO.File.Exists(path)) throw new HttpFailure(404);
                await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous))
                {
                    await Bytes(new(async (offset, length, ctx) => { stream.Position = offset; var data = new byte[length]; var read = await stream.ReadAtLeastAsync(data, length, false, ctx.AbortSignal ?? CancellationToken.None).ConfigureAwait(false); return data.AsMemory(0, read); }, stream.Length, file.Mime ?? Mime(path), file.Download ?? Path.GetFileName(path), file.Inline, file.Ranges));
                }
                return;
            case HttpResult.Bytes bytes: await Bytes(bytes); return;
            default: throw new InvalidDataException("unknown HTTP response");
        }
        async Task Text(string body, string mime)
        {
            native.Response.ContentType = mime; var bytes = Encoding.UTF8.GetBytes(body); native.Response.ContentLength = bytes.Length;
            if (request.Method != "HEAD") await native.Response.Body.WriteAsync(bytes, context.AbortSignal).ConfigureAwait(false);
        }
        async Task Bytes(HttpResult.Bytes bytes)
        {
            if (ChordHttp.IsForeign(native.Request, request.Origin)) throw new HttpFailure(403);
            if (bytes.Size < 0) throw new InvalidDataException("negative body size");
            native.Response.ContentType = bytes.Mime;
            var name = new string((bytes.Download ?? "download").Where(c => !char.IsControl(c) && c is not ('"' or '\\') && c is not (>= '\u202a' and <= '\u202e') && c is not (>= '\u2066' and <= '\u2069')).ToArray());
            native.Response.Headers.ContentDisposition = $"{(bytes.Inline ? "inline" : "attachment")}; filename*=UTF-8''{Uri.EscapeDataString(name)}";
            if (bytes.Inline) native.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            long start = 0; long? end = bytes.Size;
            if (bytes.Size is { } size && bytes.Ranges)
            {
                native.Response.Headers.AcceptRanges = "bytes";
                var range = native.Request.Headers.Range.ToString();
                if (range.StartsWith("bytes=", StringComparison.Ordinal) && !range.Contains(','))
                {
                    var parts = range[6..].Split('-');
                    if (parts.Length == 2 && (parts[0].Length == 0 || long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)) && (parts[1].Length == 0 || long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                    {
                        if (parts[0].Length == 0) { var suffix = parts[1].Length == 0 ? 0 : long.Parse(parts[1], CultureInfo.InvariantCulture); start = Math.Max(0, size - suffix); }
                        else { start = long.Parse(parts[0], CultureInfo.InvariantCulture); if (parts[1].Length > 0) end = Math.Min(size, long.Parse(parts[1], CultureInfo.InvariantCulture) + 1); }
                        if (start >= size || end <= start) { native.Response.StatusCode = 416; native.Response.Headers.ContentRange = $"bytes */{size}"; native.Response.ContentLength = 0; return; }
                        native.Response.StatusCode = 206; native.Response.Headers.ContentRange = $"bytes {start}-{end - 1}/{size}";
                    }
                }
            }
            if (end is { } length) native.Response.ContentLength = length - start;
            if (request.Method == "HEAD") return;
            for (var offset = start; end is null || offset < end;)
            {
                context.AbortSignal.ThrowIfCancellationRequested();
                var lengthToRead = (int)Math.Min(256 * 1024, end is { } stop ? stop - offset : 256 * 1024);
                var block = await PluginCallbacks.InvokeAsync(bytes.Read, () => bytes.Read(offset, lengthToRead, context.Context), dependencies, declaringOwners).WaitAsync(context.AbortSignal).ConfigureAwait(false);
                if (block.Length > lengthToRead || end is not null && block.Length != lengthToRead) throw new IOException("body reader returned a short or oversized block");
                if (block.Length == 0) break;
                await native.Response.Body.WriteAsync(block, context.AbortSignal).ConfigureAwait(false); offset += block.Length;
            }
        }
    }
    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".txt" => "text/plain", ".json" => "application/json", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".mp3" => "audio/mpeg", ".mp4" => "video/mp4", ".pdf" => "application/pdf", _ => "application/octet-stream" };
}
