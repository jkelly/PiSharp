// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/tool-execution.ts.
// ensurePngTranscoder (coding-agent/src/utils/image-convert.ts) registers PiSharp's Skia PNG conversion as the Image transcoder.
using System.Text.Json.Nodes;
using PiSharp.Extensions;
using PiSharp.Tools.Skia;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal sealed record ToolExecutionOptions(bool? ShowImages = null, int? ImageWidthCells = null, int? OutputPad = null);

internal sealed class ToolExecutionComponent : Container
{
    private const int FallbackPreviewLines = 10;

    private readonly Box contentBox;
    private readonly Text contentText;
    private readonly MouseRegion contentTextRegion;
    private readonly Container selfRenderContainer;
    private int selfRenderHeight;
    private IComponent? callRendererComponent;
    private IComponent? resultRendererComponent;
    private readonly Dictionary<string, object?> rendererState = new(StringComparer.Ordinal);
    private List<Image> imageComponents = [];
    /// <summary>Inputs of imageComponents, so updateDisplay can reuse images and keep their converted PNG data.</summary>
    private List<(string Data, string MimeType, int WidthCells)> imageSources = [];
    private List<Spacer> imageSpacers = [];
    private readonly string toolName;
    private readonly string toolCallId;
    private JsonNode? args;
    private bool expanded;
    private bool showImages;
    private int imageWidthCells;
    private int outputPad;
    private bool isPartial = true;
    private readonly ToolRenderers? toolDefinition;
    private readonly ITui ui;
    private readonly string cwd;
    private bool executionStarted;
    private bool argsComplete;
    /// <summary>{content, isError, details?, durationMs?}.</summary>
    private JsonObject? result;
    /// <summary>The {content, details} object handed to renderResult for the current result.</summary>
    private JsonObject? resultForRenderer;
    private bool hideComponent;

    public ToolExecutionComponent(string toolName, string toolCallId, JsonNode? args, ToolExecutionOptions? options, ToolRenderers? toolDefinition,
        ITui ui, string cwd)
    {
        options ??= new();
        this.toolName = toolName;
        this.toolCallId = toolCallId;
        this.args = args;
        this.toolDefinition = toolDefinition;
        showImages = options.ShowImages ?? true;
        imageWidthCells = options.ImageWidthCells ?? 60;
        outputPad = options.OutputPad ?? 1;
        this.ui = ui;
        this.cwd = cwd;

        AddChild(new Spacer(1));

        // Always create all shell variants. contentBox is used for default renderer-based composition.
        // selfRenderContainer is used when the tool renders its own framing.
        // contentText is reserved for generic fallback rendering when no tool definition exists.
        contentBox = new Box(1, 1, text => theme.Bg("toolPendingBg", text));
        contentText = new Text("", 1, 1, text => theme.Bg("toolPendingBg", text));
        contentTextRegion = CreateResultRegion(contentText);
        selfRenderContainer = new Container();

        if (HasRendererDefinition()) AddChild(GetRenderShell() == ToolRenderShell.Self ? selfRenderContainer : contentBox);
        else AddChild(contentTextRegion);

        UpdateDisplay();
    }

    /// <summary>A tool drawn by extension renderers (async rows), adapted to <see cref="ToolRenderers"/>.</summary>
    public static ToolExecutionComponent FromExtensionRenderers(string toolName, string toolCallId, JsonNode? args, ToolExecutionOptions? options,
        ExtensionToolRenderers extensionRenderers, ITui ui, string cwd) =>
        new(toolName, toolCallId, args, options, ExtensionToolRendererAdapter.ToToolRenderers(extensionRenderers), ui, cwd);

    private ToolCallRenderer? GetCallRenderer() => toolDefinition?.RenderCall;
    private ToolResultRenderer? GetResultRenderer() => toolDefinition?.RenderResult;
    private bool HasRendererDefinition() => toolDefinition is not null;
    private ToolRenderShell GetRenderShell() => toolDefinition?.RenderShell ?? ToolRenderShell.Default;

    private ToolRenderContext GetRenderContext(IComponent? lastComponent) => new()
    {
        Args = args,
        ToolCallId = toolCallId,
        ToolName = toolName,
        Invalidate = () =>
        {
            Invalidate();
            ui.RequestRender();
        },
        LastComponent = lastComponent,
        State = rendererState,
        Cwd = cwd,
        ExecutionStarted = executionStarted,
        ArgsComplete = argsComplete,
        IsPartial = isPartial,
        Expanded = expanded,
        ShowImages = showImages,
        IsError = result is not null && ToolJson.Truthy(result["isError"]),
        DurationMs = isPartial ? null : ToolJson.GetNumber(result, "durationMs"),
        OutputPad = outputPad,
    };

