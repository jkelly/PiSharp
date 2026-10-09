// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/renderers/edit.ts.
// The preview diff is computed off the render path; its completion is posted to the synchronization context of the render that
// requested it (the TUI loop) before the row is invalidated, like the source's promise continuation.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Presentation for the edit tool.</summary>
internal static class EditRenderers
{
    /// <summary>EditRenderState.callComponent.</summary>
    private const string CallComponentKey = "callComponent";

    internal sealed class EditCallRenderComponent() : Box(1, 1, text => text)
    {
        public EditDiffPreview? Preview;
        public string? PreviewArgsKey;
        public bool PreviewPending;
        public bool SettledError;
    }

    private static EditCallRenderComponent GetEditCallRenderComponent(Dictionary<string, object?> state, IComponent? lastComponent)
    {
        if (lastComponent is EditCallRenderComponent last)
        {
            state[CallComponentKey] = last;
            return last;
        }
        if (state.GetValueOrDefault(CallComponentKey) is EditCallRenderComponent existing) return existing;
        var component = new EditCallRenderComponent();
        state[CallComponentKey] = component;
        return component;
    }

    private static (string Path, JsonArray Edits)? GetRenderablePreviewInput(JsonNode? args)
    {
        if (args is null) return null;

        var path = ToolJson.AsString(ToolJson.Get(args, "path")) ?? ToolJson.AsString(ToolJson.Get(args, "file_path"));
        if (string.IsNullOrEmpty(path)) return null;

        if (ToolJson.Get(args, "edits") is JsonArray edits && edits.Count > 0 &&
            edits.All(edit => ToolJson.AsString(ToolJson.Get(edit, "oldText")) is not null && ToolJson.AsString(ToolJson.Get(edit, "newText")) is not null))
            return (path, edits);

        if (ToolJson.AsString(ToolJson.Get(args, "oldText")) is { } oldText && ToolJson.AsString(ToolJson.Get(args, "newText")) is { } newText)
            return (path, new JsonArray(new JsonObject { ["oldText"] = oldText, ["newText"] = newText }));

        return null;
    }

    private static string? ArgsKey((string Path, JsonArray Edits)? previewInput) => previewInput is { } input
        ? ToolJson.Stringify(new JsonObject { ["path"] = input.Path, ["edits"] = input.Edits.DeepClone() })
        : null;

    private static string FormatEditCall(JsonNode? args, Theme theme, string cwd)
    {
        var pathDisplay = RenderUtils.RenderToolPath(RenderUtils.Str(ToolJson.Get(args, "file_path") ?? ToolJson.Get(args, "path")), theme, cwd);
        return theme.Fg("toolTitle", theme.Bold("edit")) + " " + pathDisplay;
    }

    private static string? FormatEditResult(JsonNode? args, EditDiffPreview? preview, JsonObject result, Theme theme, bool isError)
    {
        var rawPath = RenderUtils.Str(ToolJson.Get(args, "file_path") ?? ToolJson.Get(args, "path"));
        var previewDiff = preview is { IsError: false } ? preview.Diff : null;
        var previewError = preview is { IsError: true } ? preview.Error : null;
        if (isError)
        {
            var content = ToolJson.Get(result, "content") as JsonArray ?? [];
            var errorText = string.Join("\n", content.Where(c => ToolJson.GetString(c, "type") == "text").Select(c => ToolJson.TruthyString(ToolJson.Get(c, "text"))));
            if (errorText.Length == 0 || errorText == previewError) return null;
            return theme.Fg("error", errorText);
        }

        var resultDiff = ToolJson.Get(ToolJson.Get(result, "details"), "diff");
        if (ToolJson.Truthy(resultDiff) && ToolJson.AsString(resultDiff) is { } diff && diff != previewDiff)
            return Diff.RenderDiff(diff, new RenderDiffOptions(rawPath));

        return null;
    }

    private static Func<string, string> GetEditHeaderBg(EditDiffPreview? preview, bool settledError, Theme theme)
    {
        if (preview is not null)
        {
            if (preview.IsError) return text => theme.Bg("toolErrorBg", text);
            return text => theme.Bg("toolSuccessBg", text);
        }
        if (settledError) return text => theme.Bg("toolErrorBg", text);
        return text => theme.Bg("toolPendingBg", text);
    }

