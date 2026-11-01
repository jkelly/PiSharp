// Decision 0011 (run W3, S2): the Ledger unit's registration: the ledger's webagent.db migrations (1000-1099), SpendLedger as the app's
// ISpendLedger (one instance, so one lock guards every reservation), and SpendRecovery, which marks reservations a previous run left
// reserved as uncertain at start. The ledger has no endpoints of its own: the Pages unit maps /api/spend. W3 fix 2 (spend-core): the ledger is
// made only once the app holds its data folder's lock (WebAgentDataLock, registered by the core), whichever hosted service asks for it first,
// so its startup recovery can never mark the live reservations of another app on the same folder uncertain: that app holds the lock, and
// this one fails to start instead.
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Spend;

public static partial class SpendModule
{
    static partial void AddLedger(IServiceCollection services, WebAgentOptions options)
    {
        foreach (var migration in SpendLedgerMigrations.All) services.AddWebAgentMigration(migration);
        services.AddSingleton(provider =>
        {
            // The app's lock (taken now if nothing took it yet; DataFolderLockedException while another app holds it). A composition without
            // the core (some tests) has no data folder of its own to lock.
            _ = provider.GetService<WebAgentDataLock>();
            return new SpendLedger(provider.GetRequiredService<WebAgentDb>(), provider.GetRequiredService<ISettingsStore>(),
                provider.GetService<TimeProvider>(), provider.GetService<ILogger<SpendLedger>>());
        });
        services.AddSingleton<ISpendLedger>(provider => provider.GetRequiredService<SpendLedger>());
        services.AddSingleton<SpendRecovery>();
        services.AddHostedService(provider => provider.GetRequiredService<SpendRecovery>());
    }
}
