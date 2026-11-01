// Durable.ReleaseWatcher: see ReleaseWatcher.cs and OfflineDemo.cs. Usage:
//   Durable.ReleaseWatcher demo [--dir <path>]
//   Durable.ReleaseWatcher setup    --device <url> --config <file> [--issue-token]
//   Durable.ReleaseWatcher process  --device <url> --config <file>
//   Durable.ReleaseWatcher status   --device <url> --config <file>
//   Durable.ReleaseWatcher review   --device <url> --config <file> <judgmentId> approve|reject [--reason <text>]
//   Durable.ReleaseWatcher feedback --device <url> --config <file> <artifactId> more|less|dismiss|undo [--labels <a,b>] [--request <id>]
//   Durable.ReleaseWatcher tick     --device <url> --schedule <id>
// The device credential comes from PISHARP_DEVICE_TOKEN and tick's trigger credential from PISHARP_TRIGGER_TOKEN, never from an argument.
using System.Text.Json;
using PiSharp.Durable.Client;

namespace PiSharp.Durable.ReleaseWatcher;

// An explicit Main rather than top-level statements: with the ASP.NET framework referenced, a top-level Program is generated public and
// would collide with the Program of a test project that references this sample.
internal static class Program
{
    private const string Usage = """
        Usage:
          Durable.ReleaseWatcher demo [--dir <path>]
          Durable.ReleaseWatcher setup    --device <url> --config <file> [--issue-token]
          Durable.ReleaseWatcher process  --device <url> --config <file>
          Durable.ReleaseWatcher status   --device <url> --config <file>
          Durable.ReleaseWatcher review   --device <url> --config <file> <judgmentId> approve|reject [--reason <text>]
          Durable.ReleaseWatcher feedback --device <url> --config <file> <artifactId> more|less|dismiss|undo [--labels <a,b>] [--request <id>]
          Durable.ReleaseWatcher tick     --device <url> --schedule <id>
        The device credential is read from PISHARP_DEVICE_TOKEN; tick reads the trigger credential from PISHARP_TRIGGER_TOKEN.
        """;

    private static async Task<int> Main(string[] args)
    {
        var indented = new JsonSerializerOptions { WriteIndented = true };

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        var command = args[0];
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) positional.Add(args[i]);
            else if (args[i] == "--issue-token") options[args[i]] = null;
            else if (i + 1 < args.Length) options[args[i]] = args[++i];
            else return Fail($"{args[i]} needs a value.");
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, press) =>
        {
            press.Cancel = true;
            stop.Cancel();
        };
        try
        {
            if (command == "demo")
            {
                var root = options.GetValueOrDefault("--dir") ?? Path.Join(Path.GetTempPath(), "pisharp-release-watcher-" + Guid.NewGuid().ToString("n")[..8]);
                Console.WriteLine($"Offline demo in {root}");
                var report = await OfflineDemo.RunAsync(root, Console.Out, stop.Token);
                Console.WriteLine(JsonSerializer.Serialize(report, indented));
                return 0;
            }
            if (command == "tick")
            {
                using var trigger = Client("PISHARP_TRIGGER_TOKEN");
                var tick = await trigger.TickScheduleAsync(Option("--schedule"), stop.Token);
                Console.WriteLine(tick?.ToJsonString(indented));
                return 0;
            }
            if (command is not ("setup" or "process" or "status" or "review" or "feedback")) return Fail($"Unknown command {command}.");

            using var device = Client("PISHARP_DEVICE_TOKEN");
            var watcher = new ReleaseWatcher(device, WatcherConfig.Load(Option("--config")), Console.Out);
            switch (command)
            {
                case "setup":
                    var setup = await watcher.SetupAsync(options.ContainsKey("--issue-token"), stop.Token);
                    Console.WriteLine($"Session {setup.SessionId}, schedule {setup.ScheduleId}, policy version {setup.PolicyVersion}");
                    if (setup.TriggerToken is { } token)
                        Console.WriteLine($"Trigger token (shown once; keep it as PISHARP_TRIGGER_TOKEN for tick): {token}");
                    return 0;
                case "process":
                    await watcher.ProcessAsync(stop.Token);
                    return 0;
                case "status":
                    var checks = await watcher.JobRunsAsync(ReleaseWatcher.SourceCheckJob, stop.Token);
                    Console.WriteLine(checks.Count == 0 ? "No source check has run yet."
                        : $"Last source check: execution {checks[0]["id"]}, {checks[0]["status"]}, {checks[0]["updatedAt"] ?? checks[0]["createdAt"]}");
                    var judged = await watcher.JobRunsAsync(ReleaseWatcher.JudgeJob, stop.Token);
                    var researched = await watcher.JobRunsAsync(ReleaseWatcher.ResearchJob, stop.Token);
                    var artifacts = await watcher.ArtifactsAsync(cancellationToken: stop.Token);
                    Console.WriteLine($"{judged.Count} judgment run(s), {researched.Count} research run(s), {artifacts.Count} artifact(s)");
                    foreach (var pending in await watcher.PendingAsync(stop.Token))
                        Console.WriteLine($"Waiting for review: {pending["id"]} {pending["input"]?["title"]}: {pending["result"]?["label"]} " +
                            $"with confidence {pending["result"]?["confidence"]}");
                    return 0;
                case "review" when positional is [var judgmentId, ("approve" or "reject") and var verdict]:
                    var decided = await watcher.DecideAsync(judgmentId, verdict == "approve", options.GetValueOrDefault("--reason"), stop.Token);
                    Console.WriteLine($"Judgment {judgmentId}: {decided["decision"]}");
                    return 0;
                case "feedback" when positional is [var artifactId, ("more" or "less" or "dismiss" or "undo") and var action]:
                    var labels = options.GetValueOrDefault("--labels")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var feedback = await watcher.FeedbackAsync(artifactId, action, labels, options.GetValueOrDefault("--request"), stop.Token);
                    Console.WriteLine(feedback.ToJsonString(indented));
                    return 0;
                default:
                    return Fail($"{command} needs other arguments.");
            }
        }
        catch (ArgumentException usage)
        {
            return Fail(usage.Message);
        }
        catch (DurableApiException error)
        {
            Console.Error.WriteLine($"The device refused: {error.Code}: {error.Message}");
            return 1;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or InvalidDataException or IOException or TimeoutException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 130;
        }

        string Option(string name) => options.GetValueOrDefault(name) ?? throw new ArgumentException($"{name} is required.");

        DurableDeviceClient Client(string tokenVariable) => new(new Uri(Option("--device")),
            Environment.GetEnvironmentVariable(tokenVariable) is { Length: > 0 } token ? token : throw new ArgumentException($"Set {tokenVariable} to the credential."));

        static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }
}
