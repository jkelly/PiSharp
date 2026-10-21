// PiSharp-only (decision 0010, M6): the operator commands of src/PiSharp.Cli/Durable/DurableOps.cs through `pisharp durable`. `serve` runs
// in process as a service manager would start it and stops on SIGTERM (delivered through ShutdownSignals, as the process registration
// does); doctor, backup and restore run against it while it serves and after it stops. Service definitions are generated, parsed and
// checked for the serve command and the absence of the token; nothing is installed.
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using PiSharp.Cli;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Pi;
using PiSharp.Durable.Client;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> OpsCases() =>
    [
        Case("ops.serve-stops-on-sigterm-and-doctor-backup-and-restore-work-around-it", async () =>
        {
            var root = Temp("ops-serve");
            var data = Path.Combine(root, "data");
            var work = Path.Combine(root, "work");
            var port = ClosedPort();
            var output = new Capture();
            var errors = new Capture();
            using var signals = new ShutdownSignals(register: false);
            var host = new PiHost
            {
                Cwd = root, Home = root, GetEnvironment = _ => null, Stdout = output, Stderr = errors,
                LiveRuntime = new LiveSessionRuntime(_ => null, () => null), Signals = () => signals,
            };
            var serving = DurableMain.RunAsync(["serve", "--provider", "faux", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--data-dir", data, "--start-dirs", work, "--save"], host, CancellationToken.None);
            await UntilAsync(() => Task.FromResult(output.Text.Contains("PiSharp durable device", StringComparison.Ordinal) || serving.IsCompleted), "the device started");
            Check(!serving.IsCompleted, "serving: " + errors.Text);
            var token = File.ReadAllText(Path.Combine(data, "token")).Trim();
            using (var client = new DurableDeviceClient(new Uri($"http://127.0.0.1:{port}/"), token))
            {
                var (session, agent) = await MainAgentAsync(client, work);
                await client.SendMessageAsync(session, agent, "keep this", "ops-1");
                await UntilAsync(async () => (await client.TranscriptAsync(session, agent, markdown: true))!["text"]!.GetValue<string>().Contains("Echo: keep this", StringComparison.Ordinal), "the reply");
            }

            var probe = await CliAsync(root, NoEnvironment, "doctor", "--probe", "--json", "--data-dir", data);
            Equal(DurableExit.Ok, probe.Exit, "no check fails on a running device: " + probe.Out);
            var probed = JsonNode.Parse(probe.Out)!["checks"]!.AsArray().OfType<JsonObject>().ToDictionary(check => check["id"]!.GetValue<string>(), check => check);
            Equal("ok", probed["probe"]!["status"]!.GetValue<string>(), "the probe reaches the listener: " + probed["probe"]);
            Equal("info", probed["device"]!["status"]!.GetValue<string>(), "running");
            Equal("ok", probed["token"]!["status"]!.GetValue<string>(), "a strong token");
            Equal("ok", probed["config"]!["status"]!.GetValue<string>(), "loopback");
            Equal(DurableExit.Conflict, (await CliAsync(root, NoEnvironment, "doctor", "--repair", "--data-dir", data)).Exit, "no repair while running");
            Equal(DurableExit.Conflict, (await CliAsync(root, NoEnvironment, "backup", "create", "--out", Path.Combine(root, "early"), "--data-dir", data)).Exit,
                "no backup while running");

            signals.Signal(PosixSignal.SIGTERM);
            Equal(0, await serving.WaitAsync(TimeSpan.FromSeconds(30)), "SIGTERM stops the device cleanly: " + errors.Text);
            var stopped = await CliAsync(root, NoEnvironment, "doctor", "--data-dir", data);
            Equal(DurableExit.Ok, stopped.Exit, "a stopped device is healthy: " + stopped.Out);
            Check(stopped.Out.Contains("device: Not running.", StringComparison.Ordinal), "the lock was released: " + stopped.Out);

            var backup = Path.Combine(root, "backup");
            var created = await CliAsync(root, NoEnvironment, "backup", "create", "--out", backup, "--data-dir", data);
            Equal(DurableExit.Ok, created.Exit, "backup: " + created.Err);
            Check(created.Out.Contains("The device token is not included.", StringComparison.Ordinal), created.Out);
            var verified = await CliAsync(root, NoEnvironment, "backup", "verify", backup, "--json");
            Equal(true, JsonNode.Parse(verified.Out)!["ok"]!.GetValue<bool>(), "verified: " + verified.Out);
            var restored = Path.Combine(root, "restored");
            Equal(DurableExit.Ok, (await CliAsync(root, NoEnvironment, "backup", "restore", backup, "--data-dir", restored)).Exit, "restored");
            Equal(DurableExit.Failed, (await CliAsync(root, NoEnvironment, "backup", "restore", backup, "--data-dir", restored)).Exit, "never over a non-empty directory");
            Equal(DurableExit.Usage, (await CliAsync(root, NoEnvironment, "backup", "restore", backup)).Exit, "restore names its target");
            var doctorRestored = await CliAsync(root, NoEnvironment, "doctor", "--json", "--data-dir", restored);
            Equal(true, JsonNode.Parse(doctorRestored.Out)!["ok"]!.GetValue<bool>(), "the restored store is healthy: " + doctorRestored.Out);
            File.AppendAllText(Path.Combine(backup, "device.json"), " ");
            var damaged = await CliAsync(root, NoEnvironment, "backup", "verify", backup);
            Equal(DurableExit.Invalid, damaged.Exit, "a changed file is found");
            Check(damaged.Out.Contains("device.json", StringComparison.Ordinal), damaged.Out);

            foreach (var text in new[] { output.Text, errors.Text, probe.Out, stopped.Out, created.Out, verified.Out, doctorRestored.Out, damaged.Out })
                Check(!text.Contains(token, StringComparison.Ordinal), "no output shows the token");
        }),
        Case("ops.doctor-fails-a-weak-token-warns-a-wide-listener-and-never-prints-the-token", async () =>
        {
            var root = Temp("ops-doctor");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "token"), "weak-XYZZY");
            File.WriteAllText(Path.Combine(data, "device.json"), """{"host":"0.0.0.0","port":7311}""");
            var result = await CliAsync(root, NoEnvironment, "doctor", "--json", "--data-dir", data);
            Equal(DurableExit.Failed, result.Exit, "a failing check exits 1");
            var checks = JsonNode.Parse(result.Out)!["checks"]!.AsArray().OfType<JsonObject>().ToDictionary(check => check["id"]!.GetValue<string>(), check => check);
            Equal("fail", checks["token"]!["status"]!.GetValue<string>(), "a weak token");
            Equal("warn", checks["config"]!["status"]!.GetValue<string>(), "a listener beyond loopback");
            Check(checks["config"]!["fix"]!.GetValue<string>().Contains("SSH tunnel", StringComparison.Ordinal), "points at the safe alternatives");
            Equal("warn", checks["catalog"]!["status"]!.GetValue<string>(), "no catalog yet");
            Check(!result.Out.Contains("XYZZY", StringComparison.Ordinal) && !result.Err.Contains("XYZZY", StringComparison.Ordinal), "the token is never printed");
            Equal(DurableExit.Usage, (await CliAsync(root, NoEnvironment, "doctor", "--device", "http://127.0.0.1:1/")).Exit, "doctor is local");
            Equal(DurableExit.Usage, (await CliAsync(root, NoEnvironment, "backup", "frobnicate")).Exit, "an unknown backup command");
            var help = await CliAsync(root, NoEnvironment, "help");
            foreach (var name in new[] { "pisharp durable attach", "pisharp durable doctor", "pisharp durable service", "pisharp durable backup create", "pisharp durable retention purge" })
                Check(help.Out.Contains(name, StringComparison.Ordinal), "help lists " + name);
        }),
        Case("ops.service-definitions-run-serve-hold-no-token-and-install-nothing", async () =>
        {
            var root = Temp("ops-service");
            var data = Path.Combine(root, "data dir");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "token"), "service-token-QWERTY-0123456789-abcdefghij");
            var exec = Path.Combine(root, "bin", "pisharp");
            var printed = await CliAsync(root, NoEnvironment, "service", "systemd", "--data-dir", data, "--exec", exec);
            Equal(DurableExit.Ok, printed.Exit, "printed: " + printed.Err);
            Check(printed.Out.Contains("Nothing was installed.", StringComparison.Ordinal) && printed.Out.Contains("systemctl --user enable --now pisharp-durable", StringComparison.Ordinal),
                "the install commands are printed, not run: " + printed.Out);
            var execStart = printed.Out.Split('\n').Single(line => line.StartsWith("ExecStart=", StringComparison.Ordinal)).Trim();
            Check(execStart.Contains("durable serve --data-dir", StringComparison.Ordinal) && execStart.Contains("data dir", StringComparison.Ordinal), "ExecStart runs serve on the data directory: " + execStart);
            Check(execStart.Contains('"', StringComparison.Ordinal), "a path with a space is quoted: " + execStart);

            var output = Path.Combine(root, "out");
            foreach (var (kind, file) in new[] { ("systemd", "pisharp-durable.service"), ("launchd", "dev.pisharp.durable.plist"), ("windows", "pisharp-durable-task.ps1") })
            {
                var written = await CliAsync(root, NoEnvironment, "service", kind, "--out", output, "--data-dir", data, "--exec", exec);
                Equal(DurableExit.Ok, written.Exit, kind + ": " + written.Err);
                var content = File.ReadAllText(Path.Combine(output, file));
                Check(content.Contains("serve", StringComparison.Ordinal) && content.Contains("data dir", StringComparison.Ordinal), kind + " runs serve on the data directory");
                Check(!content.Contains("QWERTY", StringComparison.Ordinal), kind + " holds no token");
                Equal(DurableExit.Failed, (await CliAsync(root, NoEnvironment, "service", kind, "--out", output, "--data-dir", data)).Exit, kind + " never overwrites");
            }
            using (var reader = XmlReader.Create(Path.Combine(output, "dev.pisharp.durable.plist"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
            {
                var plist = XDocument.Load(reader);
                var arguments = plist.Descendants("array").Single().Elements("string").Select(element => element.Value).ToList();
                Equal(exec, arguments[0], "launchd runs the executable");
                Equal("durable,serve,--data-dir," + data, string.Join(",", arguments.Skip(1)), "with serve's arguments, one per string");
            }
            var script = File.ReadAllText(Path.Combine(output, "pisharp-durable-task.ps1"));
            Check(script.Contains("Register-ScheduledTask", StringComparison.Ordinal) && script.Contains("-AtLogOn", StringComparison.Ordinal), "a per-user logon task");
            Check(!Directory.EnumerateFiles(output).Any(path => !path.EndsWith(".service", StringComparison.Ordinal) && !path.EndsWith(".plist", StringComparison.Ordinal) &&
                !path.EndsWith(".ps1", StringComparison.Ordinal)), "only the definitions were written");
            Equal(DurableExit.Usage, (await CliAsync(root, NoEnvironment, "service", "bsd")).Exit, "an unknown service manager");
        }),
    ];
}
