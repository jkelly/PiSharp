using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Packages;
using PiSharp.Cli.Pi;

// End-to-end package commands through PiCommand.RunAsync with the user's own npm and git: a local npm registry fixture serving
// tarballs made with `npm pack` (no network), and a local bare git repository reached through a `url.<base>.insteadOf` rewrite of
// the https URL Pi derives from `git:example.test/owner/ext-repo`.
internal static partial class Program
{
    /// <summary>A minimal HTTP/1.1 registry on 127.0.0.1: GET routes answered from memory, one request per connection.</summary>
    private sealed class LocalRegistry : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public Dictionary<string, (byte[] Body, string Type)> Routes { get; } = new(StringComparer.Ordinal);
        public List<string> Seen { get; } = [];
        public string Url { get; }
        public LocalRegistry()
        {
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            _ = Task.Run(Loop);
        }
        private async Task Loop()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch (Exception) { return; }
                _ = Task.Run(() => Handle(client));
            }
        }
        private async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var header = new StringBuilder(); var buffer = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) == 1) header.Append((char)buffer[0]);
                    var path = Uri.UnescapeDataString(header.ToString().Split(' ')[1].Split('?')[0]);
                    lock (Seen) Seen.Add(path);
                    var found = Routes.TryGetValue(path, out var route);
                    var body = found ? route.Body : Encoding.UTF8.GetBytes("{\"error\":\"not found\"}");
                    var head = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: {(found ? route.Type : "application/json")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(body);
                    await stream.FlushAsync();
                }
                catch (IOException) { }
            }
        }
        /// <summary>Publishes tarballs (name, version, path) under their packument.</summary>
        public void Publish(string name, string latest, params (string Version, string Tarball)[] versions)
        {
            var packument = new JsonObject { ["name"] = name, ["dist-tags"] = new JsonObject { ["latest"] = latest }, ["versions"] = new JsonObject() };
            foreach (var (version, tarball) in versions)
            {
                var bytes = File.ReadAllBytes(tarball);
                var route = $"/{name}/-/{name}-{version}.tgz";
                Routes[route] = (bytes, "application/octet-stream");
                packument["versions"]![version] = new JsonObject
                {
                    ["name"] = name, ["version"] = version,
                    ["dist"] = new JsonObject
                    {
                        ["tarball"] = Url.TrimEnd('/') + route, ["integrity"] = "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes)),
                        ["shasum"] = Convert.ToHexStringLower(SHA1.HashData(bytes))
                    }
                };
            }
            Routes["/" + name] = (Encoding.UTF8.GetBytes(packument.ToJsonString()), "application/json");
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); stop.Dispose(); }
    }

    private static async Task PackageNpmEndToEnd()
    {
        RequireNpm();
        using var sandbox = new Sandbox("pkg-e2e-npm");
        using var registry = new LocalRegistry();
        var userConfig = sandbox.Write("npmrc-user", ""); var globalConfig = sandbox.Write("npmrc-global", "");
        var environment = new Dictionary<string, string?>
        {
            ["npm_config_registry"] = registry.Url, ["npm_config_cache"] = Path.Combine(sandbox.Root, "npm-cache"), ["npm_config_userconfig"] = userConfig,
            ["npm_config_globalconfig"] = globalConfig, ["npm_config_audit"] = "false", ["npm_config_fund"] = "false", ["npm_config_update_notifier"] = "false",
            ["npm_config_progress"] = "false"
        };
        var tools = new PiPackageProcesses { Environment = environment };
        var tarballs = Path.Combine(sandbox.Root, "tarballs"); Directory.CreateDirectory(tarballs);
        async Task<string> Pack(string name, string version)
        {
            var dir = Path.Combine(sandbox.Root, "fixtures", name + "-" + version);
            sandbox.Write(Path.Combine(dir, "package.json"), Jstr(new { name, version, pi = new { skills = new[] { "./skills" }, prompts = new[] { "./prompts" } } }));
            Skill(sandbox, Path.Combine(dir, "skills", name + "-skill", "SKILL.md"), name + "-skill", $"Skill {version} from {name}");
            sandbox.Write(Path.Combine(dir, "prompts", name + ".md"), "Prompt " + version);
            await tools.CaptureAsync("npm", ["pack", "--pack-destination", tarballs], dir, 120_000, null, CancellationToken.None);
            return Path.Combine(tarballs, $"{name}-{version}.tgz");
        }
        registry.Publish("fixture-pkg", "1.1.0", ("1.0.0", await Pack("fixture-pkg", "1.0.0")), ("1.1.0", await Pack("fixture-pkg", "1.1.0")));
        var tar = await Pack("fixture-tar", "2.0.0");
        Func<TextWriter, TextWriter, PiPackageProcesses> processes = (output, error) => new PiPackageProcesses { Environment = environment, Output = output, ErrorOutput = error };
        string? Version(string root, string name) => File.Exists(Path.Combine(root, "node_modules", name, "package.json"))
            ? JsonNode.Parse(File.ReadAllText(Path.Combine(root, "node_modules", name, "package.json")))!["version"]!.GetValue<string>() : null;
        string Packages(string settingsPath) => JsonNode.Parse(File.ReadAllText(settingsPath))!["packages"]!.ToJsonString();
        var userRoot = Path.Combine(sandbox.AgentDir, "npm"); var settingsPath = Path.Combine(sandbox.AgentDir, "settings.json");

        var (code, stdout, stderr) = await RunPackages(sandbox, processes, "install", "npm:fixture-pkg@1.0.0");
        Equal(0, code, "pinned install; " + stderr);
        Check(stdout.StartsWith("Installing npm:fixture-pkg@1.0.0...\n", StringComparison.Ordinal) && stdout.EndsWith("Installed npm:fixture-pkg@1.0.0\n", StringComparison.Ordinal), "install output: " + stdout);
        Equal("1.0.0", Version(userRoot, "fixture-pkg"), "pinned version installed");
        Check(registry.Seen.Contains("/fixture-pkg"), "served by the local registry");
        Equal("""["npm:fixture-pkg@1.0.0"]""", Packages(settingsPath), "settings");
        Equal("*\n!.gitignore\n", File.ReadAllText(Path.Combine(userRoot, ".gitignore")), "managed root ignored");
        (code, stdout, _) = await RunPackages(sandbox, processes, "list");
        Equal($"User packages:\n  npm:fixture-pkg@1.0.0\n    {Path.Combine(userRoot, "node_modules", "fixture-pkg")}\n", stdout, "list");

        (code, stdout, _) = await RunPackages(sandbox, processes, "update", "--extensions");
        Equal(0, code, "pinned update"); Equal("Updated packages\n", stdout, "pinned versions are not updated"); Equal("1.0.0", Version(userRoot, "fixture-pkg"), "still pinned");

        sandbox.Write(settingsPath, Jstr(new { packages = new[] { "npm:fixture-pkg@^1.0.0" } }));
        (code, stdout, stderr) = await RunPackages(sandbox, processes, "update", "--extensions");
        Equal(0, code, "range update; " + stderr);
        Check(stdout.StartsWith("Updating npm:fixture-pkg@^1.0.0...\n", StringComparison.Ordinal) && stdout.EndsWith("Updated packages\n", StringComparison.Ordinal), "range update output: " + stdout);
        Equal("1.1.0", Version(userRoot, "fixture-pkg"), "updated within the range");
        (code, stdout, _) = await RunPackages(sandbox, processes, "update", "npm:fixture-pkg");
        Equal(0, code, "targeted update"); Equal("Updated npm:fixture-pkg\n", stdout, "already current");

        var run = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
        Equal(0, run.Code, "session; " + run.Err);
        Check(sandbox.Requests[^1].Json.GetProperty("system")[0].GetProperty("text").GetString()!.Contains("Skill 1.1.0 from fixture-pkg", StringComparison.Ordinal), "installed package skill in the run");

        // Project scope: installed under .pi/npm and preferred over the user package of the same name.
        var projectRoot = Path.Combine(sandbox.Cwd, ".pi", "npm");
        (code, _, stderr) = await RunPackages(sandbox, processes, "install", "-l", "npm:fixture-pkg@1.0.0");
        Equal(0, code, "project install; " + stderr); Equal("1.0.0", Version(projectRoot, "fixture-pkg"), "project copy");
        Equal("""["npm:fixture-pkg@1.0.0"]""", Packages(Path.Combine(sandbox.Cwd, ".pi", "settings.json")), "project settings");
        run = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
        var system = sandbox.Requests[^1].Json.GetProperty("system")[0].GetProperty("text").GetString()!;
        Check(system.Contains("Skill 1.0.0 from fixture-pkg", StringComparison.Ordinal) && !system.Contains("Skill 1.1.0", StringComparison.Ordinal), "project package wins");

        // A local tarball through an npm alias spec.
        var tarSource = "npm:fixture-tar@file:" + tar.Replace('\\', '/');
        (code, _, stderr) = await RunPackages(sandbox, processes, "install", tarSource);
        Equal(0, code, "tarball install; " + stderr); Equal("2.0.0", Version(userRoot, "fixture-tar"), "tarball installed");

        (code, stdout, stderr) = await RunPackages(sandbox, processes, "remove", "npm:fixture-pkg");
        Equal(0, code, "remove; " + stderr);
        Check(stdout.StartsWith("Removing npm:fixture-pkg...\n", StringComparison.Ordinal) && stdout.EndsWith("Removed npm:fixture-pkg\n", StringComparison.Ordinal), "remove output");
        Equal(null, Version(userRoot, "fixture-pkg"), "uninstalled");
        Equal(Jstr(new[] { tarSource }), Packages(settingsPath), "settings keep the other package");
        Equal("1.0.0", Version(projectRoot, "fixture-pkg"), "project copy untouched");

        // Temporary -e sources install under <agentDir>/tmp/extensions.
        var temporary = await new PiPackageManager(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true), _ => null,
            new PiPackageProcesses { Environment = environment, Output = TextWriter.Null, ErrorOutput = TextWriter.Null }).ResolveExtensionSourcesAsync(["npm:fixture-pkg@1.1.0"], temporary: true);
        var skill = temporary.Skills.Single();
        Check(skill.Path.StartsWith(Path.Combine(sandbox.AgentDir, "tmp", "extensions", "npm") + Path.DirectorySeparatorChar, StringComparison.Ordinal) && skill.Metadata.Scope == "temporary", "temporary npm install");
    }

    private static async Task PackageGitEndToEnd()
    {
        RequireGit();
        using var sandbox = new Sandbox("pkg-e2e-git");
        var remotes = Path.Combine(sandbox.Root, "remotes"); var bare = Path.Combine(remotes, "owner", "ext-repo"); var work = Path.Combine(sandbox.Root, "work");
        Directory.CreateDirectory(bare); Directory.CreateDirectory(work);
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "url." + new Uri(remotes + Path.DirectorySeparatorChar).AbsoluteUri + ".insteadOf",
            ["GIT_CONFIG_VALUE_0"] = "https://example.test/", ["GIT_TERMINAL_PROMPT"] = "0"
        };
        var tools = new PiPackageProcesses { Environment = environment };
        Task<string> Git(string cwd, params string[] args) => tools.CaptureAsync("git", ["-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false", .. args], cwd, 60_000, null, CancellationToken.None);
        async Task<string> Commit(string file, string text)
        {
            sandbox.Write(Path.Combine(work, file), text);
            await Git(work, "add", "-A"); await Git(work, "commit", "-m", file);
            await Git(work, "push", "origin", "main", "--tags");
            return await Git(work, "rev-parse", "HEAD");
        }
        await Git(bare, "init", "--bare", "--initial-branch=main");
        await Git(work, "init", "--initial-branch=main");
        await Git(work, "remote", "add", "origin", bare);
        Skill(sandbox, Path.Combine(work, "skills", "git-skill", "SKILL.md"), "git-skill", "Skill from a git package");
        var v1 = await Commit("prompts/first.md", "First");
        await Git(work, "tag", "v1"); await Git(work, "push", "origin", "--tags");

        const string source = "git:example.test/owner/ext-repo";
        Func<TextWriter, TextWriter, PiPackageProcesses> processes = (output, error) => new PiPackageProcesses { Environment = environment, Output = output, ErrorOutput = error };
        var target = Path.Combine(sandbox.AgentDir, "git", "example.test", "owner", "ext-repo");
        var settingsPath = Path.Combine(sandbox.AgentDir, "settings.json");
        string Packages(string path) => JsonNode.Parse(File.ReadAllText(path))!["packages"]!.ToJsonString();

        var (code, stdout, stderr) = await RunPackages(sandbox, processes, "install", source);
        Equal(0, code, "install; " + stderr);
        Check(stdout.StartsWith($"Installing {source}...\n", StringComparison.Ordinal) && stdout.EndsWith($"Installed {source}\n", StringComparison.Ordinal), "install output: " + stdout);
        Check(File.Exists(Path.Combine(target, "prompts", "first.md")), "cloned into <agentDir>/git/<host>/<path>");
        Equal("*\n!.gitignore\n", File.ReadAllText(Path.Combine(sandbox.AgentDir, "git", ".gitignore")), "git root ignored");
        Equal(Jstr(new[] { source }), Packages(settingsPath), "settings");
        (_, stdout, _) = await RunPackages(sandbox, processes, "list");
        Equal($"User packages:\n  {source}\n    {target}\n", stdout, "list");

        var v2 = await Commit("prompts/second.md", "Second");
        (code, stdout, stderr) = await RunPackages(sandbox, processes, "update", "--extensions");
        Equal(0, code, "update; " + stderr);
        Check(stdout.StartsWith($"Updating {source}...\n", StringComparison.Ordinal) && stdout.EndsWith("Updated packages\n", StringComparison.Ordinal), "update output: " + stdout);
        Check(File.Exists(Path.Combine(target, "prompts", "second.md")), "new commit pulled");
        Equal(v2, await Git(target, "rev-parse", "HEAD"), "at the remote head");
        (code, stdout, _) = await RunPackages(sandbox, processes, "update", source);
        Equal(0, code, "targeted update"); Check(stdout.EndsWith($"Updated {source}\n", StringComparison.Ordinal), "targeted update output");

        var run = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
        Equal(0, run.Code, "session; " + run.Err);
        Check(sandbox.Requests[^1].Json.GetProperty("system")[0].GetProperty("text").GetString()!.Contains("Skill from a git package", StringComparison.Ordinal), "git package skill in the run");

        // Pinning a ref replaces the settings entry and checks the ref out.
        (code, _, stderr) = await RunPackages(sandbox, processes, "install", source + "@v1");
        Equal(0, code, "pin; " + stderr);
        Equal(Jstr(new[] { source + "@v1" }), Packages(settingsPath), "pinned entry replaced in place");
        Equal(v1, await Git(target, "rev-parse", "HEAD"), "checked out v1");
        Check(!File.Exists(Path.Combine(target, "prompts", "second.md")), "later files gone");
        await Commit("prompts/third.md", "Third");
        Equal(0, (await RunPackages(sandbox, processes, "update", "--extensions")).Code, "pinned update");
        Equal(v1, await Git(target, "rev-parse", "HEAD"), "pinned ref kept");

        (code, stdout, _) = await RunPackages(sandbox, processes, "remove", source);
        Equal(0, code, "remove"); Equal($"Removing {source}...\nRemoved {source}\n", stdout, "remove output");
        Check(!Directory.Exists(target) && !Directory.Exists(Path.Combine(sandbox.AgentDir, "git", "example.test")), "checkout removed, empty parents pruned");
        Equal("[]", Packages(settingsPath), "settings emptied");

        (code, _, stderr) = await RunPackages(sandbox, processes, "install", "-l", source);
        Equal(0, code, "project install; " + stderr);
        var projectTarget = Path.Combine(sandbox.Cwd, ".pi", "git", "example.test", "owner", "ext-repo");
        Check(File.Exists(Path.Combine(projectTarget, "prompts", "third.md")), "project checkout at the remote head");
        Equal(Jstr(new[] { source }), Packages(Path.Combine(sandbox.Cwd, ".pi", "settings.json")), "project settings");

        // Temporary -e checkouts are refreshed on each resolve.
        var manager = new PiPackageManager(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true), _ => null,
            new PiPackageProcesses { Environment = environment, Output = TextWriter.Null, ErrorOutput = TextWriter.Null });
        var temporary = await manager.ResolveExtensionSourcesAsync([source], temporary: true);
        var tempPrompt = temporary.Prompts.First(r => r.Path.EndsWith("third.md", StringComparison.Ordinal)).Path;
        Check(tempPrompt.StartsWith(Path.Combine(sandbox.AgentDir, "tmp", "extensions", "git-example.test") + Path.DirectorySeparatorChar, StringComparison.Ordinal), "temporary checkout location");
        await Commit("prompts/fourth.md", "Fourth");
        temporary = await manager.ResolveExtensionSourcesAsync([source], temporary: true);
        Check(temporary.Prompts.Any(r => r.Path.EndsWith("fourth.md", StringComparison.Ordinal)), "temporary checkout refreshed");
    }
}
