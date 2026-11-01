using System.Reflection;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;

// The extraction boundary (W2 brief, WU2): PiSharp.ModelRuntime never references PiSharp.Cli, directly or through another assembly, and
// the CLI's own parts (auth.json, the llama.cpp provider, login prompts) did not move with it.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> BoundaryCases() =>
    [
        Case("boundary.model-runtime-never-references-the-cli", BoundaryNoCliReference),
        Case("boundary.cli-owned-types-stayed-in-the-cli", BoundaryCliTypes),
        Case("boundary.namespaces-kept-and-session-command-failure-moved", BoundaryNamespaces),
    ];

    private static Assembly ModelRuntime => typeof(ModelRegistry).Assembly;

    private static void BoundaryNoCliReference()
    {
        Equal("PiSharp.ModelRuntime", ModelRuntime.GetName().Name, "the registry's assembly");
        Check(!ModelRuntime.GetReferencedAssemblies().Any(reference => reference.Name == "PiSharp.Cli"), "PiSharp.ModelRuntime references PiSharp.Cli");
        // The whole PiSharp closure: no assembly the runtime loads references the CLI either.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<AssemblyName>([ModelRuntime.GetName()]);
        while (pending.TryDequeue(out var name))
        {
            if (name.Name is not { } simple || !simple.StartsWith("PiSharp.", StringComparison.Ordinal) || !seen.Add(simple)) continue;
            Check(simple != "PiSharp.Cli", "PiSharp.Cli is in the closure of PiSharp.ModelRuntime");
            foreach (var reference in Assembly.Load(name).GetReferencedAssemblies()) pending.Enqueue(reference);
        }
        Check(seen.Contains("PiSharp.AI") && seen.Contains("PiSharp.CodingAgent"), "closure walked: " + string.Join(",", seen));
        Check(!typeof(Program).Assembly.GetReferencedAssemblies().Any(reference => reference.Name == "PiSharp.Cli"), "this suite references PiSharp.Cli");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "PiSharp.Cli"), "PiSharp.Cli was loaded");
    }

    private static void BoundaryCliTypes()
    {
        var names = ModelRuntime.GetTypes().Select(type => type.FullName ?? type.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var type in new[]
        {
            "PiSharp.Cli.Authentication.AuthJsonCredentialStore", "PiSharp.Cli.Authentication.ProviderLoginHost",
            "PiSharp.Cli.Llama.LlamaCatalog", "PiSharp.Cli.Commands.LiveSessionRuntime", "PiSharp.Cli.Durable.DurableModels"
        })
            Check(!names.Contains(type), type + " moved to PiSharp.ModelRuntime");
        Check(!ModelRuntime.GetTypes().Any(type => type.Namespace is { } ns && (ns.StartsWith("PiSharp.Cli.Llama", StringComparison.Ordinal) ||
            ns.StartsWith("PiSharp.Cli.Interactive", StringComparison.Ordinal) || ns.StartsWith("PiSharp.Cli.Durable", StringComparison.Ordinal))),
            "a llama, interactive or durable namespace moved to PiSharp.ModelRuntime");
        Check(typeof(IProviderCredentialSource).IsInterface && typeof(PiSharp.AI.Authentication.OAuth.IAdmittedOAuthCredentialSource).IsAssignableFrom(typeof(IProviderCredentialSource)),
            "IProviderCredentialSource extends IAdmittedOAuthCredentialSource");
    }

    private static void BoundaryNamespaces()
    {
        foreach (var (type, ns) in new[]
        {
            (typeof(ModelRegistry), "PiSharp.Cli.Models"), (typeof(BuiltinModelCatalog), "PiSharp.Cli.Models"), (typeof(ModelsJsonConfig), "PiSharp.Cli.Models"),
            (typeof(ConfigValueResolver), "PiSharp.Cli.Models"), (typeof(RemoteCatalogOverlay), "PiSharp.Cli.Models"), (typeof(IRegistryProviderLayer), "PiSharp.Cli.Models"),
            (typeof(ProviderAuthCatalog), "PiSharp.Cli.Authentication"), (typeof(ProviderLiveAuthentication), "PiSharp.Cli.Authentication"),
            (typeof(IProviderCredentialSource), "PiSharp.Cli.Authentication"), (typeof(LiveProviderRoute), "PiSharp.Cli.Commands"),
            (typeof(LiveModelCatalog), "PiSharp.Cli.Commands"), (typeof(LiveSessionSelection), "PiSharp.Cli.Commands"), (typeof(LiveRouteRuntime), "PiSharp.Cli.Commands")
        })
        {
            Equal(ns, type.Namespace, type.Name + " namespace");
            Equal(ModelRuntime, type.Assembly, type.Name + " assembly");
        }
        // The pinned parse keeps the CLI's session command failure (moved with the selection, namespace unchanged).
        var failure = Throws<SessionCommandException>(() => LiveSessionSelection.Parse("not-a-provider", "x", null), "unknown provider");
        Equal(SessionCommandFailure.InvalidArguments, failure.Failure, "failure class");
    }
}
