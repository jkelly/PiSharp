// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/src/images/fit.ts, the `@earendil-works/pi-durable/images/fit` entry
// point: images entering a conversation, fitted to its model's limits by any IImageProcessor before they are stored, so the transcript (and
// the provider's prompt cache) never changes later: a host's input before it is submitted (FitImages.FitImageAsync, FitImages.FitAsync), and
// the results of the model's tool calls (FitImages.CreateExtension). No codec is loaded from here; pass a processor (Images.cs builds one
// over PiSharp.Tools' image codec seam, the one the coding agent's read tool resizes with, such as PiSharp.Tools.Skia's SkiaImageCodec).
//
// Mappings:
// - fitImage, fitImages and createFitImagesExtension are FitImages.FitImageAsync, FitAsync and CreateExtension; FittedImage and FittedImages
//   are closed record hierarchies (`kind: "fitted" | "omitted"` is FittedImage.Fitted or .Omitted; `"unchanged" | "fitted"` is
//   FittedImages.Unchanged or .Fitted).
// - Content is pi-ai wire JSON, as everywhere in the durable port (SubmissionDraft.Input's content, ToolExecutionResult.Output): an image is
//   an object whose `type` is "image", with `data` and `mimeType`; every other item stays where it is, the same instance. An image block
//   whose `data` is not a string is left out as malformed base64 is (upstream's `atob(undefined)` throws too), and one without a string
//   `mimeType` is named "unknown" in the note.
// - `errorMessage(error)` is DurableErrors.MessageOf. A processor's OutOfMemoryException is not caught, as in Images.cs.
// - The hook reads the conversation's model from AgentDoc through the hook api's snapshot, as upstream does.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Tools.Images;

/// <summary>One image fitted by <see cref="FitImages.FitImageAsync"/>, with notes for the model saying what changed; or why it was left out
/// (upstream <c>FittedImage</c>).</summary>
public abstract record FittedImage
{
    private FittedImage() { }

    /// <summary>The image as the model can take it: <c>{ type: "image", data, mimeType }</c>.</summary>
    public sealed record Fitted(JsonObjectValue Image, ImmutableArray<string> Notes) : FittedImage;

    /// <summary>The image cannot be prepared; <paramref name="Note"/> says so for the model.</summary>
    public sealed record Omitted(string Note) : FittedImage;
}

/// <summary>What <see cref="FitImages.FitAsync"/> did: nothing, or the content with its images fitted and notes for the model (upstream
/// <c>FittedImages</c>).</summary>
public abstract record FittedImages
{
    private FittedImages() { }

    /// <summary>Every image came back as it was.</summary>
    public sealed record Unchanged : FittedImages;

    /// <summary>The content with its images fitted (or left out), and what changed.</summary>
    public sealed record Fitted(JsonArrayValue Content, ImmutableArray<string> Notes) : FittedImages;
}

/// <summary>Upstream <c>images/fit.ts</c>; see the file header.</summary>
public static class FitImages
{
    /// <summary>The name of the extension <see cref="CreateExtension"/> returns.</summary>
    public const string ExtensionName = "fit-images";

    private static readonly FittedImages UnchangedResult = new FittedImages.Unchanged();

    /// <summary>
    /// The image in <paramref name="bytes"/>, declared as <paramref name="mimeType"/>, prepared by <paramref name="processor"/> to fit
    /// <paramref name="limits"/> (upstream <c>fitImage</c>). Every image is decoded, as the coding agent's are: one the provider would reject
    /// (damaged, or too large) never reaches the transcript, where it would fail every later request. Its type is read from its bytes, since
    /// providers reject a declared type that differs. An image the processor cannot prepare, or fails on, is left out with a note.
    /// </summary>
    public static async Task<FittedImage> FitImageAsync(ReadOnlyMemory<byte> bytes, string mimeType, ImageLimits limits, IImageProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(processor);
        PreparedImage? prepared;
        try
        {
            prepared = await processor.PrepareAsync(bytes, ImageDetection.DetectSupportedImageMimeType(bytes.Span) ?? mimeType, limits).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The image is known to need preparing: keeping it would commit what the provider rejects. The note carries the failure into the
            // transcript.
            return Omitted(mimeType, $": {DurableErrors.MessageOf(error)}");
        }
        if (prepared is null) return Omitted(mimeType, "");
        return new FittedImage.Fitted(ImageBlock(prepared.Data, prepared.MimeType), [.. ImageFormats.PreparedNotes(prepared)]);
    }

    private static FittedImage.Omitted Omitted(string mimeType, string reason) =>
        new($"An image ({mimeType}) cannot be prepared for the model and was left out{reason}.");

    private static JsonObjectValue ImageBlock(string data, string mimeType) =>
        JsonValue.Object([new("type", "image"), new("data", data), new("mimeType", mimeType)]);