    private IComponent CreateCallFallback() => new Text(RenderUtils.FormatToolCallWithArgs(toolName, args, theme, expanded), 0, 0);

    private IComponent? CreateResultFallback()
    {
        var output = GetTextOutput();
        if (output.Length == 0) return null;

        var lines = output.Split('\n');
        var displayLines = expanded ? lines : lines.Take(FallbackPreviewLines).ToArray();
        var remaining = lines.Length - displayLines.Length;
        var text = string.Join("\n", displayLines.Select(line => theme.Fg("toolOutput", line)));
        if (remaining > 0)
            text += theme.Fg("muted", $"\n... ({remaining} more lines,") + " " + KeybindingHints.KeyHint("app.tools.expand", "to expand") + theme.Fg("muted", ")");
        return new Text(text, 0, 0);
    }

    private MouseRegion CreateResultRegion(IComponent component) => new(component, mouseEvent =>
    {
        if (result is null || mouseEvent.Type != TuiMouseEventType.Click || mouseEvent.Button != TuiMouseButton.Left) return null;
        SetExpanded(!expanded);
        return new TuiMouseEventResult(Handled: true);
    });

    public void UpdateArgs(JsonNode? args)
    {
        this.args = args;
        UpdateDisplay();
    }

    public void MarkExecutionStarted()
    {
        executionStarted = true;
        UpdateDisplay();
        ui.RequestRender();
    }

    public void SetArgsComplete()
    {
        argsComplete = true;
        UpdateDisplay();
        ui.RequestRender();
    }

    /// <summary>
    /// <paramref name="result"/> is {content: [{type, text?, data?, mimeType?}], details?, isError, durationMs?} (durationMs is the
    /// execution time of a final result).
    /// </summary>
    public void UpdateResult(JsonObject result, bool isPartial = false)
    {
        this.result = result;
        resultForRenderer = new JsonObject
        {
            ["content"] = result["content"]?.DeepClone() ?? new JsonArray(),
            ["details"] = result["details"]?.DeepClone(),
        };
        this.isPartial = isPartial;
        UpdateDisplay();
    }

    public void SetExpanded(bool expanded)
    {
        this.expanded = expanded;
        UpdateDisplay();
    }

    public void SetOutputPad(int outputPad)
    {
        this.outputPad = outputPad;
        UpdateDisplay();
    }

    public void SetShowImages(bool show)
    {
        showImages = show;
        UpdateDisplay();
    }

    public void SetImageWidthCells(double width)
    {
        imageWidthCells = Math.Max(1, (int)Math.Floor(width));
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    public override List<string> Render(int width)
    {
        if (hideComponent) return [];

        if (HasRendererDefinition() && GetRenderShell() == ToolRenderShell.Self)
        {
            var contentLines = selfRenderContainer.Render(width);
            selfRenderHeight = contentLines.Count;
            if (contentLines.Count == 0 && imageComponents.Count == 0) return [];

            var lines = new List<string>();
            if (contentLines.Count > 0)
            {
                lines.Add("");
                lines.AddRange(contentLines);
            }
            for (var i = 0; i < imageComponents.Count; i++)
            {
                if (i < imageSpacers.Count) lines.AddRange(imageSpacers[i].Render(width));
                lines.AddRange(imageComponents[i].Render(width));
            }
            return lines;
        }

        return base.Render(width);
    }

    public override TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        if (!HasRendererDefinition() || GetRenderShell() != ToolRenderShell.Self) return base.HandleMouse(mouseEvent);
        if (mouseEvent.Y <= 0 || mouseEvent.Y > selfRenderHeight) return null;
        return selfRenderContainer.HandleMouse(mouseEvent with { Y = mouseEvent.Y - 1, Height = selfRenderHeight });
    }

