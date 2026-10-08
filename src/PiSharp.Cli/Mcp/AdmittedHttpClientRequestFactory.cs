using System.Runtime.ExceptionServices;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Cli.Mcp;

/// <summary>Request-specific cancellation over a caller-admitted borrowed HttpClient. This factory
/// never creates or disposes the client, changes headers, or resolves credentials. Its handler must
/// honor request cancellation; native handler physical-abort qualification remains a host obligation.</summary>
public static class AdmittedHttpClientRequestFactory
{
    public static McpAdmittedHttpRequestFactory Create(HttpClient borrowedClient)
    {
        ArgumentNullException.ThrowIfNull(borrowedClient);
        return request => new Operation(borrowedClient, request ?? throw new ArgumentNullException(nameof(request)));
    }

    private sealed class Operation(HttpClient client, HttpRequestMessage request) : IMcpAdmittedHttpRequestOperation
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource source = new();
        private readonly AsyncLocal<bool> inSendCallback = new();
        private readonly TaskCompletionSource sendAdmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool admitted;
        private CancellationTokenRegistration callerRegistration;
        private Task<HttpResponseMessage>? physicalSend, send;
        private Task? stop;

        public ValueTask<HttpResponseMessage> SendAsync(CancellationToken cancellationToken)
        {
            RejectReentry(); cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (stop is not null || send is not null) throw new InvalidOperationException("Admitted HTTP request lease already consumed");
                admitted = true;
                // Explicit linking starts the stable Stop original without awaiting it inside the
                // caller cancellation callback. It avoids synchronous nested cancellation of a
                // handler callback which may need the physical abort to begin first.
                try
                {
                    callerRegistration = cancellationToken.Register(() => { lock (gate) _ = BeginStop(); });
                    if (stop is not null) throw new OperationCanceledException(cancellationToken);
                    send = SendCoreAsync(); return new(send);
                }
                finally { sendAdmission.TrySetResult(); }
            }
        }

        private async Task<HttpResponseMessage> SendCoreAsync()
        {
            inSendCallback.Value = true;
            try
            {
                source.Token.ThrowIfCancellationRequested();
                physicalSend = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, source.Token);
                // Late headers remain a successful transfer to the transport, which owns response
                // and body disposal. Neither cancellation nor Stop disposes that transferred owner.
                return await physicalSend.ConfigureAwait(false);
            }
            finally { inSendCallback.Value = false; }
        }

        public Task StopAsync()
        { RejectReentry(); lock (gate) return BeginStop(); }

        private void RejectReentry()
        { if (inSendCallback.Value) throw new InvalidOperationException("Admitted HTTP handler cannot join its own stop"); }

        private Task BeginStop()
        {
            if (!admitted) sendAdmission.TrySetResult();
            // The retained original runs outside the gate. Registration.Dispose may join a caller
            // callback that is itself waiting to enter this gate; never join it while holding it.
            return stop ??= Task.Run(StopCoreAsync);
        }

        private async Task StopCoreAsync()
        {
            var failures = new List<Exception>(); Task? cancellation = null;
            try { cancellation = source.CancelAsync(); } catch (Exception error) { failures.Add(error); }
            // A caller may cancel synchronously inside handler/header initiation. Wait for the
            // admission original before snapshotting tasks or disposing its registration/source.
            await sendAdmission.Task.ConfigureAwait(false);
            Task<HttpResponseMessage>? headers, sendOriginal;
            lock (gate) { headers = physicalSend; sendOriginal = send; }
            // Actual header/send failures belong to SendAsync. Stop still joins those originals,
            // including late headers and synchronous SendAsync initiation faults, without replay.
            if (headers is not null) try { await headers.ConfigureAwait(false); } catch { }
            if (sendOriginal is not null) try { await sendOriginal.ConfigureAwait(false); } catch { }
            if (cancellation is not null) try { await cancellation.ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            try { callerRegistration.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { source.Dispose(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }
}
