// Decision 0011 (run W2, WU4): the Web Agent's core hosting, which Program.cs composes with the area modules. The app answers only this
// computer: it binds 127.0.0.1:5180 unless --urls names another loopback address (anything else is refused at start), and HostGuard from
// PiSharp.Web.Common turns away a foreign Host (421), a cross-origin write (403) and a non-JSON API write (415), and sends the strict
// Content-Security-Policy the hub sends. There are no login pages: the single user is whoever can reach loopback. The Data Protection key
// ring lives in keys\, protected with DPAPI on Windows as in the hub, and is created at start. Fallback registrations (faux models, the faux
// catalog, no recent chats) apply only where an area module registered nothing; --faux replaces the models and catalog whatever was registered.
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Durable;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;

namespace PiSharp.WebAgent.Hosting;

/// <summary>Core service registration and pipeline of the Web Agent.</summary>
public static class WebAgentHost
{
    /// <summary>The address the app binds when <c>--urls</c> does not name one.</summary>
    public const string DefaultUrls = "http://127.0.0.1:5180";

    /// <summary>The Data Protection application name, which isolates the key ring's purposes from other apps.</summary>
    public const string DataProtectionApplicationName = "PiSharpWebAgent";

    /// <summary>The 421 answer to a foreign Host.</summary>
    public const string MisdirectedMessage = "Misdirected request: PiSharp Web Agent answers only localhost.";

    /// <summary>A builder whose content root is the output folder (so appsettings.json is found from any working directory), bound to
    /// <see cref="DefaultUrls"/> unless the configuration's <c>urls</c> names loopback addresses, and serving static web assets from a build
    /// output.</summary>
    /// <exception cref="InvalidOperationException">The configured <c>urls</c> name an address other than 127.0.0.1, localhost or [::1].</exception>
    public static WebApplicationBuilder CreateBuilder(WebAgentOptions options, string[] args)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        var urls = builder.Configuration["urls"];
        if (string.IsNullOrWhiteSpace(urls)) builder.WebHost.UseUrls(DefaultUrls);
        else CheckLoopbackUrls(urls);
        // A build output serves wwwroot and PiSharp.Web.Common's _content through its static web assets manifest, a publish output from wwwroot.
        builder.WebHost.UseStaticWebAssets();
        return builder;
    }

    /// <summary>Refuses any address in <paramref name="urls"/> (separated by <c>;</c>) that is not http(s) on 127.0.0.1, localhost or [::1]:
    /// the app holds provider keys and has no login, so it must never listen beyond this computer.</summary>
    /// <exception cref="InvalidOperationException">An address is not loopback (including <c>*</c>, <c>+</c> and <c>0.0.0.0</c>).</exception>
    public static void CheckLoopbackUrls(string urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var loopback = Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" &&
                (parsed.Host == "localhost" || (IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var address) &&
                    (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))));
            if (!loopback)
                throw new InvalidOperationException(
                    $"--urls \"{url}\" is not this computer: the app has no login, so it listens only on http://127.0.0.1, http://localhost or http://[::1].");
        }
    }

    /// <summary>Registers the core: <see cref="WebAgentOptions"/>, Data Protection (keys\, DPAPI on Windows), <see cref="WebAgentDb"/> with the
    /// core and settings migrations, <see cref="ISettingsStore"/>, PiSharp.Web.Common's renderers, HostGuard with the default
    /// Content-Security-Policy, and MVC.</summary>
    public static IServiceCollection AddWebAgentCore(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        var protection = services.AddDataProtection().SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(options.KeysDirectory));
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        services.AddSingleton(provider => new WebAgentDb(provider.GetRequiredService<WebAgentOptions>(), provider.GetServices<IWebAgentMigration>(),
            provider.GetService<ILogger<WebAgentDb>>()));
        foreach (var migration in CoreMigrations.All) services.AddWebAgentMigration(migration);
        services.AddWebAgentMigration(SettingsStore.Migration);
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddWebCommon();
        services.AddHostGuard(guard =>
        {
            guard.MisdirectedMessage = MisdirectedMessage;
            guard.ContentSecurityPolicy = HostGuardOptions.DefaultContentSecurityPolicy;
        });
        services.AddControllersWithViews();
        return services;
    }

    /// <summary>Registers what no area module registered: <see cref="IDurableModels"/> = the faux models, <see cref="IModelCatalog"/> =
    /// <see cref="FauxModelCatalog"/> and <see cref="IRecentChats"/> = <see cref="EmptyRecentChats"/>. With <see cref="WebAgentOptions.Faux"/>,
    /// the faux models and catalog replace any registered ones, so no provider code path is reachable. Call it after the area modules.</summary>
    public static IServiceCollection AddWebAgentFallbacks(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(_ => FauxModelCatalog.CreateModels());
        if (options.Faux)
        {
            services.RemoveAll<IDurableModels>();
            services.RemoveAll<IModelCatalog>();
        }
        services.TryAddSingleton<IDurableModels>(provider => provider.GetRequiredService<FauxDurableModels>());
        services.TryAddSingleton<IModelCatalog, FauxModelCatalog>();
        services.TryAddSingleton<IRecentChats, EmptyRecentChats>();
        return services;
    }

    /// <summary>Prepares the data folder (folders, webagent.db migrations, the Data Protection key ring) and adds HostGuard first, then the
    /// static files.</summary>
    public static WebApplication UseWebAgentCore(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<WebAgentOptions>();
        options.EnsureDirectories();
        app.Services.GetRequiredService<WebAgentDb>().Migrate();
        // Data Protection creates its key lazily; protecting a probe creates (or loads) it now, so a broken key ring or DPAPI fails the start
        // instead of the first credential save.
        app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("PiSharp.WebAgent.KeyRing.v1").Protect([1]);
        app.UseHostGuard();
        // Revalidate every static file (a 304 on loopback costs nothing): page scripts import modules by plain URL (site.js -> shell.js), so a
        // cached old module would otherwise outlive an update.
        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache" });
        return app;
    }

    /// <summary>Maps <c>GET /api/health</c> (<c>{status, faux}</c>, for scripts and tests waiting for the app) and the MVC controllers.</summary>
    public static WebApplication MapWebAgentCore(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/health", (WebAgentOptions options) => Results.Json(new { status = "ok", faux = options.Faux }));
        app.MapControllers();
        return app;
    }
}
