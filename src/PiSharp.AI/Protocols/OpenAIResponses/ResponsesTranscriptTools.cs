// Pi v1.5.0 utils/transcript.ts resolveTranscriptTools and openai-responses-shared.ts getResponsesToolSupport.
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAIResponses;

internal static class ResponsesTranscriptTools
{
    // Changes can replace earlier inline declarations. Tool search can only add; removals and redeclarations move its boundary.
    internal static int FoldedSystems(ChatRequest request, ResponsesTranscriptProjectionOptions options, CancellationToken token)
    {
        if (!options.SupportsMidConversationSystemMessages || !options.SupportsAdditionalTools && !options.SupportsToolSearch)
            return int.MaxValue;
        if (options.SupportsAdditionalTools) return 0;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var folded = request.Messages.Length > 0 && request.Messages[0].Role == "system" ? 1 : 0;
        var systemIndex = 0;
        foreach (var entry in request.Messages)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Role != "system") continue;
            var body = entry.WireBody.Value;
            if (body.TryGetProperty("toolsRemoved", out var removed))
            {
                if (removed.ValueKind != JsonValueKind.Array) throw new ResponsesProjectionException(ResponsesProjectionFailure.InvalidTranscript);
                if (removed.GetArrayLength() > 0) folded = systemIndex + 1;
            }
            if (body.TryGetProperty("toolsAdded", out var added))
            {
                if (added.ValueKind != JsonValueKind.Array) throw new ResponsesProjectionException(ResponsesProjectionFailure.InvalidTranscript);
                foreach (var tool in added.EnumerateArray())
                    if (!declared.Add(tool.GetProperty("name").GetString()!)) folded = systemIndex + 1;
            }
            systemIndex++;
        }
        return folded;
    }
}
