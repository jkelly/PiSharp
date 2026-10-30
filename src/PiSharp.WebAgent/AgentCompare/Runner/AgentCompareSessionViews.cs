// Decision 0011 (run W4, fix 2): what an agent compare model's durable session is, for its read-only view on the chat stream
// (ChatStreamMux's session/{session}/{agent} keys): the column's letter and model as its title, and the model's provider, model, account id
// and thinking level as the run chose them. Only sessions the store knows as a model's are answered, so the stream cannot be pointed at an
// arbitrary session; once a model is opened as a chat, its chat binding answers instead (SessionViews looks there first).
using PiSharp.WebAgent.Chat;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>The read-only views of agent compare models' sessions.</summary>
public sealed class AgentCompareSessionViews(AgentCompareStore store) : ISessionViewSource
{
    /// <inheritdoc/>
    public SessionView? Find(string sessionId)
    {
        if (store.FindSession(sessionId) is not { } found || found.Run.ModelSpec(found.Model.Slot) is not { } spec) return null;
        var title = $"{found.Model.Slot.ToUpperInvariant()}: {spec.Name ?? spec.Model} · agent compare {found.Run.Id}";
        return new SessionView(sessionId, title, spec.Provider, spec.Model, spec.Account, spec.Thinking);
    }
}
