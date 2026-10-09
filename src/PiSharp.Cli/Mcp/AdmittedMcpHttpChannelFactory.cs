using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Roots;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Transport;

namespace PiSharp.Cli.Mcp;

/// <summary>Explicit host injection of a borrowed admitted client and independently admitted HTTP
/// binding. Configuration labels a server; it never acquires a client, endpoint, headers or authority.</summary>
public static class AdmittedMcpHttpChannelFactory
{
    public static McpAdmittedChannelFactory Create(McpServerEntry exactAdmittedEntry,
        McpHttpBinding explicitlyAdmittedBinding, HttpClient borrowedClient, McpRuntimeOptions admittedRuntimeOptions, McpTransportLimits? limits = null,
        McpNotificationHandler? notification = null, TimeProvider? clock = null,
        McpAdmittedDynamicRootsProvider? dynamicRoots = null,
        McpAdmittedHttpAuthentication? authentication = null)
    {
        ArgumentNullException.ThrowIfNull(exactAdmittedEntry); ArgumentNullException.ThrowIfNull(explicitlyAdmittedBinding);
        ArgumentNullException.ThrowIfNull(borrowedClient);
        ArgumentNullException.ThrowIfNull(admittedRuntimeOptions);
        if (dynamicRoots?.GetInvocationList().Length > 1)
            throw new ArgumentException("One explicitly admitted dynamic roots provider required", nameof(dynamicRoots));
        if (dynamicRoots is not null && admittedRuntimeOptions.Roots is not null)
            throw new ArgumentException("Static and dynamic roots are mutually exclusive", nameof(dynamicRoots));
        if (admittedRuntimeOptions.Generation < 1 || admittedRuntimeOptions.Limits is not { MaximumResponseBytes: > 0 })
            throw new ArgumentException("Explicit runtime generation and response ceiling are required", nameof(admittedRuntimeOptions));
        limits ??= new(MaximumFrameBytes: Math.Min(new McpTransportLimits().MaximumFrameBytes, admittedRuntimeOptions.Limits.MaximumResponseBytes));
        if (limits.MaximumFrameBytes > admittedRuntimeOptions.Limits.MaximumResponseBytes)
            throw new ArgumentException("Wire frame ceiling must not exceed the admitted runtime response ceiling", nameof(limits));
        if (exactAdmittedEntry.Config.Transport != McpTransportKind.Http || !exactAdmittedEntry.Config.Enabled)
            throw new ArgumentException("An explicitly admitted enabled HTTP server entry is required", nameof(exactAdmittedEntry));
        var requests = AdmittedHttpClientRequestFactory.Create(borrowedClient);
        if (authentication is not null)
            requests = McpAuthenticatedHttpRequestFactory.Create(requests, explicitlyAdmittedBinding.Endpoint, authentication, limits.MaximumFrameBytes);
        return (entry, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // `/mcp` reconfigures the server (enabled, exposure) without changing its connection.
            if (!McpConfigurationReader.SameConnection(entry, exactAdmittedEntry) || !entry.Config.Enabled)
                throw new InvalidOperationException("HTTP admission belongs to the exact captured server entry");
            // Each acquisition transfers one independently closeable channel; the client stays borrowed.
            var channel = new McpJsonRpcRequestChannel(
                new McpStreamableHttpTransport(explicitlyAdmittedBinding, requests, limits, clock), limits, notification, clock);
            try
            {
                // Install before runtime Start/initialize. This channel owns the handler and joins its
                // admitted provider originals on Close; the callback itself acquires no URI authority.
                if (dynamicRoots is not null) channel.ConfigureDynamicRoots(dynamicRoots);
                return ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel);
            }
            catch (Exception error) { return new(CloseFailedAdmissionAsync(channel, error)); }
        };
    }

    private static async Task<IMcpAdmittedRequestChannel> CloseFailedAdmissionAsync(
        McpJsonRpcRequestChannel channel, Exception admissionFailure)
    {
        Task? original = null;
        try { original = channel.CloseAsync(); await original.ConfigureAwait(false); }
        catch (Exception error)
        {
            var evidence = original is { IsFaulted: true } ? original.Exception! : error;
            throw new AggregateException("Dynamic roots admission and owned channel retirement failed.", admissionFailure, evidence);
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(admissionFailure).Throw();
        throw new InvalidOperationException("Unreachable after admission failure.");
    }
}
