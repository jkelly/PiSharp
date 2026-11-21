using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using static Expect;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/tools.test.ts ("should keep a text-only model's image
// warning out of what the model gets", #12131), the part that renders the read result: core/tools/renderers/read.ts shows the
// warning from details.imageUnsupportedByModel, collapsed and expanded. The tool side of the case is sync160.read-image-* in
// PiSharp.Tools.Tests. Read from the source, not captured from a run.
internal static class Sync160ReadImageCases
{
    private static IEnumerable<(string Id, Func<Task> Run)> All() => [.. ToolRenderingCases.Sync160ReadImage()];
}

internal static partial class ToolRenderingCases
{
    internal static IEnumerable<(string Id, Func<Task> Run)> Sync160ReadImage()
    {
        const string warning = "[text-only does not support images; it saw a placeholder instead]";
        // The result as the read tool stores it for a model without image input: the note, the image, and the model's id in details.
        static JsonObject ImageResult(JsonNode? details, bool isError = false)
        {
            var result = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Read image file [image/png]" },
                    new JsonObject { ["type"] = "image", ["data"] = TinyPng, ["mimeType"] = "image/png" }),
                ["isError"] = isError
            };
            if (details is not null) result["details"] = details;
            return result;
        }

        yield return ("sync160.read-image-warning.collapsed-and-expanded", Sync(() =>
        {
            var component = Tool("read", """{"path":"text-model.png"}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory);
            component.UpdateResult(ImageResult(JsonNode.Parse("""{"imageUnsupportedByModel":"text-only"}""")));
            var collapsed = Render(component, 120);
            Contains(collapsed, warning, "collapsed");
            Check(!collapsed.Contains("Read image file", StringComparison.Ordinal), "collapsed read shows the warning only: " + collapsed);
            component.SetExpanded(true);
            var expanded = Render(component, 120);
            Contains(expanded, warning, "expanded");
            Contains(expanded, "Read image file [image/png]", "expanded note");
            Check(expanded.IndexOf(warning, StringComparison.Ordinal) > expanded.IndexOf("Read image file [image/png]", StringComparison.Ordinal), "the warning follows the output");
        }));

        yield return ("sync160.read-image-warning.only-with-the-detail", Sync(() =>
        {
            foreach (var details in new JsonNode?[] { null, new JsonObject(), JsonNode.Parse("""{"imageUnsupportedByModel":""}"""),
                         JsonNode.Parse("""{"truncation":{"truncated":false}}""") })
            {
                var component = Tool("read", """{"path":"vision-model.png"}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory);
                component.UpdateResult(ImageResult(details?.DeepClone()));
                Check(!Render(component, 120).Contains("does not support images", StringComparison.Ordinal), "no warning collapsed for " + (details?.ToJsonString() ?? "no details"));
                component.SetExpanded(true);
                Check(!Render(component, 120).Contains("does not support images", StringComparison.Ordinal), "no warning expanded for " + (details?.ToJsonString() ?? "no details"));
            }
        }));

        yield return ("sync160.read-image-warning.renderer-text", Sync(() =>
        {
            var theme = Themes.Current;
            var renderers = BuiltInToolRenderers.ForToolDefinition("read")!;
            string Result(bool expanded, bool isError) => string.Join("\n", renderers.RenderResult!(
                ImageResult(JsonNode.Parse("""{"imageUnsupportedByModel":"text-only"}"""), isError), new ToolRenderResultOptions(expanded, false), theme,
                new ToolRenderContext { Args = Args("""{"path":"text-model.png"}"""), Cwd = Environment.CurrentDirectory, Expanded = expanded, IsError = isError }).Render(120));
            // formatReadResult: collapsed and not an error, the result is the warning alone, in the warning color.
            Contains(Result(false, false), theme.Fg("warning", warning), "collapsed warning color");
            Equal(warning, Strip(Result(false, false)).Trim(), "collapsed text");
            Contains(Result(true, false), theme.Fg("warning", warning), "expanded warning color");
            Contains(Strip(Result(false, true)), warning, "an error result keeps the warning");
        }));
    }
}
