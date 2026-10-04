using System.Text;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;

namespace PiSharp.Cli.Output;

public enum SessionPrintOutcome { NoAssistant, Completed, ProviderError, Aborted }

/// <summary>Final-message text projection over borrowed output. The command owns durable settlement before this call.</summary>
public static class SessionPrintOutput
{
    public const int MaximumOutputBytes = 1_048_576;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<SessionPrintOutcome> WriteAsync(TextWriter output, TranscriptEntry? lastMessage)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (lastMessage is null || lastMessage.Role != "assistant")
        {
            await output.FlushAsync().ConfigureAwait(false);
            return SessionPrintOutcome.NoAssistant;
        }
        var assistant = PiWireJson.ReadMessage(lastMessage.WireBody.Value);
        if (assistant.StopReason is StopReason.Error or StopReason.Aborted)
        {
            await output.FlushAsync().ConfigureAwait(false);
            return assistant.StopReason == StopReason.Aborted ? SessionPrintOutcome.Aborted : SessionPrintOutcome.ProviderError;
        }
        if (assistant.StopReason is StopReason.Pending or StopReason.Deferred)
            throw new SessionCommandException(SessionCommandFailure.CommandFailed);
        long characters = 0, bytes = 0;
        // Preflight all text before writing a prefix; preserve existing LF/CR/NUL/Unicode without rewriting.
        foreach (var text in assistant.Content.OfType<TextContent>())
        {
            characters += text.Text.Length + 1L;
            if (characters > MaximumOutputBytes)
                throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
            bytes += Utf8.GetByteCount(text.Text) + 1L;
            if (bytes > MaximumOutputBytes)
                throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
        }
        foreach (var text in assistant.Content.OfType<TextContent>())
            await output.WriteAsync(text.Text + "\n").ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
        return SessionPrintOutcome.Completed;
    }
}
