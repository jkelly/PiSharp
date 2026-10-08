using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Settings;

internal static class ToolSelectionCliFlagTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("startup tools CLI disable aliases and explicit precedence", Precedence),
        ("startup tools CLI exclusions ordered replacement and missing values", Exclusions),
        ("startup tools CLI actual disabled create and inspect rejection", Commands),
        ("startup tools CLI exclusion defaults use admitted catalog", ExcludedCatalog)
    ];
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Tool selection flag contract failed."); }
    private static ToolSelectionCliOptions Parse(params string[] args)
    {
        var result = new ToolSelectionCliOptions();
        for (var i = 0; i < args.Length; i++) Check(ToolSelectionCliConfiguration.TryConsume(args, ref i, ref result));
        return result;
    }
    private static Task Precedence()
    {
        foreach (var alias in new[] { "--no-tools", "-nt" })
        {
            var disabled = ToolSelectionCliConfiguration.ResolveOptions(Parse(alias), null)!;
            Check(disabled.Names.IsEmpty && !disabled.IncludeDefaultExtensions && !disabled.LifetimePolicy!.IsAllowed("extension"));
            foreach (var flags in new[] { new[] { alias, "--tools", "read" }, new[] { "-t", "read", alias } })
            {
                var selected = ToolSelectionCliConfiguration.ResolveOptions(Parse(flags), null)!;
                Check(selected.Names.SequenceEqual(new[] { "read" }) && selected.LifetimePolicy!.IsAllowed("read") && !selected.LifetimePolicy.IsAllowed("write"));
            }
        }
        foreach (var alias in new[] { "--no-builtin-tools", "-nbt" })
        {
            var selection = ToolSelectionCliConfiguration.ResolveOptions(Parse(alias), null)!;
            Check(selection.Names.IsEmpty && selection.IncludeDefaultExtensions && selection.LifetimePolicy!.IsAllowed("read"));
            Check(!ToolSelectionCliConfiguration.ResolveOptions(Parse(alias, "-nt"), null)!.IncludeDefaultExtensions);
            Check(!ToolSelectionCliConfiguration.ResolveOptions(Parse("-nt", alias), null)!.IncludeDefaultExtensions);
        }
        Check(ToolSelectionCliConfiguration.ResolveOptions(new ToolSelectionCliOptions(), null) is null);
        return Task.CompletedTask;
    }
    private static Task Exclusions()
    {
        foreach (var alias in new[] { "--exclude-tools", "-xt" })
        {
            var selection = ToolSelectionCliConfiguration.ResolveOptions(Parse("-t", "write, read,read", alias, " write , ,"), null)!;
            Check(selection.Names.SequenceEqual(new[] { "read" }) && !selection.LifetimePolicy!.IsAllowed("write"));
            var repeated = ToolSelectionCliConfiguration.ResolveOptions(Parse(alias, "read", alias, "write"), null)!;
            Check(repeated.LifetimePolicy!.IsAllowed("read") && !repeated.LifetimePolicy.IsAllowed("write"));
            try { Parse(alias); throw new InvalidOperationException("Missing value accepted."); }
            catch (SessionCommandException e) { Check(e.Failure == SessionCommandFailure.InvalidArguments); }
        }
        Check(ToolSelectionCliConfiguration.ResolveOptions(Parse("-t", "", "-xt", ""), null)!.LifetimePolicy!.AllowedNames!.IsEmpty);
        return Task.CompletedTask;
    }
    private static async Task ExcludedCatalog()
    {
        using var fixture = new StartupSettingsTests.Fixture();
        using var output = new StringWriter(); using var error = new StringWriter();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var path = Path.Combine(fixture.Root, "session.jsonl");
        Check(await SessionCommands.RunAsync(["session", "create", "--session", path, "--workspace", fixture.Root,
            "--exclude-tools", "write"], output, error, stop.Token) == 0);
        Check(error.ToString() == "");
        var names = new List<string>();
        foreach (var line in await File.ReadAllLinesAsync(path, stop.Token))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("toolsAdded", out var tools))
                names.AddRange(tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!));
        }
        Check(names.Contains("read") && !names.Contains("write") && !names.Contains("bash"));
    }
    private static async Task Commands()
    {
        foreach (var alias in new[] { "--no-tools", "-nt", "--no-builtin-tools", "-nbt" })
        {
            using var fixture = new StartupSettingsTests.Fixture();
            using var output = new StringWriter(); using var error = new StringWriter();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var path = Path.Combine(fixture.Root, "session.jsonl");
            Check(await SessionCommands.RunAsync(["session", "create", "--session", path, "--workspace", fixture.Root, alias], output, error, stop.Token) == 0);
            Check(error.ToString() == "");
            var before = await File.ReadAllBytesAsync(path, stop.Token);
            using var inspectOutput = new StringWriter(); using var inspectError = new StringWriter();
            Check(await SessionCommands.RunAsync(["session", "inspect", "--session", path, alias], inspectOutput, inspectError, stop.Token) == 2);
            var after = await File.ReadAllBytesAsync(path, stop.Token);
            Check(before.SequenceEqual(after));
            Check((await File.ReadAllLinesAsync(path, stop.Token)).Any(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("toolsAdded", out var tools) && tools.GetArrayLength() == 0;
            }));
        }
    }
}
