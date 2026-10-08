using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;

// Controlled test observation only; no exception is reclassified or used to change command outcomes.
internal sealed class TerminalCommandFailureDiagnostics
{
    internal Exception? OriginalFailure { get; private set; }
    internal JsonElement? Receipt { get; private set; }
    internal void Capture(TerminalSessionFailureObservation observation)
    {
        OriginalFailure = observation.Failure;
        var canceled = observation.Failure as OperationCanceledException;
        var provenance = new
        {
            hasCancellation = canceled is not null,
            exceptionTokenCanBeCanceled = canceled?.CancellationToken.CanBeCanceled ?? false,
            exceptionTokenCanceled = canceled?.CancellationToken.IsCancellationRequested ?? false,
            matchesCaller = canceled is not null && canceled.CancellationToken == observation.CallerToken,
            matchesInput = canceled is not null && canceled.CancellationToken == observation.InputToken,
            matchesHost = canceled is not null && canceled.CancellationToken == observation.HostToken,
            matchesResize = canceled is not null && canceled.CancellationToken == observation.ResizeToken,
            callerCanceled = observation.CallerToken.IsCancellationRequested,
            inputCanceled = observation.InputToken.IsCancellationRequested,
            hostCanceled = observation.HostToken.IsCancellationRequested,
            resizeCanceled = observation.ResizeToken.IsCancellationRequested
        };
        var exceptionText = observation.Failure?.ToString();
        var receipt = JsonSerializer.SerializeToElement(new
        {
            observation.ExitCode, observation.UserShutdown, provenance,
            failureType = Clip(observation.Failure?.GetType().FullName, 256),
            message = Clip(observation.Failure?.Message, 256), exception = Clip(exceptionText, 2048),
            innerType = Clip(observation.Failure?.InnerException?.GetType().FullName, 256),
            truncated = exceptionText?.Length > 2048
        });
        // Bound UTF-8, including JSON escaping. Fallback retains outcome and exact token comparisons.
        Receipt = Encoding.UTF8.GetByteCount(receipt.GetRawText()) <= 8192 ? receipt : JsonSerializer.SerializeToElement(new
        {
            observation.ExitCode, observation.UserShutdown, provenance,
            failureType = Clip(observation.Failure?.GetType().FullName, 64),
            message = Clip(observation.Failure?.Message, 64), exception = Clip(exceptionText, 256), truncated = true
        });
    }
    private static string? Clip(string? value, int limit) => value is null || value.Length <= limit ? value : value[..limit];
}
