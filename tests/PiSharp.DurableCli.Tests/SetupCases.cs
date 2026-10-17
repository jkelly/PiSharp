using System.Reflection;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Pi;
using PiSharp.Cli.Prompts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

internal static partial class Program
{
    // The runtime/TUI contract compiles and its value types behave as plain records.
    private static IEnumerable<(string, Func<Task>)> SetupCases() =>
    [
        Case("setup.contract.model-summary-ref", () =>
        {
            var model = new DurableModelSummary("anthropic", "claude-sonnet-4-5", "Claude Sonnet 4.5", 200_000);
            Equal(new ModelRef("anthropic", "claude-sonnet-4-5"), model.Ref, "ref");
        }),
        // Durable work list area e: pisharp durable's coding tools read images through an image processor on the Skia codec, so read lists
        // bmp and converts a BMP to PNG.
        Case("setup.coding-tools.read-prepares-images-with-the-skia-processor", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "pisharp-durable-cli-images-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var registry = DurableHarnessSetup.CreateCodingRegistry(PiSettings.FromObjects(new JsonObject()), directory,
                    new DurablePromptResources(directory, directory, new OriginalPromptDocumentation("", "", "")));
                var read = registry.Snapshot().GetExtension("coding-tools")!.Tools!.First(tool => tool.Name == "read");
                Check(read.Description.Contains("images (jpg, png, gif, webp, bmp)", StringComparison.Ordinal), read.Description);
                // A 2x2 24-bit BMP.
                var bmp = new byte[54 + 8 * 2];
                (bmp[0], bmp[1]) = ((byte)'B', (byte)'M');
                BitConverter.TryWriteBytes(bmp.AsSpan(2), bmp.Length);
                BitConverter.TryWriteBytes(bmp.AsSpan(10), 54);
                BitConverter.TryWriteBytes(bmp.AsSpan(14), 40);
                BitConverter.TryWriteBytes(bmp.AsSpan(18), 2);
                BitConverter.TryWriteBytes(bmp.AsSpan(22), 2);
                BitConverter.TryWriteBytes(bmp.AsSpan(26), (ushort)1);
                BitConverter.TryWriteBytes(bmp.AsSpan(28), (ushort)24);
                await File.WriteAllBytesAsync(Path.Combine(directory, "a.bmp"), bmp);
                var result = await read.Execute(JsonValue.Object([new("path", "a.bmp")]), new ReadApi(new LocalExecutionEnv(directory)), DurableContext.Background);
                Equal("image/png", ((JsonStringValue)((JsonObjectValue)result.Output![0])["mimeType"]).Value, "converted");
                Equal("Read image file [image/png]. Converted from image/bmp to image/png.", result.Diagnostics!.Value[0].Message, "diagnostic");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        // Durable work list area l: pisharp durable installs pi-durable 1.2.0's tool_search as the coding agent registers it, switched off
        // until a tool is deferred. It defers none, so the model is offered the tools it was offered before.
        Case("setup.tool-search.installed-off-until-a-tool-is-deferred", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "pisharp-durable-cli-tool-search-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var settings = PiSettings.FromObjects(new JsonObject());
                var registry = DurableHarnessSetup.CreateCodingRegistry(settings, directory,
                    new DurablePromptResources(directory, directory, new OriginalPromptDocumentation("", "", "")));
                registry.Install(DurableSubagent.Extension);
                var harnessSettings = DurableHarnessSetup.CreateHarnessSettings(settings, registry);
                Agent Resolve() => Agents.ResolveAgent(null, registry.Snapshot(), Agents.ResolveSettings(harnessSettings), error => throw error);
                static string Names(IEnumerable<ToolRegistration> tools) => string.Join(",", tools.Select(tool => tool.Name));

                var installed = registry.Snapshot().GetExtension("tool-search");
                Equal(ToolSearch.ToolName, Names(installed?.Tools ?? []), "tool_search installed");
                var off = Resolve();
                Equal("read,write,edit,bash,ask_user_question,exit_plan_mode,subagent", Names(off.Tools), "the tools offered without deferred tools");
                Equal("coding-tools,pi-prompt,user-interaction,subagent", string.Join(",", off.Extensions.Select(extension => extension.Name)), "selected extensions");
                Check(off.Deferred.IsEmpty, "nothing deferred");

                // An extension with a deferred tool turns it on: the model is offered tool_search, which loads the deferred tool.
                var lookup = Define.Tool(new ToolRegistration
                {
                    Name = "lookup_ticket",
                    Description = "Looks up a ticket.",
                    Deferred = true,
                    Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult()),
                });
                var tickets = Define.Extension(new Extension { Name = "tickets", Tools = [lookup] });
                registry.Install(tickets);
                var on = Resolve();
                Equal("read,write,edit,bash,tool_search,ask_user_question,exit_plan_mode,subagent", Names(on.Tools), "tool_search offered");
                Equal("lookup_ticket", Names(on.Deferred), "the deferred tool");
                // And off again once nothing is deferred.
                registry.Uninstall(tickets);
                Equal("read,write,edit,bash,ask_user_question,exit_plan_mode,subagent", Names(Resolve().Tools), "off again");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
    ];

    /// <summary>The members of the tool api read uses: its environment and an agent without a model.</summary>
    private sealed class ReadApi(IExecutionEnv env) : DelegatingToolExecutionApi(DispatchProxy.Create<IToolExecutionApi, UnusedApi>())
    {
        public override IExecutionEnv? Env => env;
        public override Task<Agent> AgentAsync(DurableContext context) =>
            Task.FromResult(new Agent { ThinkingLevel = "off", Extensions = [], Tools = [], Callable = [], Deferred = [], Sections = [] });
    }

    public class UnusedApi : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name);
    }
}
