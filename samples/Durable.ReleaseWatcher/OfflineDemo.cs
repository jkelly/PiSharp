// The reference scenario (decision 0010, M5), offline: a device and a recorded release feed on loopback, a manual clock, and a faux
// classifier, so it needs no network, provider or key. The device is a normal DurableDeviceHost behind DurableDeviceServer; with
// RunSchedules off, the schedule runs only when the demo ticks it with the watcher's trigger credential, as a cron job or CI step would.
// Everything after the device starts goes through ReleaseWatcher and DurableDeviceClient, as against any other device.
//
//   1. Set up the watcher and issue a trigger credential restricted to its schedule.
//   2. Tick: the feed has 1.0.0 (ignore) and 1.1.0 (BREAKING). Both are judged automatically; 1.1.0 is researched.
//   3. The feed changes 1.0.0's notes and adds 1.2.0 (a security fix). Tick: the changed and the new release are judged; 1.2.0's medium
//      confidence routes it to review, and a person approves it.
//   4. Inspect evidence, costs and completion audits; give feedback (more, less, undo, less) and read the profile.
//   5. Restart the device and set up again, which finds everything the first setup made; tick the unchanged feed: no new artifact, and
//      the next pass replays every judgment instead of starting one.
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;
using Chord = PiSharp.Durable.Chord;

namespace PiSharp.Durable.ReleaseWatcher;

/// <summary>What the reference scenario showed.</summary>
public sealed record DemoReport
{
    public required int FirstTickJudged { get; init; }
    public required int FirstTickResearched { get; init; }
    public required int SecondTickJudged { get; init; }
    public required int SecondTickReplayed { get; init; }
    public required string ReviewedRelease { get; init; }
    public required string ReviewRoute { get; init; }
    public required string ReviewDecision { get; init; }
    public required IReadOnlyList<string> Audits { get; init; }
    public required int ReviewedReservations { get; init; }
    public required bool ReviewedCostKnown { get; init; }
    public required long ReviewedTokens { get; init; }
    public required long RequestsToday { get; init; }
    public required IReadOnlyDictionary<string, double> Weights { get; init; }
    public required long ProfileRevision { get; init; }
    public required bool SetupRepeatable { get; init; }
    public required string UnchangedTick { get; init; }
    public required int UnchangedTickArtifacts { get; init; }
    public required int ArtifactsBeforeRestart { get; init; }
    public required int ArtifactsAfterRestart { get; init; }
    public required int JudgmentsBeforeRestart { get; init; }
    public required int JudgmentsAfterRestart { get; init; }
    public required int ReplayedAfterRestart { get; init; }
}

/// <summary>What processing the watcher again on a restored data directory found.</summary>
/// <param name="Judged">Judgments started anew (0 for a faithful restore).</param>
/// <param name="Replayed">Judgments that replayed.</param>
/// <param name="Judgments">Judgment runs in the session.</param>
/// <param name="Artifacts">Artifacts in the session.</param>
/// <param name="Evidence">The judgments' evidence texts, by evidence ID.</param>
public sealed record ReplayReport(int Judged, int Replayed, int Judgments, int Artifacts, IReadOnlyDictionary<string, string> Evidence);

/// <summary>The release watcher's reference scenario, offline.</summary>
public static class OfflineDemo
{
    /// <summary>How long the demo waits for a run.</summary>
    public static TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The sample configuration pointed at the fixture feed and the faux classifier.</summary>
    public static WatcherConfig Config(string cwd, Uri feed)
    {
        var sample = WatcherConfig.Parse(Resource("watcher.sample.json"));
        var spec = (JsonObject)sample.PolicySpec.DeepClone();
        spec["model"] = new JsonObject { ["provider"] = FauxDurableModels.ProviderId, ["id"] = FauxDurableModels.JudgeId };
        return sample with
        {
            Cwd = cwd,
            Sources = [new JsonObject { ["id"] = "widget-feed", ["kind"] = "feed", ["url"] = feed.ToString(), ["title"] = "Widget releases" }],
            PolicySpec = spec,
        };
    }

