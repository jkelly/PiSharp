// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/provider.ts. pi-ai's uuidv7 is Guid.CreateVersion7 in its
// lowercase hyphenated form.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>The built-in provider identity (upstream <c>provider.ts</c>).</summary>
public static class ProviderIdentity
{
    /// <summary>Stable provider-facing identity of one conversation, <c>{sessionId}</c>; every fork starts with a fresh identity instead of
    /// copying its parent (upstream <c>ProviderDoc</c>).</summary>
    public static ConversationDocToken ProviderDoc { get; } = Documents.DefineConversationDoc("pi.provider", 1, DocumentFork.Initial,
        () => JsonValue.Object([KeyValuePair.Create("sessionId", (JsonValue)Guid.CreateVersion7().ToString())]), checkpointWhen: (_, _, _) => true);

    /// <summary>
    /// Returns the persisted identity without writing in the normal path (upstream <c>ensureProviderSessionId</c>). A legacy conversation
    /// without <c>pi.provider</c> gets one migration commit whose document access runs the initial value before the provider request starts.
    /// </summary>
    public static async Task<string> EnsureProviderSessionIdAsync(ITaskRuntime runtime, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var existing = await runtime.SnapshotAsync(ProviderDoc, runtime.ConversationId, context).ConfigureAwait(false);
        if (existing is not null) return ((JsonStringValue)existing["sessionId"]).Value;
        string? created = null;
        await runtime.CommitAsync(async (tx, _) =>
        {
            created = ((JsonStringValue)(await tx.DocAsync(ProviderDoc, runtime.ConversationId).ConfigureAwait(false)).Get("sessionId")!).Value;
        }, context).ConfigureAwait(false);
        return created ?? throw new InvalidOperationException($"Conversation {runtime.ConversationId} has no provider session ID");
    }
}
