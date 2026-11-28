// Decision 0016: classifies transient connection/network failures before native transports
// replace raw exception text with fixed sanitized error messages.
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.ProviderShared;

internal static class TransportRetryClassifier
{
    private const int MaximumDepth = 16;
    private static readonly ConditionalWeakTable<Exception, object> NonRetryable = new();
    private static readonly object NonRetryableMarker = new();

    internal static void MarkNonRetryable(Exception? error)
    {
        if (error is not null) NonRetryable.AddOrUpdate(error, NonRetryableMarker);
    }

    internal static bool IsTransientConnectionFailure(Exception? error, bool callerCancelled = false)
    {
        if (callerCancelled || error is null) return false;
        return Inspect(error, depth: 0);
    }

    private static bool Inspect(Exception error, int depth)
    {
        if (depth > MaximumDepth) return false;
        if (NonRetryable.TryGetValue(error, out _)) return false;
        if (error is StreamLimitException or StreamProtocolException or JsonException or ArgumentException or InvalidOperationException)
            return false;

        if (error is SocketException socket)
        {
            return socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted
                or SocketError.ConnectionRefused or SocketError.TimedOut or SocketError.HostNotFound
                or SocketError.TryAgain or SocketError.NetworkDown or SocketError.NetworkUnreachable
                or SocketError.HostUnreachable or SocketError.Shutdown or SocketError.Interrupted
                or SocketError.OperationAborted or SocketError.NoData;
        }

        if (error is HttpIOException httpIo)
        {
            if (httpIo.HttpRequestError is HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError
                or HttpRequestError.NameResolutionError or HttpRequestError.ProxyTunnelError
                or HttpRequestError.HttpProtocolError)
                return true;
            if (httpIo.HttpRequestError is HttpRequestError.InvalidResponse or HttpRequestError.ConfigurationLimitExceeded
                or HttpRequestError.UserAuthenticationError or HttpRequestError.ExtendedConnectNotSupported
                or HttpRequestError.VersionNegotiationError)
                return false;
            if (httpIo.InnerException is not null && Inspect(httpIo.InnerException, depth + 1))
                return true;
            return httpIo.HttpRequestError == HttpRequestError.Unknown && MatchesNetworkText(httpIo.Message);
        }

        if (error is HttpRequestException http)
        {
            if (http.StatusCode is { } status)
                return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                    or (HttpStatusCode)520 or (HttpStatusCode)524 or (HttpStatusCode)525;
            if (http.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.ResponseEnded or HttpRequestError.ProxyTunnelError
                or HttpRequestError.HttpProtocolError)
                return true;
            if (http.InnerException is not null && Inspect(http.InnerException, depth + 1))
                return true;
            return http.HttpRequestError == HttpRequestError.Unknown && MatchesNetworkText(http.Message);
        }

        if (error is TimeoutException)
            return true;

        if (error is OperationCanceledException)
            // Caller cancellation is already excluded at the top; an unrequested cancellation is a request/header timeout.
            return true;

        if (error is IOException io && error is not FileNotFoundException and not DirectoryNotFoundException)
        {
            if (io.InnerException is not null && Inspect(io.InnerException, depth + 1))
                return true;
            if (MatchesNetworkText(io.Message))
                return true;
        }

        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                if (Inspect(inner, depth + 1)) return true;
            return false;
        }

        return error.InnerException is not null && Inspect(error.InnerException, depth + 1);
    }

    private static bool MatchesNetworkText(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
            || message.Contains("error occurred while sending the request", StringComparison.OrdinalIgnoreCase)
            || message.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase)
            || message.Contains("premature", StringComparison.OrdinalIgnoreCase)
            || message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection aborted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection was aborted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("software caused connection abort", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("did not properly respond after a period of time", StringComparison.OrdinalIgnoreCase)
            || message.Contains("socket had already been shut down", StringComparison.OrdinalIgnoreCase)
            || message.Contains("without completing the close handshake", StringComparison.OrdinalIgnoreCase)
            || message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
            || message.Contains("socket", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ECONNABORTED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase)
            || message.Contains("EPIPE", StringComparison.OrdinalIgnoreCase);
    }
}
