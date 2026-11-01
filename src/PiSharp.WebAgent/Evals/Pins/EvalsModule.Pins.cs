// Decision 0011 (run W5, E7): the Pins unit's part of the Evals area: PinService (TryAdd, so a test can register its own first) and
// /api/evals/pins (PinEndpoints; the modal fragment is PinsController's GET /api/evals/pins/modal).
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Evals.Pins;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Evals;

public static partial class EvalsModule
{
    static partial void AddPins(IServiceCollection services, WebAgentOptions options) =>
        services.TryAddSingleton(provider => new PinService(provider));

    static partial void MapPins(WebApplication app) => app.MapPinEndpoints();
}
