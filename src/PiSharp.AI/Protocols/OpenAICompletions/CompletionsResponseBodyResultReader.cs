namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>An invocation-owned result executor. Empty nonterminal chunks differ from physical EOF.</summary>
public interface ICompletionsResponseBodyResultReader
{
    ValueTask<CompletionsBodyReadResult> ReadAsync(int maximumBytes, CancellationToken cancellationToken = default);
    ValueTask CancelAsync();
    void Release();
}

/// <summary>Creates a result executor after response observation over borrowed source or canonical input.</summary>
public delegate ValueTask<ICompletionsResponseBodyResultReader> CompletionsResponseBodyResultReaderFactory(
    Stream body, CancellationToken cancellationToken);