    /// <summary>
    /// <paramref name="content"/> with each image fitted by <see cref="FitImageAsync"/> (malformed base64 is left out as an image that cannot
    /// be prepared); text stays where it is (upstream <c>fitImages</c>). <see cref="FittedImages.Unchanged"/> when every image came back as it
    /// was.
    /// </summary>
    public static async Task<FittedImages> FitAsync(IReadOnlyList<JsonValue> content, ImageLimits limits, IImageProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(processor);
        var fitted = ImmutableArray.CreateBuilder<JsonValue>(content.Count);
        var notes = ImmutableArray.CreateBuilder<string>();
        var changed = false;
        foreach (var item in content)
        {
            if (!IsImage(item))
            {
                fitted.Add(item);
                continue;
            }
            var image = (JsonObjectValue)item;
            var data = (image.TryGetValue("data", out var dataValue) ? dataValue as JsonStringValue : null)?.Value;
            var mimeType = (image.TryGetValue("mimeType", out var typeValue) ? typeValue as JsonStringValue : null)?.Value;
            var result = await FitBase64Async(data, mimeType ?? "unknown", limits, processor).ConfigureAwait(false);
            if (result is FittedImage.Omitted omitted)
            {
                changed = true;
                notes.Add(omitted.Note);
                continue;
            }
            var made = (FittedImage.Fitted)result;
            if (S(made.Image, "data") == data && S(made.Image, "mimeType") == mimeType)
            {
                fitted.Add(item);
            }
            else
            {
                changed = true;
                fitted.Add(made.Image);
                notes.AddRange(made.Notes);
            }
        }
        return changed ? new FittedImages.Fitted(JsonValue.Array(fitted.ToImmutable()), notes.ToImmutable()) : UnchangedResult;
    }

    /// <summary>Whether a content item is an image block: an object whose <c>type</c> is <c>image</c>.</summary>
    internal static bool IsImage(JsonValue item) =>
        item is JsonObjectValue block && block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "image" };

    private static string S(JsonObjectValue block, string key) => ((JsonStringValue)block[key]).Value;

    private static Task<FittedImage> FitBase64Async(string? data, string mimeType, ImageLimits limits, IImageProcessor processor)
    {
        byte[] bytes;
        try
        {
            bytes = ImageFormats.FromBase64(data ?? throw new FormatException("The string to be decoded is not correctly encoded."));
        }
        catch (FormatException error)
        {
            // Wire input: a tool's base64 may be malformed.
            return Task.FromResult<FittedImage>(Omitted(mimeType, $": {DurableErrors.MessageOf(error)}"));
        }
        return FitImageAsync(bytes, mimeType, limits, processor);
    }

    /// <summary>
    /// The <c>fit-images</c> extension (upstream <c>createFitImagesExtension</c>): an <c>afterTool</c> hook that fits the images of every
    /// result the model gets to the conversation model's current limits (<see cref="FitAsync"/>) before it is committed, whichever tool made
    /// them (an MCP tool, code mode's <c>image()</c>), and says what changed in an <c>info</c> diagnostic (code <c>image</c>). Hooks of
    /// extensions installed before it see the images unfitted. Nested calls' results are left as they are: the program that made the call
    /// gets them, not the model.
    /// </summary>
    public static Extension CreateExtension(IImageProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return Define.Extension(new Extension
        {
            Name = ExtensionName,
            Hooks =
            [
                Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    AfterTool = (call, result, api, context) => FitResultAsync(call, result, processor, api, context),
                }),
            ],
        });
    }

    private static async Task<ToolExecutionResult?> FitResultAsync(JsonObjectValue call, ToolExecutionResult result, IImageProcessor processor,
        IHookApi api, DurableContext context)
    {
        if (call.TryGetValue("parent", out _) || result.Output is not { } output || !output.Any(IsImage)) return null;
        var agent = await api.SnapshotAsync(Agents.AgentDoc, api.ConversationId, context).ConfigureAwait(false);
        var reference = agent is not null && agent.TryGetValue("model", out var modelJson) ? ModelRef.FromJson(modelJson) : null;
        var model = reference is null ? null : api.Models.GetModel(reference.Provider, reference.ModelId);
        if (await FitAsync(output, ImageLimits.Of(model), processor).ConfigureAwait(false) is not FittedImages.Fitted fitted) return null;
        if (fitted.Notes.Length == 0) return result with { Output = fitted.Content };
        var note = new ToolDiagnostic(ToolDiagnosticSeverity.Info, string.Join(" ", fitted.Notes), "image");
        return result with { Output = fitted.Content, Diagnostics = [.. result.Diagnostics ?? [], note] };
    }
}