    /// <summary>
    /// Faux models whose classifier reads the release: "breaking" with confidence 0.95, a security fix "relevant" with 0.70 (a person
    /// confirms it), anything else "ignore" with 0.92. Each classification reports recorded usage, so its cost is known.
    /// </summary>
    public static FauxDurableModels Models() => new()
    {
        Classify = (context, _) =>
        {
            var input = (Chord.JsonObjectValue)((Chord.JsonObjectValue)context["state"])["input"];
            string Field(string key) => input.TryGetValue(key, out var value) && value is Chord.JsonStringValue text ? text.Value : "";
            var release = (Field("title") + " " + Field("summary")).ToLowerInvariant();
            var (label, confidence) = release.Contains("breaking", StringComparison.Ordinal) ? ("breaking", 0.95)
                : release.Contains("security", StringComparison.Ordinal) || release.Contains("cve-", StringComparison.Ordinal) ? ("relevant", 0.70)
                : ("ignore", 0.92);
            var probabilities = new[] { "breaking", "relevant", "ignore" }
                .Select(name => new KeyValuePair<string, Chord.JsonValue>(name, name == label ? confidence : Math.Round((1 - confidence) / 2, 4)));
            return Task.FromResult(Chord.JsonValue.Object(
            [
                new("label", Chord.JsonValue.Object([new("type", "choice"), new("choice", label), new("probabilities", Chord.JsonValue.Object(probabilities)),
                    new("confidence", confidence)])),
            ]));
        },
        ClassifierUsage = Chord.JsonValue.Object([new("input", 180), new("output", 6), new("totalTokens", 186),
            new("cost", Chord.JsonValue.Object([new("input", 0.00018), new("output", 0.00003), new("cacheRead", 0), new("cacheWrite", 0), new("total", 0.00021)]))]),
    };

