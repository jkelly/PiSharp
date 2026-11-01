// Authored for PiSharp (no upstream test file): the containment cases of the PiSharp-only plan mode (decision 0005, "PiSharp-only
// additions: ask_user_question and plan mode"). They run the wrapped write and edit tools directly, with a LocalExecutionEnv and a plan
// state document, and check that nothing outside the plan directory changes. Link cases skip, with the reason, where this process cannot
// create the link (symbolic links on Windows need a privilege or Developer Mode; junctions exist only on Windows).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools;
using static InteractionTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> PlanScopeCases() => PlanScopeTests.Cases("plan.");
}

/// <summary>The plan.* containment cases.</summary>
internal static class PlanScopeTests
{
    /// <summary>A plan root inside a scratch directory, an outside directory beside it, and the wrapped tools.</summary>
    private sealed class Scope
    {
        public Scope(string name)
        {
            Dirs = new Dirs(name);
            Root = Path.GetFullPath(Dirs.Plans);
            Api = new ReaderApi(Dirs.Env, new PlanStateReader(PlanState(Root)), new ConversationId(1));
        }

        public Dirs Dirs { get; }
        public string Root { get; }
        public string Outside => Dirs.Outside;
        public ReaderApi Api { get; }
        public ToolRegistration WriteTool { get; } = PlanScope.Wrap(CodingTools.CreateWriteTool());
        public ToolRegistration EditTool { get; } = PlanScope.Wrap(CodingTools.CreateEditTool());

        public Task Write(string path, string content = "x") => WriteTool.Execute(Obj(("path", path), ("content", content)), Api, Ctx);

        public Task Edit(string path, string oldText, string newText) =>
            EditTool.Execute(Obj(("path", path), ("edits", JsonValue.Array(Obj(("oldText", oldText), ("newText", newText))))), Api, Ctx);

        /// <summary>Asserts the call fails as plan mode's denial.</summary>
        public async Task Denied(Func<Task> call, string what)
        {
            Exception? error = null;
            try { await call(); }
            catch (Exception thrown) { error = thrown; }
            Check(error is FileError { Code: FileErrorCode.PermissionDenied } && error.Message.StartsWith("Plan mode: ", StringComparison.Ordinal) &&
                error.Message.Contains($"is outside the plan directory {Root}", StringComparison.Ordinal), $"{what}: expected a plan-mode denial, got {error?.ToString() ?? "success"}");
        }
    }

    private static void Skip(string reason) => throw new CaseSkippedException(reason);

