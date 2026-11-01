// Decision 0011 (run W4, unit R9): Settings › Data & storage's entry points, which Program.cs calls last of the areas (after the custom
// providers, the runs and agent compare, whose data the page reports on and changes):
//   - DataStorageOptions (DataStorage:OpenFolders from configuration, default true) and the IFolderOpener (the platform's file manager);
//   - the export parts (IDataExportPart, TryAddEnumerable): the settings sections and, when the custom providers area is registered, its
//     models.json; both are also IDataResetPart, the parts "Reset all settings" sets back. Another area adds its own part (Presets, R8) the
//     same way, from its own module, and the file, the import review and the reset pick it up;
//   - DataExport, DataStorageService and the archive purge (ChatArchiveJanitor, a hosted service: at start and daily).
// Every registration is a TryAdd, so a test can put its own first (an opener that records instead of starting Explorer). MapDataStorageArea
// maps /api/data; the page is DataController's (GET /settings/data).
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.CustomProviders;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>Registration and endpoints of Settings › Data &amp; storage.</summary>
public static class DataStorageModule
{
    /// <summary>Registers the area's services. Call it after the areas it reports on (custom providers, runs, agent compare).</summary>
    public static IServiceCollection AddDataStorageArea(this IServiceCollection services, WebAgentOptions options, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(new DataStorageOptions { OpenFolders = configuration?.GetValue(DataStorageOptions.OpenFoldersKey, true) ?? true });
        services.TryAddSingleton<IFolderOpener, SystemFolderOpener>();

        services.TryAddSingleton(provider => new SettingsExportPart(provider.GetRequiredService<ISettingsStore>(), provider.GetService<IModelCatalog>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataExportPart, SettingsExportPart>(provider => provider.GetRequiredService<SettingsExportPart>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataResetPart, SettingsExportPart>(provider => provider.GetRequiredService<SettingsExportPart>()));
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CustomProviderService)))
        {
            services.TryAddSingleton(provider => new CustomProvidersExportPart(provider.GetRequiredService<CustomProviderService>()));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataExportPart, CustomProvidersExportPart>(provider => provider.GetRequiredService<CustomProvidersExportPart>()));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataResetPart, CustomProvidersExportPart>(provider => provider.GetRequiredService<CustomProvidersExportPart>()));
        }

        services.TryAddSingleton(provider => new DataExport(provider.GetServices<IDataExportPart>(), provider.GetService<TimeProvider>()));
        services.TryAddSingleton(provider => new DataStorageService(options, provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<DataStorageOptions>(), provider.GetRequiredService<IFolderOpener>(), provider.GetRequiredService<DataExport>(), provider,
            provider.GetService<TimeProvider>(), provider.GetService<ILogger<DataStorageService>>()));
        services.TryAddSingleton(provider => new ChatArchiveJanitor(options, provider.GetRequiredService<DataStorageOptions>(),
            provider.GetService<TimeProvider>(), provider.GetService<ILogger<ChatArchiveJanitor>>()));
        services.AddHostedService(provider => provider.GetRequiredService<ChatArchiveJanitor>());
        return services;
    }

    /// <summary>Maps <c>/api/data</c>.</summary>
    public static WebApplication MapDataStorageArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapDataEndpoints();
        return app;
    }
}