    private void UpdateDisplay()
    {
        Func<string, string> bgFn = isPartial
            ? text => theme.Bg("toolPendingBg", text)
            : result is not null && ToolJson.Truthy(result["isError"])
                ? text => theme.Bg("toolErrorBg", text)
                : text => theme.Bg("toolSuccessBg", text);

        var hasContent = false;
        hideComponent = false;
        if (HasRendererDefinition())
        {
            var self = GetRenderShell() == ToolRenderShell.Self;
            Action<IComponent> addChild = self ? selfRenderContainer.AddChild : contentBox.AddChild;
            if (!self)
            {
                contentBox.SetBgFn(bgFn);
                contentBox.SetPaddingX(outputPad);
                contentBox.Clear();
            }
            else selfRenderContainer.Clear();

            var callRenderer = GetCallRenderer();
            if (callRenderer is null)
            {
                addChild(CreateResultRegion(CreateCallFallback()));
                hasContent = true;
            }
            else
            {
                try
                {
                    var component = callRenderer(args, theme, GetRenderContext(callRendererComponent));
                    callRendererComponent = component;
                    addChild(CreateResultRegion(component));
                    hasContent = true;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    callRendererComponent = null;
                    addChild(CreateResultRegion(CreateCallFallback()));
                    hasContent = true;
                }
            }

            if (result is not null)
            {
                var resultRenderer = GetResultRenderer();
                if (resultRenderer is null)
                {
                    var component = CreateResultFallback();
                    if (component is not null)
                    {
                        addChild(CreateResultRegion(component));
                        hasContent = true;
                    }
                }
                else
                {
                    try
                    {
                        var component = resultRenderer(resultForRenderer!, new ToolRenderResultOptions(expanded, isPartial), theme,
                            GetRenderContext(resultRendererComponent));
                        resultRendererComponent = component;
                        addChild(CreateResultRegion(component));
                        hasContent = true;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        resultRendererComponent = null;
                        var component = CreateResultFallback();
                        if (component is not null)
                        {
                            addChild(CreateResultRegion(component));
                            hasContent = true;
                        }
                    }
                }
            }
        }
        else
        {
            contentText.SetCustomBgFn(bgFn);
            contentText.SetPaddingX(outputPad);
            contentText.SetText(FormatToolExecution());
            hasContent = true;
        }

        var previousImages = imageComponents;
        var previousSources = imageSources;
        foreach (var img in imageComponents) RemoveChild(img);
        imageComponents = [];
        imageSources = [];
        foreach (var spacer in imageSpacers) RemoveChild(spacer);
        imageSpacers = [];

        if (result is not null)
        {
            var imageBlocks = (result["content"] as JsonArray ?? []).Where(c => ToolJson.GetString(c, "type") == "image");
            var caps = TerminalImage.GetCapabilities();
            foreach (var img in imageBlocks)
            {
                var data = ToolJson.GetString(img, "data");
                var mimeType = ToolJson.GetString(img, "mimeType");
                if (caps.Images != ImageProtocol.None && showImages && !string.IsNullOrEmpty(data) && !string.IsNullOrEmpty(mimeType))
                {
                    var spacer = new Spacer(1);
                    AddChild(spacer);
                    imageSpacers.Add(spacer);
                    var source = (Data: data, MimeType: mimeType, WidthCells: imageWidthCells);
                    var index = imageComponents.Count;
                    if (mimeType != "image/png")
                    {
                        EnsurePngTranscoder(() =>
                        {
                            Invalidate();
                            ui.RequestRender();
                        });
                    }
                    var imageComponent = index < previousSources.Count && previousSources[index] == source
                        ? previousImages[index]
                        : new Image(source.Data, source.MimeType, new ImageTheme(s => theme.Fg("toolOutput", s)), new ImageOptions(MaxWidthCells: source.WidthCells));
                    imageComponents.Add(imageComponent);
                    imageSources.Add(source);
                    AddChild(imageComponent);
                }
            }
        }

        if (HasRendererDefinition() && !hasContent && imageComponents.Count == 0) hideComponent = true;
    }

    private string GetTextOutput() => RenderUtils.GetTextOutput(result, showImages);

    private string FormatToolExecution()
    {
        var text = theme.Fg("toolTitle", theme.Bold(toolName));
        if (args is not null)
        {
            var content = ToolJson.Stringify(args, 2);
            if (content.Length > 0) text += "\n\n" + content;
        }
        var output = GetTextOutput();
        if (output.Length > 0) text += "\n" + output;
        return text;
    }

    private static bool pngTranscoderRegistered;

    /// <summary>
    /// image-convert.ts ensurePngTranscoder: Kitty displays only PNG, so other formats are converted once a transcoder is
    /// registered. PiSharp's transcoder (Skia) loads synchronously, before the new image first renders, so
    /// <paramref name="onRegistered"/> is not needed to re-render it.
    /// </summary>
    private static void EnsurePngTranscoder(Action onRegistered)
    {
        _ = onRegistered;
        if (pngTranscoderRegistered || TerminalImage.GetCapabilities().Images != ImageProtocol.Kitty) return;
        Image.Transcoder ??= static (base64, mimeType) =>
        {
            try
            {
                var png = SkiaImageCodec.Instance.ConvertToPng(Convert.FromBase64String(base64), mimeType);
                return png is null ? null : Convert.ToBase64String(png);
            }
            catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException) { return null; }
        };
        pngTranscoderRegistered = true;
    }
}