    /// <summary>Creates a file symbolic link, or skips the case when this process may not.</summary>
    private static void FileLink(string path, string target)
    {
        try { File.CreateSymbolicLink(path, target); }
        catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException)
        {
            Skip("this process cannot create symbolic links (Windows needs SeCreateSymbolicLinkPrivilege or Developer Mode): " + error.Message);
        }
    }

    /// <summary>Creates a directory symbolic link, or skips the case when this process may not.</summary>
    private static void DirectoryLink(string path, string target)
    {
        try { Directory.CreateSymbolicLink(path, target); }
        catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException)
        {
            Skip("this process cannot create symbolic links (Windows needs SeCreateSymbolicLinkPrivilege or Developer Mode): " + error.Message);
        }
    }

    /// <summary>Creates a junction with <c>mklink /J</c>, or skips the case off Windows or when it fails.</summary>
    private static void Junction(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) Skip("junctions exist only on Windows");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", path, target])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(path)) Skip("mklink /J failed: " + error);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint length);

    /// <summary>The 8.3 short name of an existing path, or null when the volume keeps none.</summary>
    private static string? ShortPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var buffer = new StringBuilder(1024);
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) return null;
        var shortPath = buffer.ToString();
        return string.Equals(shortPath, path, StringComparison.OrdinalIgnoreCase) ? null : shortPath;
    }

    private static void Unchanged(string path, string content, string what) => Equal(content, File.ReadAllText(path), what + " unchanged");

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "write-inside-direct", async () =>
        {
            var scope = new Scope("scope-inside");
            await scope.Write(Path.Combine(scope.Root, "plan.md"), "# Plan");
            await scope.Write(Path.Combine(scope.Root, "a", "b", "deep.md"), "deep");
            await scope.Edit(Path.Combine(scope.Root, "plan.md"), "Plan", "Plan v2");
            Equal("# Plan v2", File.ReadAllText(Path.Combine(scope.Root, "plan.md")), "plan.md");
            Equal("deep", File.ReadAllText(Path.Combine(scope.Root, "a", "b", "deep.md")), "deep.md");
            // The root is created when it was removed meanwhile.
            Directory.Delete(scope.Root, recursive: true);
            await scope.Write(Path.Combine(scope.Root, "again.md"), "again");
            Equal("again", File.ReadAllText(Path.Combine(scope.Root, "again.md")), "again.md");
        });

        yield return Case(prefix + "symlink-escape.file-link", async () =>
        {
            var scope = new Scope("scope-file-link");
            var target = Path.Combine(scope.Outside, "target.txt");
            File.WriteAllText(target, "outside");
            var link = Path.Combine(scope.Root, "link.txt");
            FileLink(link, target);
            await scope.Denied(() => scope.Write(link, "overwritten"), "write through a file link");
            await scope.Denied(() => scope.Edit(link, "outside", "edited"), "edit through a file link");
            Unchanged(target, "outside", "the link target");
            // A link to a file inside the root is refused too: fail closed on any link.
            var inner = Path.Combine(scope.Root, "inner.txt");
            File.WriteAllText(inner, "inner");
            var innerLink = Path.Combine(scope.Root, "inner-link.txt");
            FileLink(innerLink, inner);
            await scope.Denied(() => scope.Write(innerLink, "x"), "write through a link inside the root");
            Unchanged(inner, "inner", "the inner file");
        });

        yield return Case(prefix + "symlink-escape.dangling-final", async () =>
        {
            var scope = new Scope("scope-dangling");
            var missing = Path.Combine(scope.Outside, "created-through-link.txt");
            var link = Path.Combine(scope.Root, "dangling.txt");
            FileLink(link, missing);
            await scope.Denied(() => scope.Write(link, "created"), "write through a dangling link");
            Check(!File.Exists(missing), "nothing created outside");
        });

        yield return Case(prefix + "symlink-escape.intermediate-dir-link", async () =>
        {
            var scope = new Scope("scope-dir-link");
            var existing = Path.Combine(scope.Outside, "existing.txt");
            File.WriteAllText(existing, "outside");
            var link = Path.Combine(scope.Root, "sub");
            DirectoryLink(link, scope.Outside);
            await scope.Denied(() => scope.Write(Path.Combine(link, "new.txt")), "write below a directory link");
            await scope.Denied(() => scope.Write(Path.Combine(link, "nested", "new.txt")), "write into a new directory below a directory link");
            await scope.Denied(() => scope.Edit(Path.Combine(link, "existing.txt"), "outside", "edited"), "edit below a directory link");
            Check(!File.Exists(Path.Combine(scope.Outside, "new.txt")) && !Directory.Exists(Path.Combine(scope.Outside, "nested")), "nothing created outside");
            Unchanged(existing, "outside", "the outside file");
        });

        yield return Case(prefix + "symlink-escape.windows-junction", async () =>
        {
            var scope = new Scope("scope-junction");
            var existing = Path.Combine(scope.Outside, "existing.txt");
            File.WriteAllText(existing, "outside");
            var junction = Path.Combine(scope.Root, "j");
            Junction(junction, scope.Outside);
            await scope.Denied(() => scope.Write(Path.Combine(junction, "new.txt")), "write below a junction");
            await scope.Denied(() => scope.Edit(Path.Combine(junction, "existing.txt"), "outside", "edited"), "edit below a junction");
            Check(!File.Exists(Path.Combine(scope.Outside, "new.txt")), "nothing created outside");
            Unchanged(existing, "outside", "the outside file");
            // A dangling junction (its target removed) is refused as well.
            var gone = Path.Combine(scope.Dirs.Outside, "gone");
            Directory.CreateDirectory(gone);
            var dangling = Path.Combine(scope.Root, "dj");
            Junction(dangling, gone);
            Directory.Delete(gone);
            await scope.Denied(() => scope.Write(Path.Combine(dangling, "x.txt")), "write below a dangling junction");
            Check(!Directory.Exists(gone), "the junction target was not recreated");
            // A plan directory that is itself a junction to a drive root confines nothing: entering refuses it, and the wrapper denies.
            var driveRoot = Path.GetPathRoot(scope.Root)!;
            var rootLink = Path.Combine(scope.Dirs.Outside, "root-link");
            Junction(rootLink, driveRoot);
            var setup = NewSetup();
            var (harness, conversation) = await Open(() => new PiSharp.Durable.Storage.MemoryStorage(), setup, scope.Dirs);
            await ThrowsAsync<ArgumentException>(() => PlanMode.EnterAsync(conversation, setup.Registry, rootLink, Ctx), "a plan directory linked to a drive root");
            await harness.CloseAsync(Ctx);
            var linked = new ReaderApi(scope.Dirs.Env, new PlanStateReader(PlanState(rootLink)), new ConversationId(1));
            var probe = Path.Combine(rootLink, "pisharp-plan-probe-" + Guid.NewGuid().ToString("N") + ".txt");
            Exception? refused = null;
            try { await scope.WriteTool.Execute(Obj(("path", probe), ("content", "x")), linked, Ctx); }
            catch (FileError error) { refused = error; }
            Check(refused is FileError { Code: FileErrorCode.PermissionDenied } && refused.Message.Contains("file system root", StringComparison.Ordinal),
                "a write below a plan directory linked to a drive root: " + refused);
            Check(!File.Exists(Path.Combine(driveRoot, Path.GetFileName(probe))), "nothing written at the drive root");
        });

        yield return Case(prefix + "windows-names", async () =>
        {
            var scope = new Scope("scope-names");
            // Refused on every platform: fail closed even where the names would be ordinary.
            string[] names = ["CON", "nul.txt", "Com1.log", "LPT9", "aux .md", "CONIN$", "file.txt:stream", "data::$DATA", "trailing.", "trailing ", "trailing ", "...",
                "PLANAB~1.MD", "what?.md", "star*.md", "pipe|.md", "quote\".md", "less<.md", "back\\slash.md"];
            foreach (var name in names)
            {
                if (OperatingSystem.IsWindows() && name.Contains('\\')) continue; // A separator on Windows: a sub-directory, which is allowed.
                await scope.Denied(() => scope.Write(scope.Root + Path.DirectorySeparatorChar + name), "name " + name);
            }
            await scope.Denied(() => scope.Write(Path.Combine(scope.Root, "CON", "x.md")), "a device name as a directory");
            string[] prefixes = [@"\\?\" + Path.Combine(scope.Root, "x.md"), @"\\.\" + Path.Combine(scope.Root, "x.md"), @"\??\" + Path.Combine(scope.Root, "x.md"),
                @"\\localhost\share\x.md", "//server/share/x.md"];
            foreach (var path in prefixes) await scope.Denied(() => scope.Write(path), "prefix " + path);
            Check(Directory.GetFileSystemEntries(scope.Root).Length == 0, "nothing created in the root: " + string.Join(", ", Directory.GetFileSystemEntries(scope.Root)));
            // A real 8.3 alias of an outside sibling, where the volume keeps short names.
            var sibling = Path.Combine(Path.GetDirectoryName(scope.Root)!, "sibling-with-a-long-name");
            Directory.CreateDirectory(sibling);
            if (ShortPath(sibling) is { } alias)
            {
                await scope.Denied(() => scope.Write(Path.Combine(alias, "x.md")), "an 8.3 alias outside");
                Check(!File.Exists(Path.Combine(sibling, "x.md")), "nothing written through the alias");
            }
        });

        yield return Case(prefix + "root-validation", async () =>
        {
            var setup = NewSetup();
            var dirs = new Dirs("scope-root-validation");
            var (harness, root) = await Open(() => new PiSharp.Durable.Storage.MemoryStorage(), setup, dirs);
            async Task Rejected(string directory, string what) =>
                await ThrowsAsync<ArgumentException>(() => PlanMode.EnterAsync(root, setup.Registry, directory, Ctx), what);
            await Rejected("plans", "a relative directory");
            await Rejected("", "an empty directory");
            await Rejected(Path.GetPathRoot(dirs.Project)!, "a file system root");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await Rejected(home, "the home directory");
            if (Path.GetDirectoryName(home) is { } aboveHome) await Rejected(aboveHome, "an ancestor of the home directory");
            await Rejected(dirs.Project, "the working directory");
            await Rejected(Path.GetDirectoryName(dirs.Project)!, "an ancestor of the working directory");
            await Rejected(@"\\server\share\plans", "a network path");
            await Rejected(@"\\?\" + dirs.Plans, "an extended-length path");
            // A registry without plan-mode, or without exit_plan_mode, cannot enter it.
            var bare = ChatTests.Setup();
            bare.Registry.Install(PiSharp.Durable.Tools.CodingTools.CreateCodingTools());
            bare.Registry.Install(UserInteraction.CreateExtension());
            await ThrowsAsync<InvalidOperationException>(() => PlanMode.EnterAsync(root, bare.Registry, dirs.Plans, Ctx), "no plan-mode extension");
            var noExit = ChatTests.Setup();
            noExit.Registry.Install(PlanMode.CreateExtension());
            await ThrowsAsync<InvalidOperationException>(() => PlanMode.EnterAsync(root, noExit.Registry, dirs.Plans, Ctx), "no exit_plan_mode");
            Check(await PlanMode.StateAsync(harness, root.Id, Ctx) is null, "nothing entered");
            Check(!PlanMode.IsActive(await AgentDoc(harness, root)), "agent unchanged");
            // A wrapped write without a plan directory fails closed.
            var orphan = PlanScope.Wrap(PiSharp.Durable.Tools.CodingTools.CreateWriteTool());
            var api = new ReaderApi(dirs.Env, new PlanStateReader(null), root.Id);
            var error = await ThrowsAsync<InvalidOperationException>(() => orphan.Execute(Obj(("path", Path.Combine(dirs.Plans, "x.md")), ("content", "x")), api, Ctx),
                "no directory");
            Check(error.Message.Contains("no plan directory is set", StringComparison.Ordinal), error.Message);
            Check(!File.Exists(Path.Combine(dirs.Plans, "x.md")), "not written");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "scoped-env-mutations", async () =>
        {
            var scope = new Scope("scope-env");
            var env = new PlanScopedEnv(scope.Dirs.Env, scope.Root);
            var inside = Path.Combine(scope.Root, "inside.txt");
            var outside = Path.Combine(scope.Outside, "outside.txt");
            File.WriteAllText(outside, "outside");
            Check((await env.WriteFileAsync(inside, "in", Ctx)).IsOk, "write inside");
            Check((await env.AppendFileAsync(inside, "+", Ctx)).IsOk, "append inside");
            Check((await env.CreateDirAsync(Path.Combine(scope.Root, "dir"), null, Ctx)).IsOk, "mkdir inside");
            Check((await env.RenameFileAsync(inside, Path.Combine(scope.Root, "renamed.txt"), Ctx)).IsOk, "rename inside");
            Check((await env.RemoveAsync(Path.Combine(scope.Root, "dir"), new RemoveOptions { Recursive = true }, Ctx)).IsOk, "remove inside");
            Check((await env.ReadTextFileAsync(outside, Ctx)).IsOk, "reads pass through");
            static void Refused<T>(PiSharp.Durable.Env.Result<T, FileError> result, string what) =>
                Check(!result.IsOk && result.Error.Code == FileErrorCode.PermissionDenied && result.Error.Message.StartsWith("Plan mode: ", StringComparison.Ordinal), what);
            Refused(await env.WriteFileAsync(outside, "x", Ctx), "write outside");
            Refused(await env.AppendFileAsync(outside, "x", Ctx), "append outside");
            Refused(await env.TruncateFileAsync(outside, 0, Ctx), "truncate outside");
            Refused(await env.FlushFileAsync(outside, Ctx), "flush outside");
            Refused(await env.CreateDirAsync(Path.Combine(scope.Outside, "dir"), null, Ctx), "mkdir outside");
            Refused(await env.RemoveAsync(outside, null, Ctx), "remove outside");
            Refused(await env.RemoveAsync(scope.Root, new RemoveOptions { Recursive = true }, Ctx), "remove the root");
            Refused(await env.RenameFileAsync(outside, Path.Combine(scope.Root, "stolen.txt"), Ctx), "rename from outside");
            Refused(await env.RenameFileAsync(Path.Combine(scope.Root, "renamed.txt"), Path.Combine(scope.Outside, "moved.txt"), Ctx), "rename to outside");
            Refused(await env.CreateTempDirAsync(null, Ctx), "temp dir");
            Refused(await env.CreateTempFileAsync(null, Ctx), "temp file");
            Refused(await env.WatchAsync([new WatchTarget(scope.Root)], _ => { }, Ctx), "watch");
            var exec = await env.ExecAsync(ShellCommand.FromScript("echo hi"), null, Ctx);
            Check(!exec.IsOk, "no commands");
            Unchanged(outside, "outside", "the outside file");
            Check(File.Exists(Path.Combine(scope.Root, "renamed.txt")) && !File.Exists(Path.Combine(scope.Outside, "moved.txt")), "rename results");
            Check(Directory.Exists(scope.Root), "the root stays");
        });
    }
}
