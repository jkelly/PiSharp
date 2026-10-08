using System.Text;
using System.Text.Json;
using System.Diagnostics;
using PiSharp.Cli.Commands;
using PiSharp.Rpc.Protocol;

// Test-only bounded facts. Capture never awaits the host from its shutdown callback.
internal sealed class TerminalShutdownDiagnostics
{
    private RpcSessionShutdownSettlement? settlement;
    internal void Capture(RpcSessionShutdownSettlement value) => settlement = value;

    // Call only after the original terminal command has joined. No diagnostic wait can detach work.
    internal async Task<JsonElement> CaptureAfterJoinAsync(string path, string? toolCallId, Exception? terminalFailure)
    {
        var captured = settlement;
        var ids = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
        var nodes = new List<object>(); var truncated = false;
        int Identity(Exception error)
        {
            if (ids.TryGetValue(error, out var existing)) return existing;
            if (ids.Count == 16) { truncated = true; return 0; }
            var id = ids.Count + 1; ids.Add(error, id);
            var children = error is AggregateException aggregate ? aggregate.InnerExceptions.AsEnumerable() :
                error.InnerException is { } inner ? new[] { inner } : Enumerable.Empty<Exception>();
            var edges = children.Take(8).Select(Identity).ToArray();
            if (children.Skip(8).Any()) truncated = true;
            var type = error.GetType().FullName ?? error.GetType().Name;
            var originMethods = (new StackTrace(error, false).GetFrames() ?? []).Take(2).Select(frame =>
            {
                var method = frame.GetMethod(); var name = method?.DeclaringType?.FullName + "." + method?.Name;
                return name[..Math.Min(name.Length, 96)];
            }).ToArray();
            var failureCode = error switch
            {
                RpcDispatchException dispatch => dispatch.Failure.ToString(),
                SessionCommandException command => command.Failure.ToString(),
                _ => null
            };
            nodes.Add(new { id, type = type[..Math.Min(type.Length, 120)], children = edges,
                failureCode, originMethods,
                cancellation = error is OperationCanceledException,
                tokenCanceled = error is OperationCanceledException canceled && canceled.CancellationToken.IsCancellationRequested });
            return id;
        }
        object Phase(string phase, IEnumerable<Exception> errors)
        {
            var all = errors.ToArray(); if (all.Length > 16) truncated = true;
            return new { phase, count = all.Length, roots = all.Take(16).Select(Identity).ToArray() };
        }
        var phases = new List<object> { Phase("terminal-command", terminalFailure is null ? [] : new[] { terminalFailure }) };
        if (captured is not null)
        {
            phases.Add(Phase("phase-one", captured.Failures));
            phases.Add(Phase("terminal-acknowledgment", captured.CaptureAcceptedTerminalFailures()));
            if (captured.RuntimeCleanup.IsCompletedSuccessfully)
                phases.Add(Phase("runtime-cleanup", await captured.RuntimeCleanup.ConfigureAwait(false)));
            if (captured.Completion.IsCompletedSuccessfully)
                phases.Add(Phase("final-settlement", await captured.Completion.ConfigureAwait(false)));
        }
        var agent = captured?.Session?.Agent;
        var outcome = agent?.CompletedToolOutcomes.FirstOrDefault(value => value.Invocation.Call.Id == toolCallId);
        var memoryResult = agent?.Messages.Any(value => value.Role == "toolResult" &&
            value.WireBody.Value.TryGetProperty("toolCallId", out var id) && id.GetString() == toolCallId);
        var physicalResult = false; var physicalError = false; string? logFailureType = null;
        if (toolCallId is not null)
            try
            {
                // Evidence parsing has its own hard byte bound and exports no message content.
                if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException();
                foreach (var line in await File.ReadAllLinesAsync(path).ConfigureAwait(false))
                {
                    using var parsed = JsonDocument.Parse(line); var entry = parsed.RootElement;
                    if (!entry.TryGetProperty("message", out var message) ||
                        !message.TryGetProperty("role", out var role) || role.GetString() != "toolResult" ||
                        !message.TryGetProperty("toolCallId", out var id) || id.GetString() != toolCallId) continue;
                    physicalResult = true;
                    physicalError |= message.TryGetProperty("isError", out var error) && error.GetBoolean();
                }
            }
            catch (Exception error) { logFailureType = error.GetType().Name; }
        var receipt = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, commandJoined = true, settlementCaptured = captured is not null,
            runtimeCleanupCompleted = captured?.RuntimeCleanup.IsCompletedSuccessfully ?? false,
            settlementCompleted = captured?.Completion.IsCompletedSuccessfully ?? false,
            phases, nodes, truncated,
            tool = new { toolCallId, outcomePresent = outcome is not null, outcomeIsError = outcome?.IsError,
                memoryResultPresent = memoryResult, physicalResultPresent = physicalResult,
                physicalResultIsError = physicalError, logFailureType }
        });
        if (Encoding.UTF8.GetByteCount(receipt.GetRawText()) <= 8192) return receipt;
        // Explicit fallback never exports exception messages, stacks or unrelated transcript data.
        return JsonSerializer.SerializeToElement(new { schemaVersion = 1, commandJoined = true,
            settlementCaptured = captured is not null, truncated = true, nodeCount = ids.Count,
            phaseCount = phases.Count, physicalResultPresent = physicalResult, physicalResultIsError = physicalError,
            logFailureType });
    }
}
