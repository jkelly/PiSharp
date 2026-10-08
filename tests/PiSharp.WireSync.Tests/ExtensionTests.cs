using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using static Assert;

// ext.tool-renderer (1.0.1/1.1.0), ext.loadout-namespace (1.0.4), ext.command-validation (0.99.2).
internal static class ExtensionTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("ext.tool-renderer.resolvers-in-load-order-then-registered-tool-then-fallback", RendererOrder);
        yield return ("ext.tool-renderer.render-context-duration-and-output-pad", RenderContext);
        yield return ("ext.loadout.namespace-instructions-and-prompt-guidelines", Loadout);
        yield return ("ext.command-validation.nameless-or-handlerless-commands-fail-load", CommandValidation);
    }

    private sealed class Extension(Func<IExtensionRegistry, ValueTask> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken) => initialize(registry);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ExtensionToolDescriptor Tool(string name) => new("tool-" + name, name, "Authored " + name,
        JsonData.Parse("""{"type":"object","properties":{}}"""), (_, _, _) => ValueTask.FromResult(JsonData.Parse("""{"content":[]}""")));

    private static ExtensionToolRenderers Rows(string label) => new(ExtensionToolRenderShell.Default,
        (context, width, token) => Task.FromResult(new ExtensionCustomComponentRows([label + ":" + context.ToolName], [width])));

    private static async Task<string> Label(ExtensionToolRenderers? renderers, string toolName) => renderers?.RenderCall is null ? "none" :
        (await renderers.RenderCall(new(toolName, "call", JsonData.EmptyObject, "/w", false, true, false, false, false, false, null, 0), 80, default)).Rows[0];

    private static async Task RendererOrder()
    {
        var registry = new ExtensionRegistry();
        Check(registry.AvailableFeatures.Contains(ExtensionToolRendererFeatures.Feature), "renderer feature not advertised");
        var trace = new List<string>();
        IExtensionRegistration? withdrawn = null;
        // First loaded: fills in only (next() ?? mine), so later resolvers and the registered tool win.
        await registry.ActivateAsync("first", new Extension(registry =>
        {
            ((IExtensionToolRendererRegistry)registry).RegisterToolRenderer(new("first-renderer", (name, next) =>
            { trace.Add("first:" + name); return next() ?? Rows("first"); }));
            return ValueTask.CompletedTask;
        }));
        // Second loaded: overrides bash and owns the MCP namespace, also for tools that are not registered yet.
        await registry.ActivateAsync("second", new Extension(registry =>
        {
            var renderers = (IExtensionToolRendererRegistry)registry;
            renderers.RegisterToolRenderer(new("second-renderer", (name, next) =>
            {
                trace.Add("second:" + name);
                return name == "bash" || name.StartsWith("mcp__", StringComparison.Ordinal) ? Rows("second") : next();
            }));
            withdrawn = renderers.RegisterToolRenderer(new("withdrawn-renderer", (name, next) => { trace.Add("withdrawn:" + name); return next(); }));
            registry.RegisterTool(Tool("owned") with { Renderers = Rows("owned") });
            return ValueTask.CompletedTask;
        }));
        var snapshot = registry.CaptureSnapshot();
        Equal("first-renderer|second-renderer|withdrawn-renderer", string.Join("|", snapshot.ToolRenderers.Select(row => row.RegistrationId)));
        Equal("owned:owned", await Label(registry.ResolveToolRenderers(snapshot, "owned"), "owned"));
        Equal("first:owned|second:owned|withdrawn:owned", string.Join("|", trace)); trace.Clear();
        Equal("second:bash", await Label(registry.ResolveToolRenderers(snapshot, "bash", () => Rows("builtin")), "bash"));
        Equal("second:mcp__docs__search", await Label(registry.ResolveToolRenderers(snapshot, "mcp__docs__search"), "mcp__docs__search"));
        // A built-in fallback is used when nothing else applies; with no fallback the first resolver fills in.
        Equal("builtin:read", await Label(registry.ResolveToolRenderers(snapshot, "read", () => Rows("builtin")), "read"));
        Equal("first:read", await Label(registry.ResolveToolRenderers(snapshot, "read"), "read"));
        trace.Clear();
        withdrawn!.Dispose();
        snapshot = registry.CaptureSnapshot();
        _ = registry.ResolveToolRenderers(snapshot, "read");
        Equal("first:read|second:read", string.Join("|", trace));
        var invalid = await Throws<ExtensionRegistrationException>(() => registry.ActivateAsync("invalid", new Extension(registry =>
        {
            ((IExtensionToolRendererRegistry)registry).RegisterToolRenderer(new("null-renderer", null!));
            return ValueTask.CompletedTask;
        })));
        Equal(ExtensionRegistrationFailure.InvalidDescriptor, invalid.Failure);
        await registry.DisposeAsync();
    }

    private static async Task RenderContext()
    {
        // The source durationMs/outputPad render context members reach the chosen renderer unchanged.
        var renderers = new ExtensionToolRenderers(ExtensionToolRenderShell.Self, RenderResult: (result, context, width, token) =>
            Task.FromResult(new ExtensionCustomComponentRows(
                [new string(' ', context.OutputPad) + "Took " + (context.DurationMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-") + "ms"], [width])));
        var final = new ExtensionToolRenderContext("bash", "call", JsonData.EmptyObject, "/w", true, true, false, false, false, false, 1250, 2);
        Equal("  Took 1250ms", (await renderers.RenderResult!(JsonData.EmptyObject, final, 80, default)).Rows[0]);
        Equal("Took -ms", (await renderers.RenderResult!(JsonData.EmptyObject, final with { IsPartial = true, DurationMs = null, OutputPad = 0 }, 80, default)).Rows[0]);
        Equal(ExtensionToolRenderShell.Self, renderers.RenderShell);
    }

    private sealed class Allow : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(true));
    }

    private static async Task Loadout()
    {
        var registry = new ExtensionRegistry();
        ToolLoadout? seen = null;
        var docs = new ToolNamespace("mcp__docs", "Documentation search") { Instructions = "Search before reading; cite page ids." };
        await registry.ActivateAsync("loadout", new Extension(registry =>
        {
            registry.RegisterTool(Tool("docs_search") with { Namespace = docs, PromptGuidelines = ["Use docs_search for product questions."] });
            registry.RegisterTool(Tool("orchestrator") with { PrepareLoadout = loadout => { seen = loadout; return null; } });
            return ValueTask.CompletedTask;
        }));
        var info = registry.CaptureSnapshot().Tools.Single(tool => tool.Name == "docs_search");
        Equal("Search before reading; cite page ids.", info.Namespace?.Instructions);
        Equal("Use docs_search for product questions.", info.PromptGuidelines.Single());
        _ = new ExtensionAgentBinding(registry, new Allow(), (_, _, _) => ValueTask.FromResult(true));
        Check(seen is not null, "loadout preparation did not run");
        Equal("Use docs_search for product questions.", string.Join("|", seen!.GetPromptGuidelines("docs_search")));
        Check(seen.GetPromptGuidelines("orchestrator").IsEmpty && seen.GetPromptGuidelines("unknown").IsEmpty, "absent guidelines");
        Equal(docs, seen.GetNamespace("docs_search"));
        Equal("Documentation search", seen.GetNamespace("docs_search")!.Description);
        // The plain contract has the same semantics without a registry.
        var plain = new ToolLoadout([], [], [new(JsonData.Parse("""{"name":"t","description":"d"}"""), ToolExposure.Direct) { PromptGuidelines = ["g1", "g2"] }]);
        Equal("g1|g2", string.Join("|", plain.GetPromptGuidelines("t")));
        var invalid = await Throws<ExtensionRegistrationException>(() => registry.ActivateAsync("bad-guidelines", new Extension(registry =>
        { registry.RegisterTool(Tool("bad") with { PromptGuidelines = default }); return ValueTask.CompletedTask; })));
        Equal(ExtensionRegistrationFailure.InvalidDescriptor, invalid.Failure);
        await registry.DisposeAsync();
    }

    private static async Task CommandValidation()
    {
        var registry = new ExtensionRegistry();
        foreach (var (owner, command) in new[]
        {
            ("nameless", new ExtensionCommandDescriptor("nameless-command", "", "Has no name", (_, _, _) => ValueTask.CompletedTask)),
            ("null-name", new ExtensionCommandDescriptor("null-name-command", null!, "Has no name", (_, _, _) => ValueTask.CompletedTask)),
            ("handlerless", new ExtensionCommandDescriptor("handlerless-command", "deploy", "Has no handler", null!))
        })
        {
            var error = await Throws<ExtensionRegistrationException>(() => registry.ActivateAsync(owner, new Extension(registry =>
            { registry.RegisterCommand(command); return ValueTask.CompletedTask; })));
            Equal(ExtensionRegistrationFailure.InvalidDescriptor, error.Failure, owner);
            Check(registry.CaptureSnapshot().Commands.IsEmpty, "an invalid command loaded");
        }
        // The failed owners are fully retired: the same owner identity loads once the command is valid.
        await registry.ActivateAsync("handlerless", new Extension(registry =>
        { registry.RegisterCommand(new("deploy-command", "deploy", "Deploys", (_, _, _) => ValueTask.CompletedTask)); return ValueTask.CompletedTask; }));
        Equal("deploy", registry.CaptureSnapshot().Commands.Single().Name);
        await registry.DisposeAsync();
    }
}
