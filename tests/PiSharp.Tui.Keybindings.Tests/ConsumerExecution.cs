using System.Reflection;
using System.Text.Json;
using PiSharp.Tui.Input;

internal static class ConsumerExecution
{
    internal static ConsumerCasePlan[] StandardPlan() =>
        new[] { "default-keys", "scalar-user-override", "ordered-array-dedup", "empty-array-disables-default",
            "unknown-id-is-not-activated", "explicit-two-action-conflict", "duplicate-single-claim-is-not-conflict",
            "override-versus-default-not-user-conflict", "unknown-user-id-does-not-claim", "replacement-resets-omitted-overrides",
            "remove-user-override-restores-defaults", "get-keys-copy-isolation", "conflict-copy-isolation",
            "resolved-singleton-scalar-and-multiple-array", "matches-iterates-configured-alternatives" }
        .Select(id => new ConsumerCasePlan("registry", id))
        .Concat(new[] { new ConsumerCasePlan("defaults", "source-defaults"), new ConsumerCasePlan("supplemental", "native-controls") })
        .Concat(new[] { "remap-left-alt-j", "remap-right-alt-k", "disable-left", "remap-word-left-alt-j",
            "remap-history-previous-ctrl-p", "remap-newline-alt-j", "default-left-control-b-guard", "default-left-arrow-guard" }
            .Select(id => new ConsumerCasePlan("editor", id)))
        .Concat(new[] { "remap-left-alt-j", "remap-right-alt-k", "disable-left", "remap-word-left-alt-j",
            "remap-history-previous-ctrl-p", "remap-newline-alt-j", "default-left-control-b-guard", "default-left-arrow-guard" }
            .Select(id => new ConsumerCasePlan("configured-editor", id)))
        .Append(new ConsumerCasePlan("configured-controls", "original-events-and-dispatch"))
        .Concat(ConfigurationLoadingCases.Ids.Select(id => new ConsumerCasePlan("configuration", id)))
        .Append(new ConsumerCasePlan("configuration-controls", "platform-path-reload-and-bounds"))
        .Concat(from category in new[] { "legacy-route", "acknowledged-route" }
                from id in TerminalStartupCases.RouteIds select new ConsumerCasePlan(category, id))
        .Append(new ConsumerCasePlan("startup", "official-composition-durable-reset"))
        .Concat(ConfigurationLoadingCases.DefaultIds.Select(id => new ConsumerCasePlan("load-default", id)))
        .Concat(TerminalStartupCases.DefaultStartupIds.Select(id => new ConsumerCasePlan("default-startup", id)))
        .Append(new ConsumerCasePlan("evidence-controls", "failure-report-controls")).ToArray();

    internal static int Run(string reportPath, ConsumerCasePlan[] plan, object requestedInputs,
        Action<ConsumerEvidence> body, TextWriter errorOutput, bool admissionRequired = true)
    {
        ConsumerEvidence evidence;
        try { evidence = ConsumerEvidence.Open(reportPath, plan, requestedInputs, admissionRequired); }
        catch (Exception error) { try { errorOutput.WriteLine(error.ToString()); } catch { } return 1; }
        try { evidence.Started(); body(evidence); evidence.BodyCompleted(); }
        catch (Exception error) { evidence.Abort(error); evidence.Error(errorOutput, error.ToString()); }
        return evidence.Finish(errorOutput);
    }

    internal static void ObserveInputs(ConsumerEvidence evidence, string[] args)
    {
        void FileIdentity(string kind, string? file)
        {
            object observation;
            try
            {
                observation = new { path = file, bytes = file is null ? (long?)null : new FileInfo(file).Length,
                    sha256 = file is null ? null : EvidenceAdmission.Hash(file), independentlyVerified = false };
            }
            catch (Exception error) { observation = new { path = file, observationError = ConsumerException.From(error), independentlyVerified = false }; }
            evidence.Observe(kind, observation);
        }
        evidence.Stage("observing-unverified-input-identities");
        FileIdentity("requested-manifest", args.ElementAtOrDefault(2));
        FileIdentity("requested-build-receipt", args.ElementAtOrDefault(4));
        object manifestIdentity;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(args.ElementAtOrDefault(2) ?? throw new ArgumentException("Missing manifest path")));
            manifestIdentity = new { candidate = manifest.RootElement.GetProperty("candidate").GetString(),
                tree = manifest.RootElement.GetProperty("tree").GetString(), independentlyVerified = false };
        }
        catch (Exception error) { manifestIdentity = new { observationError = ConsumerException.From(error), independentlyVerified = false }; }
        evidence.Observe("unverified-manifest-identity", manifestIdentity);
        FileIdentity("executing-host", Environment.ProcessPath);
        foreach (var assembly in new[] { "PiSharp.Agent", "PiSharp.AI", "PiSharp.Cli", "PiSharp.CodingAgent", "PiSharp.Contracts",
            "PiSharp.Extensions.Abstractions", "PiSharp.Extensions.Agent", "PiSharp.Extensions.Runtime", "PiSharp.Rpc", "PiSharp.Sessions",
            "PiSharp.Tools", "PiSharp.Tui" }.Select(name => Assembly.Load(name)).Append(Assembly.GetExecutingAssembly()))
        {
            FileIdentity("executing-assembly", assembly.Location);
            evidence.Observe("assembly-version", new { name = assembly.GetName().Name, path = assembly.Location,
                informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                independentlyVerified = false });
        }
    }
}