    private static EditCallRenderComponent BuildEditCallComponent(EditCallRenderComponent component, JsonNode? args, Theme theme, string cwd, int outputPad)
    {
        component.SetBgFn(GetEditHeaderBg(component.Preview, component.SettledError, theme));
        component.SetPaddingX(outputPad);
        component.Clear();
        component.AddChild(new Text(FormatEditCall(args, theme, cwd), 0, 0));

        if (component.Preview is not { } preview) return component;

        var body = preview.IsError ? theme.Fg("error", preview.Error!) : Diff.RenderDiff(preview.Diff!);
        component.AddChild(new Spacer(1));
        component.AddChild(new Text(body, 0, 0));
        return component;
    }

    private static bool SetEditPreview(EditCallRenderComponent component, EditDiffPreview preview, string? argsKey)
    {
        var current = component.Preview;
        var changed = current is null ||
            (current.IsError && preview.IsError ? current.Error != preview.Error : current.IsError != preview.IsError) ||
            (!current.IsError && !preview.IsError && (current.Diff != preview.Diff || current.FirstChangedLine != preview.FirstChangedLine));
        component.Preview = preview;
        component.PreviewArgsKey = argsKey;
        component.PreviewPending = false;
        return changed;
    }

    public static ToolRenderers Renderers { get; } = new(
        RenderCall: (args, theme, context) =>
        {
            var component = GetEditCallRenderComponent(context.State, context.LastComponent);
            var previewInput = GetRenderablePreviewInput(args);
            var argsKey = ArgsKey(previewInput);

            if (component.PreviewArgsKey != argsKey)
            {
                component.Preview = null;
                component.PreviewArgsKey = argsKey;
                component.PreviewPending = false;
                component.SettledError = false;
            }

            if (context.ArgsComplete && previewInput is { } input && component.Preview is null && !component.PreviewPending)
            {
                component.PreviewPending = true;
                var requestKey = argsKey;
                var edits = input.Edits.Select(edit => new EditTextPair(ToolJson.GetString(edit, "oldText")!, ToolJson.GetString(edit, "newText")!)).ToList();
                var sync = SynchronizationContext.Current;
                var invalidate = context.Invalidate;
                _ = Task.Run(() => EditDiff.ComputeEditsDiffAsync(input.Path, edits, context.Cwd)).ContinueWith(task =>
                {
                    if (!task.IsCompletedSuccessfully) return;
                    void Apply()
                    {
                        if (component.PreviewArgsKey == requestKey)
                        {
                            SetEditPreview(component, task.Result, requestKey);
                            invalidate();
                        }
                    }
                    if (sync is not null) sync.Post(_ => Apply(), null);
                    else Apply();
                }, TaskScheduler.Default);
            }

            return BuildEditCallComponent(component, args, theme, context.Cwd, context.OutputPad);
        },
        RenderResult: (result, _, theme, context) =>
        {
            var callComponent = context.State.GetValueOrDefault(CallComponentKey) as EditCallRenderComponent;
            var argsKey = ArgsKey(GetRenderablePreviewInput(context.Args));
            var resultDiff = !context.IsError ? ToolJson.Get(ToolJson.Get(result, "details"), "diff") : null;
            var changed = false;
            if (callComponent is not null)
            {
                if (ToolJson.AsString(resultDiff) is { } diff)
                {
                    var firstChangedLine = ToolJson.GetNumber(ToolJson.Get(result, "details"), "firstChangedLine");
                    changed = SetEditPreview(callComponent, EditDiffPreview.Result(diff, firstChangedLine is { } line ? (int)line : null), argsKey) || changed;
                }
                if (callComponent.SettledError != context.IsError)
                {
                    callComponent.SettledError = context.IsError;
                    changed = true;
                }
                if (changed) BuildEditCallComponent(callComponent, context.Args, theme, context.Cwd, context.OutputPad);
            }

            var output = FormatEditResult(context.Args, callComponent?.Preview, result, theme, context.IsError);
            var component = context.LastComponent as Container ?? new Container();
            component.Clear();
            if (output is null) return component;
            component.AddChild(new Spacer(1));
            component.AddChild(new Text(output, context.OutputPad, 0));
            return component;
        });
}
