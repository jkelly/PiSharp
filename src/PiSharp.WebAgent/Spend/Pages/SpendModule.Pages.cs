// Decision 0011 (run W3, S4): the Pages unit's hooks of SpendModule: the read side of Settings › Spend and History › Spend (SpendPages,
// which the views, SpendController and the provider pages' account cards use) and the JSON API under /api/spend (SpendEndpoints). The page
// routes themselves are SpendController's.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Spend;

public static partial class SpendModule
{
    static partial void AddPages(IServiceCollection services, WebAgentOptions options) => services.TryAddSingleton<SpendPages>();

    static partial void MapPages(WebApplication app) => app.MapSpendEndpoints();
}
