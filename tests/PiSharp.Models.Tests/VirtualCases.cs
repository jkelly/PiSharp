using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;

// Authored from packages/coding-agent/src/core/virtual-models.ts and model-runtime.ts (registerVirtualModel, resolveModel), following
// test/virtual-models.test.ts (v1.1.0) where its cases do not need an agent session.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> VirtualCases() =>
    [
        ("virtual.catalog-entry-shape", Sync(VirtualShape)),
        ("virtual.registration-listing-and-conflicts", Sync(VirtualRegistration)),
        ("virtual.routing-targets-physical-authenticated-models", VirtualRouting),
        ("virtual.branch-selection-and-state", Sync(VirtualBranch)),
        ("virtual.no-live-route", Sync(VirtualNoRoute)),
    ];

    private static VirtualModelDefinition Router(string provider, string id, Func<ModelRouteRequest, ModelRoute> route, string[]? levels = null) =>
        new(provider, id, "Router " + id, request => ValueTask.FromResult(route(request)), levels is null ? null : [.. levels]);

    private static void VirtualShape()
    {
        var model = VirtualModels.Create(new("router", "auto", "Auto", _ => throw new InvalidOperationException(), ["off", "high"], 1000, 100));
        Equal("pi-virtual", model.Api, "api"); Equal("", model.BaseUrl, "base URL"); Check(model.Reasoning, "reasoning from levels");
        Equal("""{"off":"off","minimal":null,"low":null,"medium":null,"high":"high","xhigh":null,"max":null}""", model.CloneJson()["thinkingLevelMap"]!.ToJsonString(), "level map");
        Names(["text", "image"], model.Input, "default input"); Equal(1000d, model.ContextWindow, "context"); Equal(100d, model.MaxTokens, "max tokens");
        Names(["off", "high"], VirtualModels.SupportedThinkingLevels(model), "supported levels");
        var plain = VirtualModels.Create(new("router", "plain", "Plain", _ => throw new InvalidOperationException()));
        Check(!plain.Reasoning && plain.ContextWindow == 0 && plain.MaxTokens == 0, "defaults: off only, unknown limits");
    }

    private static void VirtualRegistration()
    {
        Registry(out var registry, Env(("GROQ_API_KEY", "k")));
        registry.RegisterVirtualModel(Router("router", "auto", _ => throw new InvalidOperationException()));
        Check(registry.Find("router", "auto") is { Api: "pi-virtual" }, "listed under its own provider");
        Check(registry.HasConfiguredAuth("router") && registry.GetAvailable().Any(model => model.Reference == "router/auto"), "a virtual-only provider needs no credentials");
        Equal("Virtual model groq/openai/gpt-oss-120b conflicts with a physical model.",
            Throws<InvalidOperationException>(() => registry.RegisterVirtualModel(Router("groq", "openai/gpt-oss-120b", _ => throw new InvalidOperationException())), "conflict").Message, "conflict");
        Equal("Virtual model provider and id must not be empty.",
            Throws<InvalidOperationException>(() => registry.RegisterVirtualModel(Router(" ", "x", _ => throw new InvalidOperationException())), "empty").Message, "empty");
        registry.RegisterVirtualModel(Router("groq", "groq-router", _ => throw new InvalidOperationException()));
        var groq = registry.GetAll().Where(model => model.Provider == "groq").ToList();
        Equal("groq/groq-router", groq[^1].Reference, "virtual models follow the provider's physical models");
        registry.RegisterVirtualModel(Router("groq", "groq-router", _ => throw new InvalidOperationException(), ["off", "low"]));
        Check(registry.Find("groq", "groq-router")!.Reasoning, "re-registration replaces");
        registry.UnregisterVirtualModel("groq", "groq-router"); registry.UnregisterVirtualModel("router", "auto");
        Check(registry.Find("groq", "groq-router") is null && !registry.GetProviderIds().Contains("router"), "unregistered");
        var hidden = VirtualModels.WithVirtualModels([M("p", "same"), M("p", "other")], [VirtualModels.Create(new("p", "same", "Same", _ => throw new InvalidOperationException()))]);
        Names(["p/other|anthropic-messages", "p/same|pi-virtual"], hidden.Select(model => model.Reference + "|" + model.Api), "a virtual model hides a physical id");
    }

    private static async Task VirtualRouting()
    {
        Registry(out var registry, Env(("GROQ_API_KEY", "k")));
        ModelRouteRequest? seen = null;
        registry.RegisterVirtualModel(new("router", "auto", "Auto", request =>
        {
            seen = request;
            var target = request.Reason == ModelRouteReason.Retry ? request.Model : registry.Find("groq", "openai/gpt-oss-120b")!;
            return ValueTask.FromResult(new ModelRoute(target, "xhigh", JsonValue.Create("next")));
        }, ["off", "high"]));
        var auto = registry.Find("router", "auto")!;
        var branch = new BranchEntry[] { new AssistantEntry("groq", "openai/gpt-oss-120b", "openai-completions", "stop", "low"), new AssistantEntry("groq", "x", "openai-completions", "error") };
        var route = await registry.ResolveVirtualAsync(auto, branch, [], ModelRouteReason.User, "high", null, JsonValue.Create("state"), CancellationToken.None);
        Equal("groq/openai/gpt-oss-120b", route.Model.Reference, "physical target");
        Equal("high", route.ThinkingLevel, "xhigh clamped to the target's levels");
        Equal("next", route.State!.GetValue<string>(), "router state");
        Equal("groq/openai/gpt-oss-120b", seen!.Previous?.Model.Reference, "previous is the latest successful physical response");
        Equal("low", seen.Previous?.ThinkingLevel, "previous level"); Equal("state", seen.State!.GetValue<string>(), "state passed");
        Equal("Virtual model router/auto routed to router/auto, which is not a physical model.",
            (await ThrowsAsync<InvalidOperationException>(() => registry.ResolveVirtualAsync(auto, [], [], ModelRouteReason.Retry, "off", null, null, CancellationToken.None).AsTask(), "virtual target")).Message, "virtual target");
        registry.RegisterVirtualModel(new("router", "deep", "Deep", _ => ValueTask.FromResult(new ModelRoute(registry.Find("deepseek", "deepseek-v4-pro")!, "high"))));
        Equal("Virtual model router/deep routed to deepseek/deepseek-v4-pro, which has no credentials.",
            (await ThrowsAsync<InvalidOperationException>(() => registry.ResolveVirtualAsync(registry.Find("router", "deep")!, [], [], ModelRouteReason.Direct, "off", null, null, CancellationToken.None).AsTask(), "no credentials")).Message, "no credentials");
        Equal("Virtual model router/gone is not registered.",
            (await ThrowsAsync<InvalidOperationException>(() => registry.ResolveVirtualAsync(M("router", "gone", api: "pi-virtual"), [], [], ModelRouteReason.User, "off", null, null, CancellationToken.None).AsTask(), "unregistered")).Message, "unregistered");
    }

    private static void VirtualBranch()
    {
        Registry(out var registry, Env(("GROQ_API_KEY", "k")));
        registry.RegisterVirtualModel(Router("router", "auto", _ => throw new InvalidOperationException()));
        RegistryModel? Get(string provider, string id) => registry.Find(provider, id);
        // A virtual model_change holds while responses name the physical models it routed to.
        Equal(("router", "auto"), VirtualModels.GetBranchSelection([new ModelChangeEntry("router", "auto"),
            new AssistantEntry("groq", "openai/gpt-oss-120b", "openai-completions", "stop")], Get), "virtual holds");
        // A physical model_change is overridden by the latest physical response.
        Equal(("groq", "openai/gpt-oss-20b"), VirtualModels.GetBranchSelection([new ModelChangeEntry("groq", "openai/gpt-oss-120b"),
            new AssistantEntry("groq", "openai/gpt-oss-20b", "openai-completions", "stop")], Get), "latest physical response");
        // A virtual model that is no longer registered does not hold.
        Equal(("groq", "openai/gpt-oss-20b"), VirtualModels.GetBranchSelection([new ModelChangeEntry("router", "gone"),
            new AssistantEntry("groq", "openai/gpt-oss-20b", "openai-completions", "stop")], Get), "unregistered virtual does not hold");
        Equal(("router", "auto"), VirtualModels.GetBranchSelection([new AssistantEntry("groq", "a", "openai-completions", "stop"), new ModelChangeEntry("router", "auto")], Get), "latest change");
        Equal(null, VirtualModels.GetBranchSelection([new OtherEntry()], Get), "nothing recorded");
        Check(VirtualModels.FindLatestResponse([new AssistantEntry("p", "ok", "a", "stop"), new AssistantEntry("p", "bad", "a", "aborted")])!.Model == "ok", "aborted skipped");
        var state = new JsonObject { ["provider"] = "router", ["modelId"] = "auto", ["state"] = new JsonObject { ["turn"] = 2 } };
        Equal("""{"turn":2}""", VirtualModels.GetState([new CustomEntry("pi.virtual-model-state", new JsonObject { ["provider"] = "router", ["modelId"] = "auto", ["state"] = 1 }),
            new CustomEntry("pi.virtual-model-state", state), new CustomEntry("other", state)], "router", "auto")!.ToJsonString(), "latest state");
        Equal(null, VirtualModels.GetState([new CustomEntry("pi.virtual-model-state", state)], "router", "other"), "other model");
    }

    private static void VirtualNoRoute()
    {
        Registry(out var registry);
        registry.RegisterVirtualModel(Router("router", "auto", _ => throw new InvalidOperationException()));
        // IMPL-E: with its registry a virtual entry is a virtual selection (VirtualModelRoutingTransport.ForLive routes each request);
        // without one it has no route of its own and never reaches a provider.
        Check(LiveSessionSelection.FromEntry(registry.Find("router", "auto")!, registry, null).IsVirtual, "virtual selection over its registry");
        Equal("LiveApiUnavailable", Throws<LiveSessionException>(() => LiveSessionSelection.FromEntry(registry.Find("router", "auto")!, null, null), "virtual").Code,
            "a virtual model without its registry is never routed to a provider");
    }
}