    /// <summary>Runs the scenario in <paramref name="root"/> (its <c>data</c> and <c>work</c> directories) and reports what it showed.</summary>
    /// <exception cref="InvalidOperationException">A step did not do what the scenario expects.</exception>
    public static async Task<DemoReport> RunAsync(string root, TextWriter log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var data = Path.Join(root, "data");
        var work = Path.Join(root, "work");
        Directory.CreateDirectory(work);
        var clock = new DemoClock(DateTimeOffset.UtcNow);
        var models = Models();
        var token = DeviceTokens.Generate();
        await using var feed = await FixtureFeed.StartAsync(cancellationToken).ConfigureAwait(false);
        var config = Config(work, feed.Url);
        var device = await DemoDevice.StartAsync(data, work, clock, models, token, cancellationToken).ConfigureAwait(false);
        try
        {
            log.WriteLine("1. Set up the watcher");
            var watcher = new ReleaseWatcher(device.Client, config, log);
            var setup = await watcher.SetupAsync(issueToken: true, cancellationToken).ConfigureAwait(false);
            var trigger = setup.TriggerToken ?? throw new InvalidOperationException("Setup issued no trigger credential.");
            var sessionId = setup.SessionId;

            log.WriteLine("2. Tick with the trigger credential: two releases");
            feed.Use(1);
            await TickAsync(device, trigger, setup.ScheduleId, clock, config, cancellationToken).ConfigureAwait(false);
            var first = await watcher.ProcessAsync(cancellationToken).ConfigureAwait(false);
            await SettleAsync(device.Client, watcher, sessionId, cancellationToken).ConfigureAwait(false);
            var researched = await watcher.ProcessAsync(cancellationToken).ConfigureAwait(false);
            await SettleAsync(device.Client, watcher, sessionId, cancellationToken).ConfigureAwait(false);

            log.WriteLine("3. The feed changes: 1.0.0's notes changed and 1.2.0 is new");
            feed.Use(2);
            await TickAsync(device, trigger, setup.ScheduleId, clock, config, cancellationToken).ConfigureAwait(false);
            var second = await watcher.ProcessAsync(cancellationToken).ConfigureAwait(false);
            await SettleAsync(device.Client, watcher, sessionId, cancellationToken).ConfigureAwait(false);
            var pending = await watcher.PendingAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Count != 1) throw new InvalidOperationException($"Expected one release waiting for review, found {pending.Count}.");
            var review = pending[0];
            var reviewedTitle = review["input"]!["title"]!.GetValue<string>();
            log.WriteLine($"   {reviewedTitle} waits for review: {review["result"]!["label"]} with confidence {review["result"]!["confidence"]}");
            var decided = await watcher.DecideAsync(review["id"]!.GetValue<string>(), approve: true, "A security fix: schedule the upgrade.", cancellationToken)
                .ConfigureAwait(false);
            log.WriteLine($"   Approved: {decided["decision"]}");
            var reviewTask = review["runTaskId"]!.GetValue<long>();
            await WaitAsync(device.Client, sessionId, reviewTask, ["completed"], cancellationToken).ConfigureAwait(false);
            await watcher.ProcessAsync(cancellationToken).ConfigureAwait(false);
            await SettleAsync(device.Client, watcher, sessionId, cancellationToken).ConfigureAwait(false);

            log.WriteLine("4. Evidence, costs, audits and feedback");
            var usage = (await device.Client.ExecutionUsageAsync(sessionId, reviewTask, cancellationToken).ConfigureAwait(false))!;
            var budgets = (await device.Client.BudgetsAsync(cancellationToken).ConfigureAwait(false))!;
            var requestsToday = budgets["used"]?["requests"]?.GetValue<long>() ?? 0;
            var reviewedTokens = usage["totals"]?["tokens"]?.GetValue<long>() ?? 0;
            log.WriteLine($"   {reviewedTitle}: {usage["reservations"]!.AsArray().Count} reservation(s), {reviewedTokens} tokens, cost " +
                $"{(usage["costKnown"]!.GetValue<bool>() ? "known" : "not known")}; {requestsToday} model request(s) today");
            var audits = new List<string>();
            foreach (var judgment in (await watcher.JudgmentsAsync(cancellationToken).ConfigureAwait(false)).Reverse())
            {
                var execution = (await device.Client.ExecutionAsync(sessionId, judgment["runTaskId"]!.GetValue<long>(), cancellationToken).ConfigureAwait(false))!;
                audits.Add($"{judgment["input"]!["title"]} judged {judgment["result"]!["label"]}, {judgment["decision"]}: audit {execution["run"]?["audit"]?["status"]}");
            }
            foreach (var run in await watcher.JobRunsAsync(ReleaseWatcher.ResearchJob, cancellationToken).ConfigureAwait(false))
            {
                var execution = (await device.Client.ExecutionAsync(sessionId, run["id"]!.GetValue<long>(), cancellationToken).ConfigureAwait(false))!;
                audits.Add($"Research execution {run["id"]}: audit {execution["run"]?["audit"]?["status"]}");
            }
            foreach (var audit in audits) log.WriteLine("   " + audit);

            var findings = (await watcher.ArtifactsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                .Where(artifact => artifact["kind"]!.GetValue<string>() == "finding" && artifact["provenance"]?["judgmentId"] is not null).ToList();
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var finding in findings)
            {
                var judgment = (await device.Client.JudgmentAsync(sessionId, finding["provenance"]!["judgmentId"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false))!;
                labels[finding["id"]!.GetValue<string>()] = judgment["result"]!["label"]!.GetValue<string>();
            }
            var breaking = labels.First(pair => pair.Value == "breaking").Key;
            var ignored = labels.First(pair => pair.Value == "ignore").Key;
            await watcher.FeedbackAsync(breaking, "more", requestId: "demo-more", cancellationToken: cancellationToken).ConfigureAwait(false);
            await watcher.FeedbackAsync(ignored, "less", requestId: "demo-less", cancellationToken: cancellationToken).ConfigureAwait(false);
            await watcher.FeedbackAsync(ignored, "undo", requestId: "demo-undo", cancellationToken: cancellationToken).ConfigureAwait(false);
            var feedback = await watcher.FeedbackAsync(ignored, "less", requestId: "demo-less-again", cancellationToken: cancellationToken).ConfigureAwait(false);
            // A retry of a request already recorded replays it.
            await watcher.FeedbackAsync(ignored, "less", requestId: "demo-less-again", cancellationToken: cancellationToken).ConfigureAwait(false);
            var profile = feedback["profile"]!.AsObject();
            var weights = profile["weights"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<double>(), StringComparer.Ordinal);
            log.WriteLine($"   Profile {profile["id"]} at revision {profile["revision"]}: " + string.Join(", ", weights.Select(pair => $"{pair.Key} {pair.Value:+0.00;-0.00}")));

            var artifactsBefore = (await watcher.ArtifactsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Count;
            var judgmentsBefore = (await watcher.JobRunsAsync(ReleaseWatcher.JudgeJob, cancellationToken).ConfigureAwait(false)).Count;

            log.WriteLine("5. Restart the device and tick the unchanged feed");
            await device.DisposeAsync().ConfigureAwait(false);
            device = await DemoDevice.StartAsync(data, work, clock, models, token, cancellationToken).ConfigureAwait(false);
            watcher = new ReleaseWatcher(device.Client, config, log);
            // Setup again finds what the first setup made: the same session, policy version and schedule.
            var repeated = await watcher.SetupAsync(issueToken: false, cancellationToken).ConfigureAwait(false);
            var (status, unchangedTask) = await TickAsync(device, trigger, setup.ScheduleId, clock, config, cancellationToken).ConfigureAwait(false);
            var unchangedArtifacts = (await device.Client.ExecutionArtifactsAsync(sessionId, unchangedTask, cancellationToken).ConfigureAwait(false))!["items"]!.AsArray().Count;
            var again = await watcher.ProcessAsync(cancellationToken).ConfigureAwait(false);
            await SettleAsync(device.Client, watcher, sessionId, cancellationToken).ConfigureAwait(false);
            var artifactsAfter = (await watcher.ArtifactsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Count;
            var judgmentsAfter = (await watcher.JobRunsAsync(ReleaseWatcher.JudgeJob, cancellationToken).ConfigureAwait(false)).Count;
            log.WriteLine($"   Artifacts: {artifactsBefore} before the restart, {artifactsAfter} after; judgments: {judgmentsBefore}, then {judgmentsAfter}");

            return new DemoReport
            {
                FirstTickJudged = first.Judged,
                // The first pass may already find 1.1.0's judgment approved (the faux classifier is quick) and start its research; the
                // second pass then replays it. Either way the tick researches it once.
                FirstTickResearched = first.Researched + researched.Researched,
                SecondTickJudged = second.Judged,
                SecondTickReplayed = second.Replayed,
                ReviewedRelease = reviewedTitle,
                ReviewRoute = review["route"]!.GetValue<string>(),
                ReviewDecision = decided["decision"]!.GetValue<string>(),
                Audits = audits,
                ReviewedReservations = usage["reservations"]!.AsArray().Count,
                ReviewedCostKnown = usage["costKnown"]!.GetValue<bool>(),
                ReviewedTokens = reviewedTokens,
                RequestsToday = requestsToday,
                Weights = weights,
                ProfileRevision = profile["revision"]!.GetValue<long>(),
                SetupRepeatable = repeated == setup with { TriggerToken = null },
                UnchangedTick = status,
                UnchangedTickArtifacts = unchangedArtifacts,
                ArtifactsBeforeRestart = artifactsBefore,
                ArtifactsAfterRestart = artifactsAfter,
                JudgmentsBeforeRestart = judgmentsBefore,
                JudgmentsAfterRestart = judgmentsAfter,
                ReplayedAfterRestart = again.Replayed,
            };
        }
        finally
        {
            await device.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens <paramref name="data"/>, a restored copy of the demo's data directory (decision 0010, M6), and processes the watcher once more
    /// as the demo's restart does. Each judgment starts under a key made from its evidence ID with the evidence's restored text in its
    /// input, so a faithful restore replays every judgment and judges nothing again; a missing or changed evidence body would be a different
    /// request under the same key and fail. <paramref name="work"/> is the demo's original working directory, part of the session's key.
    /// </summary>
    public static async Task<ReplayReport> ReplayAsync(string data, string work, TextWriter log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var config = Config(work, new Uri("http://127.0.0.1:9/widget/releases.xml"));
        await using var device = await DemoDevice.StartAsync(data, work, new DemoClock(DateTimeOffset.UtcNow.AddDays(1)), Models(), DeviceTokens.Generate(),
            cancellationToken).ConfigureAwait(false);
        var watcher = new ReleaseWatcher(device.Client, config, log);
        var pass = await watcher.ProcessAsync(cancellationToken).ConfigureAwait(false);
        var sessionId = await watcher.SessionAsync(cancellationToken).ConfigureAwait(false);
        await SettleAsync(device.Client, watcher, sessionId, cancellationToken).ConfigureAwait(false);
        var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var judgment in await watcher.JudgmentsAsync(cancellationToken).ConfigureAwait(false))
            foreach (var id in (judgment["evidenceIds"] as JsonArray ?? []).Select(id => id!.GetValue<string>()))
                evidence[id] = await device.Client.EvidenceTextAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
        return new ReplayReport(pass.Judged, pass.Replayed, (await watcher.JobRunsAsync(ReleaseWatcher.JudgeJob, cancellationToken).ConfigureAwait(false)).Count,
            (await watcher.ArtifactsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Count, evidence);
    }

    /// <summary>Advances the clock one interval and ticks the schedule with the trigger credential, as a cron job would; waits for the check.</summary>
    private static async Task<(string Status, long TaskId)> TickAsync(DemoDevice device, string trigger, string scheduleId, DemoClock clock, WatcherConfig config,
        CancellationToken cancellationToken)
    {
        clock.Advance(TimeSpan.FromMinutes(config.EveryMinutes));
        using var client = new DurableDeviceClient(device.Base, trigger);
        var tick = (await client.TickScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false))!;
        var status = tick["status"]!.GetValue<string>();
        if (status != "started") throw new InvalidOperationException($"The tick was {status}: {tick["reason"]}");
        var taskId = tick["taskId"]!.GetValue<long>();
        var sessionId = (await device.Client.SchedulesAsync(cancellationToken).ConfigureAwait(false))!.AsArray()
            .First(item => item!["id"]!.GetValue<string>() == scheduleId)!["sessionId"]!.GetValue<string>();
        await WaitAsync(device.Client, sessionId, taskId, ["completed"], cancellationToken).ConfigureAwait(false);
        return (status, taskId);
    }

    /// <summary>Waits until every judgment and research run has finished or waits for a person.</summary>
    private static async Task SettleAsync(DurableDeviceClient client, ReleaseWatcher watcher, string sessionId, CancellationToken cancellationToken)
    {
        foreach (var job in new[] { ReleaseWatcher.JudgeJob, ReleaseWatcher.ResearchJob })
            foreach (var run in await watcher.JobRunsAsync(job, cancellationToken).ConfigureAwait(false))
                await WaitAsync(client, sessionId, run["id"]!.GetValue<long>(), ["completed", "waiting"], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonObject> WaitAsync(DurableDeviceClient client, string sessionId, long taskId, string[] statuses, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var execution = (await client.ExecutionAsync(sessionId, taskId, cancellationToken).ConfigureAwait(false))!.AsObject();
            var status = execution["status"]!.GetValue<string>();
            if (statuses.Contains(status)) return execution;
            if (status is "failed" or "aborted" or "declined")
                throw new InvalidOperationException($"Execution {taskId} {status}: {execution["run"]?["error"]}");
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Execution {taskId} is still {status}.");
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"No resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A clock the demo moves forward an interval at a time.</summary>
    private sealed class DemoClock(DateTimeOffset start) : TimeProvider
    {
        private long milliseconds = start.ToUnixTimeMilliseconds();

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref milliseconds));

        public void Advance(TimeSpan by) => Interlocked.Add(ref milliseconds, (long)by.TotalMilliseconds);
    }

    /// <summary>A device and its server on a free loopback port.</summary>
    private sealed class DemoDevice : IAsyncDisposable
    {
        private DurableDeviceHost host = null!;
        private WebApplication app = null!;

        public Uri Base { get; private set; } = null!;

        public DurableDeviceClient Client { get; private set; } = null!;

        public static async Task<DemoDevice> StartAsync(string data, string work, TimeProvider clock, FauxDurableModels models, string token,
            CancellationToken cancellationToken)
        {
            var device = new DemoDevice();
            device.host = await DurableDeviceHost.OpenAsync(new DurableDeviceOptions
            {
                DataDirectory = data,
                StartDirectories = [work],
                DeviceName = "release-watcher-demo",
                Models = models,
                DefaultModel = FauxDurableModels.EchoRef,
                // The fixture feed is on loopback; a real device allows a private host only when it is configured to.
                Retrieval = new RetrievalOptions { AllowedPrivateHosts = ["127.0.0.1"] },
                TimeProvider = clock,
                RunSchedules = false,
            }, cancellationToken).ConfigureAwait(false);
            device.app = DurableDeviceServer.Build(device.host, new DurableDeviceServerOptions { Token = token }, "http://127.0.0.1:0");
            await device.app.StartAsync(cancellationToken).ConfigureAwait(false);
            device.Base = new Uri(device.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            device.Client = new DurableDeviceClient(device.Base, token);
            return device;
        }

        public async ValueTask DisposeAsync()
        {
            if (Client is null) return;
            Client.Dispose();
            Client = null!;
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The recorded release feed, served from loopback; <see cref="Use"/> picks the recording.</summary>
    private sealed class FixtureFeed : IAsyncDisposable
    {
        private WebApplication app = null!;
        private volatile string current = "";

        public Uri Url { get; private set; } = null!;

        public static async Task<FixtureFeed> StartAsync(CancellationToken cancellationToken)
        {
            var feed = new FixtureFeed();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            feed.app = builder.Build();
            feed.app.MapGet("/widget/releases.xml", () => Results.Text(feed.current, "application/rss+xml; charset=utf-8"));
            await feed.app.StartAsync(cancellationToken).ConfigureAwait(false);
            var address = feed.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            feed.Url = new Uri(new Uri(address), "/widget/releases.xml");
            return feed;
        }

        public void Use(int recording) => current = Resource($"fixtures/releases-{recording}.xml");

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
