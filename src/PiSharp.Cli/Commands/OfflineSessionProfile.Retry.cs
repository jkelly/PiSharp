using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private AgentRetryPolicy? retryStartupPolicy;
    private Func<bool, CancellationToken, Task>? retryPersistence;

    internal void ConfigureRetrySettings(StartupSettingsSnapshot? settings,
        Func<bool, CancellationToken, Task>? persistEnabledOriginal)
    {
        if (Sessions is not null || retryStartupPolicy is not null) throw new InvalidOperationException("Retry startup admission must precede owning attachment.");
        if (persistEnabledOriginal is not null && persistEnabledOriginal.GetInvocationList().Length != 1)
            throw new ArgumentException("One acknowledged settings owner is required.", nameof(persistEnabledOriginal));
        retryStartupPolicy = settings?.RetryPolicy ?? AgentRetryPolicy.Default;
        retryPersistence = persistEnabledOriginal;
    }

    private void ConfigureRetrySession(PersistentAgentSession session)
    {
        if (retryStartupPolicy is null) return;
        var raw = SelectedModelDefinition.Raw.Value;
        var window = raw.TryGetProperty("contextWindow", out var value) && value.TryGetDouble(out var parsed) &&
            double.IsFinite(parsed) && parsed >= 0 ? parsed : 0;
        session.ConfigureAutomaticRetry(retryStartupPolicy, retryPersistence, window);
    }
}
