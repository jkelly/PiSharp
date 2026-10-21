// PiSharp-only (decision 0010, M3): direct HTTP retrieval that cannot be pointed at the device's own network. The connection callback
// resolves the name itself and connects only to an address it checked, so a name that re-resolves to a private address between a check and
// the connection (DNS rebinding) still cannot reach it. Redirects and decompression are done by hand so every hop and every byte is counted.
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace PiSharp.Durable.Hosting;

/// <summary>Which addresses a source fetch may connect to.</summary>
public static class AddressPolicy
{
    /// <summary>Why <paramref name="address"/> is not a public address, or null when it is: loopback, private, link-local, shared
    /// (carrier-grade NAT), multicast, unspecified, reserved, IPv6 unique-local or site-local, or one of those embedded in an IPv6 form.</summary>
    public static string? NonPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => "an unspecified address",
                10 => "a private address",
                127 => "a loopback address",
                100 when (b[1] & 0xC0) == 64 => "a shared (carrier-grade NAT) address",
                169 when b[1] == 254 => "a link-local address",
                172 when (b[1] & 0xF0) == 16 => "a private address",
                192 when b[1] == 168 => "a private address",
                192 when b[1] == 0 && b[2] is 0 or 2 => "a reserved address",
                198 when (b[1] & 0xFE) == 18 => "a benchmarking address",
                198 when b[1] == 51 && b[2] == 100 => "a reserved address",
                203 when b[1] == 0 && b[2] == 113 => "a reserved address",
                >= 224 => "a multicast or reserved address",
                _ => null,
            };
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return "an address of an unknown family";
        if (address.Equals(IPAddress.IPv6Loopback)) return "a loopback address";
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)) return "an unspecified address";
        if (address.IsIPv6LinkLocal) return "a link-local address";
        if (address.IsIPv6SiteLocal) return "a site-local address";
        if (address.IsIPv6Multicast) return "a multicast address";
        if (address.IsIPv6UniqueLocal) return "a unique-local (private) address";
        var bytes = address.GetAddressBytes();
        // IPv4-compatible (::a.b.c.d), NAT64 (64:ff9b::/96) and 6to4 (2002::/16) forms carry an IPv4 address that is checked in turn.
        if (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0) return NonPublic(new IPAddress(bytes.AsSpan(12, 4)));
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            return NonPublic(new IPAddress(bytes.AsSpan(12, 4)));
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return NonPublic(new IPAddress(bytes.AsSpan(2, 4)));
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0) return "a Teredo tunnel address";
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return "a documentation address";
        return null;
    }

    /// <summary>Whether <paramref name="address"/> is a cloud instance metadata address, which no allowlist opens.</summary>
    public static bool IsMetadata(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.Equals(Metadata) || address.Equals(AlibabaMetadata) || address.Equals(AwsMetadataV6);
    }

    private static readonly IPAddress Metadata = IPAddress.Parse("169.254.169.254");
    private static readonly IPAddress AlibabaMetadata = IPAddress.Parse("100.100.100.200");
    private static readonly IPAddress AwsMetadataV6 = IPAddress.Parse("fd00:ec2::254");
}

/// <summary>Direct HTTP(S) retrieval under <see cref="AddressPolicy"/>.</summary>
internal sealed class DirectRetrievalProvider : IRetrievalProvider, IDisposable
{
    /// <summary>The provider's ID.</summary>
    public const string ProviderId = "direct";

    private static readonly IReadOnlyDictionary<string, long> OneRequest = new Dictionary<string, long>(StringComparer.Ordinal) { ["requests"] = 1 };

    private readonly HttpClient client;
    private readonly HashSet<string> allowed;
    private readonly string userAgent;
    private readonly TimeProvider time;

