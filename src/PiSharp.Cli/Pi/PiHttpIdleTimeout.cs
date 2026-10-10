// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/http-dispatcher.ts (DEFAULT_HTTP_IDLE_TIMEOUT_MS,
// parseHttpIdleTimeoutMs, configureHttpDispatcher headersTimeout/bodyTimeout) and core/settings-manager.ts (parseTimeoutSetting,
// getHttpIdleTimeoutMs).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Cli.Pi;

/// <summary>The <c>httpIdleTimeoutMs</c> setting: how long a provider request may wait for response headers, and for each body chunk,
/// before it fails (upstream sets undici's headersTimeout and bodyTimeout). 0 disables it.</summary>
internal static class PiHttpIdleTimeout
{
    internal const int DefaultMilliseconds = 300_000;

    /// <summary>Source parseHttpIdleTimeoutMs: a finite number of at least 0 (floored), <c>"disabled"</c> (0) or a numeric string;
    /// anything else is undefined (null).</summary>
    internal static long? Parse(JsonNode? value)
    {
        if (value is not JsonValue scalar) return null;
        if (scalar.GetValueKind() == JsonValueKind.String)
        {
            var trimmed = PiArgs.JsTrim(scalar.GetValue<string>());
            if (trimmed.Equals("disabled", StringComparison.OrdinalIgnoreCase)) return 0;
            if (trimmed.Length == 0) return null;
            return Number(JsNumber(trimmed));
        }
        return scalar.GetValueKind() == JsonValueKind.Number ? Number(scalar.GetValue<double>()) : null;

        static long? Number(double number) => !double.IsFinite(number) || number < 0 ? null : (long)Math.Min(Math.Floor(number), long.MaxValue);
    }

    /// <summary>Source getHttpIdleTimeoutMs with parseTimeoutSetting: the merged setting, or the 300 s default when it is absent.</summary>
    /// <exception cref="InvalidDataException"><c>Invalid httpIdleTimeoutMs setting: &lt;value&gt;</c> for a present, invalid value.</exception>
    internal static long FromSettings(JsonObject merged)
    {
        if (!merged.TryGetPropertyValue("httpIdleTimeoutMs", out var value)) return DefaultMilliseconds;
        return Parse(value) ?? throw new InvalidDataException($"Invalid httpIdleTimeoutMs setting: {JsString(value)}");
    }

    /// <summary>Wraps a provider handler factory so its requests carry the idle timeout (0: unchanged). A null inner handler is the
    /// providers' own default (no automatic redirects).</summary>
    internal static Func<HttpMessageHandler?> Wrap(Func<HttpMessageHandler?> inner, long milliseconds) => milliseconds == 0 ? inner :
        () => new Handler(TimeSpan.FromMilliseconds(Math.Min(milliseconds, int.MaxValue))) { InnerHandler = inner() ?? new HttpClientHandler { AllowAutoRedirect = false } };

    // JavaScript Number(string) for the forms settings carry: decimal, exponent, Infinity and 0x/0o/0b integers.
    private static double JsNumber(string text)
    {
        if (text.Length > 2 && text[0] == '0' && char.ToLowerInvariant(text[1]) is 'x' or 'o' or 'b')
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'o' => 8, _ => 2 };
            double total = 0;
            foreach (var digit in text[2..])
            {
                var value = Uri.IsHexDigit(digit) ? Convert.ToInt32(digit.ToString(), 16) : 99;
                if (value >= radix) return double.NaN;
                total = total * radix + value;
            }
            return total;
        }
        if (text is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (text == "-Infinity") return double.NegativeInfinity;
        return text.All(c => char.IsAsciiDigit(c) || c is '.' or 'e' or 'E' or '+' or '-') &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
    }

    // JavaScript String(value) for a JSON setting.
    private static string JsString(JsonNode? value) => value switch
    {
        null => "null",
        JsonArray array => string.Join(",", array.Select(item => item is null ? "" : JsString(item))),
        JsonObject => "[object Object]",
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => scalar.GetValue<string>(),
        _ => value.ToJsonString()
    };

    /// <summary>Fails a request whose response headers, or whose next body chunk, take longer than the timeout (undici
    /// HeadersTimeoutError and BodyTimeoutError).</summary>
    internal sealed class Handler(TimeSpan timeout) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headers.CancelAfter(timeout);
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, headers.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new HttpRequestException("Headers Timeout Error", new TimeoutException($"No response headers within {timeout.TotalMilliseconds} ms.")); }
            // An upgraded connection (the Codex WebSocket) is no response body: undici's bodyTimeout does not apply to it.
            if (response.StatusCode == System.Net.HttpStatusCode.SwitchingProtocols) return response;
            var original = response.Content;
            var content = new StreamContent(new IdleStream(await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), timeout, original));
            foreach (var header in original.Headers) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = content;
            return response;
        }
    }

    private sealed class IdleStream(Stream inner, TimeSpan timeout, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(timeout);
            try { return await inner.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new HttpRequestException("Body Timeout Error", new TimeoutException($"No response body data within {timeout.TotalMilliseconds} ms.")); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); owner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
