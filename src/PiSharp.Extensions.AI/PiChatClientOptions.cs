using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.AI;

namespace PiSharp.Extensions.AI;

/// <summary>
/// Well-known <see cref="AdditionalPropertiesDictionary"/> keys recognized by <see cref="PiChatClient"/>,
/// <see cref="ChatClientTransportAdapter"/> and <see cref="PiEmbeddingGenerator"/>.
/// </summary>
public static class PiChatAdditionalProperties
{
    /// <summary>PiSharp thinking level string (e.g. <c>off</c>, <c>minimal</c>, <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, <c>max</c>).</summary>
    public const string ThinkingLevel = "pisharp.thinkingLevel";

    /// <summary>PiSharp service tier string (see <see cref="ServiceTiers"/>: <c>auto</c>, <c>normal</c>, <c>economy</c>, <c>priority</c>, <c>fast</c>, <c>ultrafast</c>).</summary>
    public const string ServiceTier = "pisharp.serviceTier";

    /// <summary>PiSharp session identifier forwarded to <see cref="ChatRequest.SessionId"/>.</summary>
    public const string SessionId = "pisharp.sessionId";

    /// <summary>Boolean flag forwarded to <see cref="ChatRequest.DisableContinuation"/>.</summary>
    public const string DisableContinuation = "pisharp.disableContinuation";

    /// <summary><see cref="ClientIdentity"/> instance forwarded to <see cref="ChatRequest.Identity"/>.</summary>
    public const string Identity = "pisharp.identity";

    /// <summary>Embedding task type string (e.g. Gemini <c>RETRIEVAL_QUERY</c>, <c>SEMANTIC_SIMILARITY</c>).</summary>
    public const string TaskType = "pisharp.taskType";

    /// <summary>Provider-specific raw JSON object (<see cref="PiSharp.Contracts.JsonData"/> or <see cref="JsonElement"/>) merged into embedding requests.</summary>
    public const string EmbeddingOptions = "pisharp.embeddingOptions";

    /// <summary>Opaque provider signature attached to a tool call (e.g. Gemini <c>thoughtSignature</c>), preserved across multi-turn round trips.</summary>
    public const string ThoughtSignature = "pisharp.thoughtSignature";

    /// <summary>Opaque provider signature attached to an assistant text block (e.g. OpenAI Responses / Gemini <c>textSignature</c>).</summary>
    public const string TextSignature = "pisharp.textSignature";

    /// <summary>Boolean flag marking a redacted thinking block (<c>redacted: true</c>).</summary>
    public const string ThinkingRedacted = "pisharp.thinkingRedacted";
}

/// <summary>Default request configuration and lifecycle options for <see cref="PiChatClient"/>.</summary>
public sealed record PiChatClientOptions
{
    /// <summary>Default <see cref="ChatRequest.ThinkingLevel"/> when per-call <see cref="ChatOptions"/> does not override it.</summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>Default <see cref="ChatRequest.ServiceTier"/> when per-call <see cref="ChatOptions"/> does not override it.</summary>
    public string? ServiceTier { get; init; }

    /// <summary>Default <see cref="ChatRequest.SessionId"/> when per-call <see cref="ChatOptions"/> does not override it.</summary>
    public string? SessionId { get; init; }

    /// <summary>Default <see cref="ChatRequest.DisableContinuation"/> value.</summary>
    public bool DisableContinuation { get; init; }

    /// <summary>Default <see cref="ChatRequest.Identity"/> when per-call <see cref="ChatOptions"/> does not override it.</summary>
    public ClientIdentity? Identity { get; init; }

    /// <summary>Optional provider endpoint URI reported via <see cref="ChatClientMetadata.ProviderUri"/>.</summary>
    public Uri? ProviderUri { get; init; }

    /// <summary>Optional clock for request timestamps; defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>When <see langword="true"/>, disposing <see cref="PiChatClient"/> also disposes the underlying transport if it implements <see cref="IDisposable"/>.</summary>
    public bool DisposeTransport { get; init; }
}

/// <summary>Configuration and lifecycle options for <see cref="ChatClientTransportAdapter"/>.</summary>
public sealed record ChatClientTransportAdapterOptions
{
    /// <summary>Optional baseline <see cref="ChatOptions"/> cloned for every adapted request before applying transcript tools and per-request properties.</summary>
    public ChatOptions? BaseOptions { get; init; }

    /// <summary>When <see langword="true"/>, disposing <see cref="ChatClientTransportAdapter"/> also disposes the underlying <see cref="IChatClient"/>.</summary>
    public bool DisposeChatClient { get; init; }
}
