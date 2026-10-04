namespace PiSharp.Contracts;

/// <summary>Native diagnostic provenance; zero and undefined values are invalid at admission.</summary>
public enum NativeChatAdapter { OpenAICompletions = 1, PiMessages = 2, GoogleGenerativeAI = 3, MistralConversations = 4 }

/// <summary>Closed native failure codes. These grant no retry, tool or durable-settlement authority.</summary>
public enum NativeChatFailureCode
{
    SourceFailed = 1, MalformedStream, UnexpectedEof, ResourceLimit, ProviderError,
    Cancelled, CleanupFailed, UnsupportedFeature
}

/// <summary>Native-only bounded classification, separate from Pi messages and private exception text.</summary>
public sealed record NativeChatDiagnostic(NativeChatAdapter Adapter, NativeChatFailureCode Code);