    public DirectRetrievalProvider(RetrievalOptions options, TimeProvider time)
    {
        allowed = new HashSet<string>(options.AllowedPrivateHosts.Select(host => host.Trim().TrimEnd('.').Trim('[', ']')), StringComparer.OrdinalIgnoreCase);
        userAgent = options.UserAgent;
        this.time = time;
        client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        })
        { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public string Id => ProviderId;

    public IReadOnlyDictionary<string, long> Estimate(RetrievalRequest request) => OneRequest;

    public async Task<RetrievalResponse> FetchAsync(RetrievalRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Limits.Timeout);
        var url = request.Url;
        var origin = Origin(url);
        try
        {
            for (var hop = 0; ; hop++)
            {
                Check(url);
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                message.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
                if (request.Accept is { } accept) message.Headers.TryAddWithoutValidation("Accept", accept);
                // Credentials and validators belong to the requested resource: never to another origin, and validators not to a redirect target.
                if (request.Authorization is { } authorization && Origin(url) == origin) message.Headers.TryAddWithoutValidation("Authorization", authorization);
                if (hop == 0 && request.ETag is { } etag) message.Headers.TryAddWithoutValidation("If-None-Match", etag);
                if (hop == 0 && request.LastModified is { } modified) message.Headers.TryAddWithoutValidation("If-Modified-Since", modified);
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location) throw new RetrievalException("http", $"A {status} redirect without a location.", status);
                    if (hop >= request.Limits.MaxRedirects) throw new RetrievalException("redirects", $"More than {request.Limits.MaxRedirects} redirects.", status);
                    url = new Uri(url, location);
                    continue;
                }
                var etagOut = response.Headers.ETag?.ToString();
                var modifiedOut = response.Content.Headers.LastModified?.ToString("R");
                if (status == 304) return new RetrievalResponse(304, url, [], null, etagOut, modifiedOut);
                if (status is 403 or 429 or >= 500)
                    throw new RetrievalException(status switch { 403 => "forbidden", 429 => "rate_limited", _ => "server" },
                        $"{url.Host} answered {status} {response.ReasonPhrase}.", status, RetryAfter(response.Headers.RetryAfter));
                if (status is < 200 or > 299) throw new RetrievalException("http", $"{url.Host} answered {status} {response.ReasonPhrase}.", status);
                var body = await ReadAsync(response, request.Limits.MaxResponseBytes, timeout.Token).ConfigureAwait(false);
                return new RetrievalResponse(status, url, body, response.Content.Headers.ContentType?.ToString(), etagOut, modifiedOut, OneRequest,
                    response.Headers.TryGetValues("Link", out var links) ? string.Join(", ", links) : null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RetrievalException("timeout", $"The fetch of {request.Url.Host} took longer than {request.Limits.Timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException)
        {
            for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
                if (inner is RetrievalException retrieval) ExceptionDispatchInfo.Throw(retrieval);
            throw new RetrievalException("network", $"{url.Host}: {error.Message}");
        }
    }

    public void Dispose() => client.Dispose();

    /// <summary>The scheme, host and port of <paramref name="url"/>.</summary>
    internal static string Origin(Uri url) => $"{url.Scheme}://{url.IdnHost}:{url.Port}".ToLowerInvariant();

    private static void Check(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https")) throw new RetrievalException("blocked", "Only http and https URLs can be fetched.");
        if (url.UserInfo.Length > 0) throw new RetrievalException("blocked", "A URL with credentials in it is not fetched.");
    }

    private TimeSpan? RetryAfter(RetryConditionHeaderValue? value) => value switch
    {
        { Delta: { } delta } => delta < TimeSpan.Zero ? TimeSpan.Zero : delta,
        { Date: { } date } => date - time.GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
        _ => null,
    };

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host.TrimEnd('.').Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var permitted = allowed.Contains(host);
        var usable = new List<IPAddress>();
        string? refused = null;
        foreach (var address in addresses)
        {
            if (AddressPolicy.IsMetadata(address)) refused = "a cloud metadata address";
            else if (!permitted && AddressPolicy.NonPublic(address) is { } reason) refused = reason;
            else usable.Add(address);
        }
        if (usable.Count == 0)
            throw new RetrievalException("blocked", $"{host} resolves to {refused ?? "no address"}; a private source needs its host in AllowedPrivateHosts.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync([.. usable], context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Reads the body, decoding its content encodings by hand: the bytes received and the bytes decoded both count against
    /// <paramref name="limit"/>.</summary>
    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new RetrievalException("too_large", $"The response has {response.Content.Headers.ContentLength} bytes; the limit is {limit}.");
        var raw = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        Stream stream = new BoundedStream(raw, limit, "received");
        foreach (var encoding in response.Content.Headers.ContentEncoding.Reverse())
        {
            stream = encoding.ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(stream, CompressionMode.Decompress),
                "deflate" => new ZLibStream(stream, CompressionMode.Decompress),
                "br" => new BrotliStream(stream, CompressionMode.Decompress),
                "identity" => stream,
                var other => throw new RetrievalException("http", $"The response uses an unsupported content encoding, {other}."),
            };
        }
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > limit) throw new RetrievalException("too_large", $"The decoded response exceeds {limit} bytes.");
                    buffer.Write(chunk, 0, read);
                }
            }
            catch (InvalidDataException error)
            {
                throw new RetrievalException("http", "The response's compressed body is invalid: " + error.Message);
            }
            return buffer.ToArray();
        }
    }

    /// <summary>A read-only stream that fails once more than its limit was read.</summary>
    private sealed class BoundedStream(Stream inner, long limit, string what) : Stream
    {
        private long total;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            total += read;
            if (total > limit) throw new RetrievalException("too_large", $"The {what} response exceeds {limit} bytes.");
            return read;
        }
    }
}
