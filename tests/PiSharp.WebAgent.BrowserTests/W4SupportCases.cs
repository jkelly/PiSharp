using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, T4): the helpers of the W4 browser cases. They run on the shared Agent (faux or priced faux) and Window, and fail
// on any page or console error as every case does. BrowseGit runs a case whose app may use git (agent compare on a temporary repository):
// the app, and every git command the case runs, sees an empty global git config and no system config (GIT_CONFIG_GLOBAL and
// GIT_CONFIG_NOSYSTEM, set for the case only and put back after it), so this machine's git settings can't change what is checked and nothing
// touches the user's repositories. The seeding helpers drive the same JSON APIs the pages post to (a Playground run, a Compare group, an
// agent compare run), so a case can open a page that already holds real records; no page is given made-up data.
internal static partial class Program
{
    /// <summary>A case on a fresh app in <paramref name="mode"/> whose git (and the case's, through <see cref="W4Git"/>) runs with an empty
    /// global config and no system config, in a temporary folder of its own (<see cref="W4Git.Root"/>).</summary>
    internal static (string, Func<Task>) BrowseGit(string id, AgentMode mode, Func<Agent, Window, W4Git, Task> run, int width = 1440, int height = 900,
        string approvalMode = "ask") =>
        Case(id, async () =>
        {
            var git = W4Git.Create(id.Replace("browser.", "", StringComparison.Ordinal));
            var saved = (Global: Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"), NoSystem: Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM"));
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", git.Config);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            try
            {
                await using var agent = await Agent.StartAsync(id.Replace("browser.", "", StringComparison.Ordinal), mode, approvalMode);
                await using var window = await Window.OpenAsync(agent, width, height);
                try
                {
                    await run(agent, window, git);
                    window.NoErrors();
                }
                catch
                {
                    await window.SaveAsync(id);
                    throw;
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", saved.Global);
                Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", saved.NoSystem);
                git.Delete();
            }
        });

    /// <summary>A temporary folder with an empty git config, where a case makes repositories and runs git (a test identity, no signing).</summary>
    internal sealed class W4Git
    {
        private W4Git(string root) => Root = root;

        public string Root { get; }

        /// <summary>The empty global config the app and the case's git use.</summary>
        public string Config => Path.Join(Root, "gitconfig");

        public static W4Git Create(string name)
        {
            var git = new W4Git(Temp("git-" + name));
            File.WriteAllText(git.Config, "");
            return git;
        }

        /// <summary>A repository on <c>main</c> with <paramref name="files"/> in one commit; returns its folder.</summary>
        public async Task<string> RepoAsync(string name, params (string Path, string Text)[] files)
        {
            var repo = Path.Join(Root, name);
            Directory.CreateDirectory(repo);
            await GitAsync(repo, "init", "-q", "-b", "main");
            foreach (var (path, text) in files)
            {
                var full = Path.Join(repo, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            await GitAsync(repo, "add", "-A");
            await GitAsync(repo, "commit", "-q", "-m", "first");
            return repo;
        }

        /// <summary>Runs git in <paramref name="cwd"/> and returns its trimmed output; a failure throws.</summary>
        public async Task<string> GitAsync(string cwd, params string[] arguments)
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-c", "user.name=PiSharp Tests", "-c", "user.email=tests@pisharp.invalid", "-c", "commit.gpgsign=false", "-c", "core.quotepath=false" })
                info.ArgumentList.Add(argument);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            info.Environment["GIT_CONFIG_GLOBAL"] = Config;
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {await error}");
            return (await output).Trim();
        }

        public void Delete()
        {
            // Git marks its objects read-only; clear that so the temporary folder can go.
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A temporary directory. */ }
            DeleteFolder(Root);
        }
    }

    // ─── Seeding through the pages' own APIs ────────────────────────────────

