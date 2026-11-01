// Decision 0011 (run W2, WU3): how an app takes PiSharp.Web.Common. Registration is explicit, so each app's Program.cs shows what it uses:
// AddWebCommon for the renderers, AddHostGuard + UseHostGuard for the guard (first in the pipeline, before static files).
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace PiSharp.Web.Common;

/// <summary>Service and pipeline registration for PiSharp.Web.Common.</summary>
public static class WebCommonExtensions
{
    /// <summary>Registers <see cref="FragmentRenderer"/> and <see cref="MarkdownRenderer"/> as singletons. The app still calls
    /// <c>AddControllersWithViews</c> (or <c>AddRazorPages</c>) for the view engine the fragment renderer uses.</summary>
    public static IServiceCollection AddWebCommon(this IServiceCollection services)
    {
        services.AddSingleton<FragmentRenderer>();
        services.AddSingleton<MarkdownRenderer>();
        return services;
    }

    /// <summary>Registers <see cref="HostGuardOptions"/>, changed by <paramref name="configure"/>, for <see cref="UseHostGuard"/>.</summary>
    public static IServiceCollection AddHostGuard(this IServiceCollection services, Action<HostGuardOptions>? configure = null)
    {
        var options = services.AddOptions<HostGuardOptions>();
        if (configure is not null) options.Configure(configure);
        return services;
    }

    /// <summary>Adds <see cref="HostGuard"/> to the pipeline. Call it first, so nothing (static files included) answers a foreign host.</summary>
    public static IApplicationBuilder UseHostGuard(this IApplicationBuilder app) => app.UseMiddleware<HostGuard>();
}
