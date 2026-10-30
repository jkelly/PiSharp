// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Samples.ModelCatalogMaintainer;

public static class MaintenanceCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        string? activeWorkspace = null;
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var options = Parse(args);
            if (options.ContainsKey("--help"))
            {
                Console.WriteLine("--maintain [--input fixture.json] [--baseline overlay.json] [--workspace directory]\n  Live source scan: --live --approve --config config.json --workspace directory\n  Publish draft PR and price issue: additionally --publish (explicit github config required).\n  --self-test runs the offline regression suite. No live calls occur by default.");
                return 0;
            }
            var live = options.ContainsKey("--live"); var approved = options.ContainsKey("--approve"); var publish = options.ContainsKey("--publish");
            if (publish && !live) throw new InvalidOperationException("--publish requires --live --approve.");
            if (live && (!approved || !options.ContainsKey("--config") || !options.ContainsKey("--workspace")))
                throw new InvalidOperationException("Live maintenance requires --approve, an explicit --config, and an explicit --workspace.");
            if (publish && options.ContainsKey("--baseline")) throw new InvalidOperationException("Publication takes its baseline from the configured repository base branch.");
            var fixturePath = options.GetValueOrDefault("--input") ?? Path.Combine(AppContext.BaseDirectory, "fixture.json");
            JsonObject? fixture = live ? null : JsonNode.Parse(await File.ReadAllTextAsync(fixturePath, cancellation.Token))?.AsObject();
            var config = options.TryGetValue("--config", out var configPath)
                ? CatalogJson.Read<MaintainerConfig>(await File.ReadAllTextAsync(configPath!, cancellation.Token))
                : CatalogJson.Read<MaintainerConfig>(fixture!["config"]!.ToJsonString());
            config.Validate();
            var workspace = Path.GetFullPath(options.GetValueOrDefault("--workspace") ?? Path.Combine("artifacts", "model-catalog-maintenance"));
            Directory.CreateDirectory(workspace);
            // Serializes local invocations; the Actions template also uses repository-level concurrency.
            await using var runLock = new FileStream(Paths.Under(workspace, "maintenance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            activeWorkspace = workspace;
            await WriteRunStatus(workspace, "running");
            using var sourceClient = HttpSourceFetcher.CreateClient();
            ISourceFetcher source = live ? new HttpSourceFetcher(sourceClient) : Workflow.Fixture(fixture!);
            source = new RecordingFetcher(source, workspace);
            GitCheckout? checkout = null;
            string? credential = null;
            if (publish)
            {
                var github = config.GitHub ?? throw new InvalidDataException("Publication requires github.repository/baseBranch/branch configuration.");
                github.Validate();
                credential = Environment.GetEnvironmentVariable(github.TokenEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("The configured GitHub token environment variable is empty.");
                checkout = await GitCheckout.CloneAsync(github, credential, config.OverlayPath, workspace, approved, cancellation.Token);
            }
            JsonObject baseline;
            var baselinePath = checkout is null ? options.GetValueOrDefault("--baseline") : Paths.Under(checkout.Root, config.OverlayPath);
            if (baselinePath is not null && File.Exists(baselinePath))
                baseline = JsonNode.Parse(await File.ReadAllTextAsync(baselinePath, cancellation.Token))?.AsObject() ?? throw new InvalidDataException("Empty baseline.");
            else if (options.ContainsKey("--baseline")) throw new FileNotFoundException("The explicitly specified baseline does not exist.");
            else baseline = live ? CatalogJson.EmptyOverlay() : (JsonObject)fixture!["baseline"]!.DeepClone();
            var baselineNotes = new List<string>();
            if (live || options.ContainsKey("--repository-root"))
                baseline = await PinnedBaseline.SeedMissingProvidersAsync(config, baseline,
                    checkout?.Root ?? options.GetValueOrDefault("--repository-root") ?? Directory.GetCurrentDirectory(), baselineNotes, cancellation.Token);
            var result = await CatalogEngine.ScanAsync(config, baseline, source, cancellation.Token);
            result = result with { Warnings = baselineNotes.Concat(result.Warnings).ToArray() };
            await File.WriteAllTextAsync(Paths.Under(workspace, "maintenance-report.json"), CatalogJson.Canonical(result.Report()), cancellation.Token);
            await File.WriteAllTextAsync(Paths.Under(workspace, "model-catalog-review.md"), ChangeReport.Markdown(result), cancellation.Token);
            if (!result.Complete)
            {
                await WriteRunStatus(workspace, "incomplete");
                Console.Error.WriteLine($"Catalog scan incomplete; no overlay or remote records written. Report: {workspace}");
                return 2;
            }
            await File.WriteAllTextAsync(Paths.Under(workspace, "models.overlay.json"), CatalogJson.Canonical(result.Overlay), cancellation.Token);
            PublishResult publication = new(false, null, null, null);
            if (publish)
            {
                using var githubClient = HttpSourceFetcher.CreateClient();
                var remote = new GitHubRemote(githubClient, config.GitHub!, credential!);
                publication = await CatalogPublisher.PublishAsync(result, config.GitHub!, checkout!, remote, approved, cancellation.Token);
            }
            await File.WriteAllTextAsync(Paths.Under(workspace, "publication-result.json"), JsonSerializer.Serialize(publication, CatalogJson.Options), cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { complete = true, changed = result.Changed, models = result.Changes.Count, prices = result.PriceChanges.Count, publication, workspace }, CatalogJson.Options));
            return 0;
        }
        catch (OperationCanceledException)
        {
            if (activeWorkspace is not null) await WriteRunStatus(activeWorkspace, "cancelled");
            Console.Error.WriteLine("Maintenance cancelled or timed out; inspect local artifacts before rerunning."); return 130;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or ArgumentException or HttpRequestException)
        {
            if (activeWorkspace is not null) await WriteRunStatus(activeWorkspace, "failed");
            // Provider response bodies and credentials are never included in failures.
            Console.Error.WriteLine($"Maintenance failed: {ex.Message}"); return 1;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static Task WriteRunStatus(string workspace, string status) => File.WriteAllTextAsync(Paths.Under(workspace, "publication-result.json"),
        new JsonObject { ["published"] = false, ["status"] = status, ["note"] = "Use only artifacts from a completed run. A failed publication may have partial remote effects; rerun to reconcile markers." }.ToJsonString(CatalogJson.Options));

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var flags = new HashSet<string>(["--maintain", "--live", "--approve", "--publish", "--help"], StringComparer.Ordinal);
        var values = new HashSet<string>(["--config", "--input", "--baseline", "--workspace", "--repository-root"], StringComparer.Ordinal);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (result.ContainsKey(name)) throw new ArgumentException($"Duplicate option {name}.");
            if (flags.Contains(name)) result[name] = null;
            else if (values.Contains(name) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) result[name] = args[++i];
            else throw new ArgumentException($"Unknown or incomplete maintenance option {name}.");
        }
        return result;
    }

    private sealed class RecordingFetcher(ISourceFetcher source, string workspace) : ISourceFetcher
    {
        public async Task<SourceDocument> FetchAsync(ProviderConfig config, CancellationToken cancellationToken)
        {
            var result = await source.FetchAsync(config, cancellationToken);
            var path = Paths.Under(workspace, "evidence/" + result.Sha256 + (config.Adapter == "official-page-json" ? ".html" : ".json"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, result.Body, cancellationToken);
            return result;
        }
    }
}