    /// <summary>Posts a Playground run (<c>POST /api/runs</c>, faux <paramref name="model"/>, one user message) and waits until it has
    /// ended; returns its id.</summary>
    internal static async Task<string> PlaygroundRunAsync(this Agent app, string text, string model = "echo", string? system = null)
    {
        var body = new JsonObject
        {
            ["provider"] = "faux",
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = text }),
        };
        if (system is not null) body["systemPrompt"] = system;
        var posted = await app.PostAsync("/api/runs", body);
        Check(posted.Status == 200 && posted.Body?["posted"]?.GetValue<bool>() == true, "a Playground run: " + posted.Text);
        var id = posted.Body!["id"]!.GetValue<string>();
        await app.RunEndedAsync(id);
        return id;
    }

    /// <summary>Waits until run <paramref name="id"/> is no longer running (<c>GET /api/runs/{id}</c>) and returns its status.</summary>
    internal static async Task<string> RunEndedAsync(this Agent app, string id)
    {
        var status = "";
        await WaitUntilAsync(async () =>
        {
            var run = await app.Get("/api/runs/" + id);
            status = run.Body?["run"]?["status"]?.GetValue<string>() ?? "";
            return run.Status == 200 && run.Body?["running"]?.GetValue<bool>() == false && status is not ("" or "running" or "queued");
        }, $"run {id} ended", TimeSpan.FromSeconds(30));
        return status;
    }

    /// <summary>Runs one Compare group (<c>POST /api/compare/run</c>, a faux model per column) and waits until every column has ended;
    /// returns the group id and the column runs.</summary>
    internal static async Task<(string Group, List<string> Runs)> CompareGroupAsync(this Agent app, string prompt, params string[] models)
    {
        var columns = new JsonArray([.. models.Select(model => (JsonNode)new JsonObject { ["provider"] = "faux", ["model"] = model })]);
        var posted = await app.PostAsync("/api/compare/run", new JsonObject { ["prompt"] = prompt, ["columns"] = columns });
        Check(posted.Status == 200 && posted.Body?["posted"]?.GetValue<bool>() == true, "a Compare group: " + posted.Text);
        var group = posted.Body!["groupId"]!.GetValue<string>();
        var runs = posted.Body["ids"]!.AsArray().Select(id => id!.GetValue<string>()).ToList();
        foreach (var run in runs) await app.RunEndedAsync(run);
        return (group, runs);
    }

    /// <summary>Starts an agent compare run (<c>POST /api/compare/agent</c>) on <paramref name="project"/> with faux
    /// <paramref name="models"/>, tools on Auto, and waits until it has finished; returns its status.</summary>
    internal static async Task<JsonNode> AgentCompareAsync(this Agent app, string requestId, string task, string project, string isolation, params string[] models)
    {
        var created = await app.PostAsync("/api/compare/agent", new JsonObject
        {
            ["requestId"] = requestId,
            ["task"] = task,
            ["projectPath"] = project,
            ["models"] = new JsonArray([.. models.Select(model => (JsonNode)new JsonObject { ["provider"] = "faux", ["model"] = model })]),
            ["approval"] = "auto",
            ["isolation"] = isolation,
            ["checkCommand"] = "echo checked",
        });
        Equal(200, created.Status, "an agent compare run: " + created.Text);
        var id = created.Body!["id"]!.GetValue<string>();
        JsonNode status = created.Body;
        await WaitUntilAsync(async () => (status = (await app.Get("/api/compare/agent/" + id)).Body!)["state"]?.GetValue<string>() == "finished",
            $"agent compare run {id} finished", TimeSpan.FromSeconds(60));
        return status;
    }

    /// <summary>A small project folder outside git under the case's work folder; returns it.</summary>
    internal static string W4Project(this Agent app, string name = "project")
    {
        var project = Path.Join(app.Work, name);
        Directory.CreateDirectory(Path.Join(project, "src"));
        File.WriteAllText(Path.Join(project, "README.md"), "# Sample\n");
        File.WriteAllText(Path.Join(project, "src", "app.txt"), "one\n");
        return project;
    }

    // ─── Page helpers ────────────────────────────────────────────────────────

    /// <summary>The text of a Playground metric (<c>[data-metric=name] .metric-value</c>).</summary>
    internal static Task<string> MetricAsync(this IPage page, string name) =>
        page.Locator($"[data-run-output] [data-metric='{name}'] .metric-value").InnerTextAsync();

    /// <summary>Clicks <paramref name="opener"/> and waits until the Bootstrap modal <paramref name="id"/> has finished opening.</summary>
    internal static async Task<ILocator> OpenModalAsync(this IPage page, ILocator opener, string id)
    {
        await opener.ClickAsync();
        await page.ModalShownAsync(id);
        return page.Locator("#" + id);
    }

    /// <summary>Closes the open modal <paramref name="id"/> with its own dismiss button and waits until it is hidden.</summary>
    internal static async Task DismissModalAsync(this IPage page, string id)
    {
        await page.ModalShownAsync(id);
        await page.Locator($"#{id} [data-bs-dismiss='modal'] >> visible=true").First.ClickAsync();
        await Expect(page.Locator("#" + id)).ToBeHiddenAsync();
    }

    /// <summary>The query string value <paramref name="name"/> of the page's address, once it has one.</summary>
    internal static async Task<string> QueryAsync(this IPage page, string name)
    {
        string? value = null;
        await WaitUntilAsync(() =>
        {
            var query = new Uri(page.Url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).Where(part => part[0] == name).Select(part => Uri.UnescapeDataString(part.Length > 1 ? part[1] : ""));
            value = query.FirstOrDefault();
            return Task.FromResult(!string.IsNullOrEmpty(value));
        }, $"the address has ?{name}=", TimeSpan.FromSeconds(15));
        return value!;
    }
}
