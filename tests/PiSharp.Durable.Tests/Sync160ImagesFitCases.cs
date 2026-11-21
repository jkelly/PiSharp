// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/images-fit.test.ts ("fitImage", "fitImages", "the fit-images
// extension"), as sync160.images-fit.<case>: images fitted to the model's limits as they enter a conversation, before they are stored
// (src/images/fit.ts, here PiSharp.Durable.Tools/Images/Fit.cs). Verbatim apart from the adaptations in HarnessSupport.cs/ChatSupport.cs,
// and these:
// - test/image-fixtures.ts, which v1.6.0 split out of tools-images.test.ts, is the fixture code of ToolsImagesCases.cs (EncodeImage,
//   NoisyPng, Bmp, WithOrientation, DecodedImage, ...), private members of this partial class; Photon is PiSharp.Tools.Skia's
//   SkiaImageCodec there and here, and the processor is ImageProcessors.CreateThreaded(SkiaImageCodec.Instance) where upstream's is
//   createNodePhotonImages().
// - fitImage, fitImages and createFitImagesExtension are FitImages.FitImageAsync, FitAsync and CreateExtension; a result's `kind` is its
//   record type, and content is wire JSON.
// - `chatSetup({ models: [{ id, input, inputLimits }] })` is FauxModelOptions with InputLimits (the faux models take text and images).
// - "leaves nested calls' results ...": upstream passes `{} as HookApi`; here the api is null, which a hook that touched it would fail on.
// - "leaves out what cannot be prepared ...": the malformed base64 "%%%" is refused by ImageFormats.FromBase64 as atob refuses it.
// Authored (no upstream case): from-base64-decodes-as-atob-does pins the forgiving decode the port's FromBase64 adds over
// Convert.FromBase64String; image-limits-of-a-model-default-each-absent-limit pins imageLimits(model); the-extension-leaves-a-result-
// without-images-alone covers the hook's early return for a top-level call.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Images;
using static ChatTests;
using static HarnessTests;
using static SessionTests;

internal static partial class Program
{
    /// <summary>The processor, with the type of every <c>PrepareAsync</c> call recorded (upstream <c>recorded</c>).</summary>
    private sealed class RecordingProcessor(IImageProcessor inner) : IImageProcessor
    {
        public SyncList<string> Calls { get; } = new();

        public Task<PreparedImage?> PrepareAsync(ReadOnlyMemory<byte> bytes, string mimeType, ImageLimits limits)
        {
            Calls.Add(mimeType);
            return inner.PrepareAsync(bytes, mimeType, limits);
        }
    }

    private sealed class FailingProcessor : IImageProcessor
    {
        public Task<PreparedImage?> PrepareAsync(ReadOnlyMemory<byte> bytes, string mimeType, ImageLimits limits) =>
            Task.FromException<PreparedImage?>(new InvalidOperationException("worker exited"));
    }

    private static JsonObjectValue ImageItem(byte[] bytes, string mimeType) =>
        SessionTests.Obj(("type", "image"), ("data", ImageFormats.ToBase64(bytes)), ("mimeType", mimeType));

    private static JsonObjectValue TextItem(string text) => SessionTests.Obj(("type", "text"), ("text", text));

    /// <summary>The single image <c>FitAsync</c> made of <paramref name="item"/>, with its notes (upstream <c>fitOne</c>).</summary>
    private static async Task<(JsonObjectValue? Image, ImmutableArray<string> Notes)> FitOne(JsonObjectValue item, ImageLimits? limits = null)
    {
        if (await FitImages.FitAsync([item], limits ?? ImageLimits.Default, Processor) is not FittedImages.Fitted fitted)
            throw new InvalidOperationException("the image was not changed");
        return (fitted.Content.Count == 0 ? null : (JsonObjectValue)fitted.Content[0], fitted.Notes);
    }

    private static IEnumerable<(string, Func<Task>)> Sync160ImagesFitCases()
    {
        const string prefix = "sync160.images-fit.";
        var context = DurableContext.Background;

        // ─── fitImage ───────────────────────────────────────────────────────

        yield return Case(prefix + "fit-image.prepares-an-image-from-its-bytes-as-base64-for-the-model", async () =>
        {
            var png = EncodeImage(8, 6, "png");
            var fitted = await FitImages.FitImageAsync(png, "image/png", ImageLimits.Default, Processor) as FittedImage.Fitted;
            Check(fitted is not null, "fitted");
            SameJson(ImageItem(png, "image/png").ToJson(), fitted!.Image, "image");
            Equal(0, fitted.Notes.Length, "notes");
        });

        yield return Case(prefix + "fit-image.leaves-out-an-image-the-processor-fails-on-with-the-failure-in-its-note", async () =>
        {
            var omitted = await FitImages.FitImageAsync(EncodeImage(8, 6, "png"), "image/png", ImageLimits.Default, new FailingProcessor()) as FittedImage.Omitted;
            Equal("An image (image/png) cannot be prepared for the model and was left out: worker exited.", omitted?.Note, "note");
        });

        // ─── fitImages ──────────────────────────────────────────────────────

        yield return Case(prefix + "fit-images.is-unchanged-when-every-image-comes-back-as-it-was-and-asks-the-processor-only-about-images", async () =>
        {
            var recorded = new RecordingProcessor(Processor);
            JsonValue[] content = [TextItem("a"), ImageItem(EncodeImage(8, 6, "png"), "image/png")];
            Check(await FitImages.FitAsync(content, ImageLimits.Default, recorded) is FittedImages.Unchanged, "unchanged");
            Equal("image/png", string.Join(",", recorded.Calls.Snapshot()), "calls");
            Check(await FitImages.FitAsync([TextItem("only text")], ImageLimits.Default, recorded) is FittedImages.Unchanged, "text only");
            Equal("image/png", string.Join(",", recorded.Calls.Snapshot()), "calls after text");
        });

        yield return Case(prefix + "fit-images.scales-an-image-beyond-the-limits-down-keeps-the-text-in-place-and-says-how-to-map-coordinates-back", async () =>
        {
            var text = TextItem("before");
            var fitted = await FitImages.FitAsync([text, ImageItem(EncodeImage(400, 100, "png"), "image/png"), TextItem("after")],
                ImageLimits.Default with { MaxWidth = 200 }, Processor) as FittedImages.Fitted;
            Check(fitted is not null, "fitted");
            Equal("text,image,text", string.Join(",", fitted!.Content.Select(item => S(((JsonObjectValue)item)["type"]))), "types");
            Check(ReferenceEquals(text, fitted.Content[0]), "the text item is the same instance");
            var decoded = DecodedImage(S(((JsonObjectValue)fitted.Content[1])["data"]));
            Equal((200, 50), (decoded.Width, decoded.Height), "size");
            Equal("Resized from 400x100 to 200x50. Multiply coordinates by 2.00 to map them to the original.", string.Join("|", fitted.Notes), "notes");
        });

        yield return Case(prefix + "fit-images.re-encodes-an-image-within-the-dimensions-but-over-the-byte-limit", async () =>
        {
            var limits = ImageLimits.Default with { MaxBytes = 8_000 };
            var (fitted, notes) = await FitOne(ImageItem(NoisyPng(64, 64), "image/png"), limits);
            Check(S(fitted!["data"]).Length <= limits.MaxBytes, "within the byte limit");
            Check(notes.Length > 0, "notes");
        });

        yield return Case(prefix + "fit-images.turns-an-image-upright-that-fits-but-carries-an-exif-orientation", async () =>
        {
            var (fitted, _) = await FitOne(ImageItem(WithOrientation(EncodeImage(40, 30, "jpeg"), 6), "image/jpeg"));
            var decoded = DecodedImage(S(fitted!["data"]));
            Equal((30, 40), (decoded.Width, decoded.Height), "size");
        });

        yield return Case(prefix + "fit-images.converts-a-format-providers-do-not-take-inline-to-png", async () =>
        {
            var (fitted, notes) = await FitOne(ImageItem(Bmp(4, 4), "image/bmp"));
            Equal("image/png", S(fitted!["mimeType"]), "mimeType");
            Equal("Converted from image/bmp to image/png.", string.Join("|", notes), "notes");
        });

        yield return Case(prefix + "fit-images.corrects-a-declared-type-the-bytes-contradict-without-a-note", async () =>
        {
            var jpeg = EncodeImage(8, 6, "jpeg");
            var (fitted, notes) = await FitOne(ImageItem(jpeg, "image/png"));
            SameJson(ImageItem(jpeg, "image/jpeg").ToJson(), fitted, "image");
            Equal(0, notes.Length, "notes");
        });

        yield return Case(prefix + "fit-images.leaves-out-what-cannot-be-prepared-with-a-note-damaged-images-other-bytes-malformed-base64", async () =>
        {
            const string note = "An image (image/png) cannot be prepared for the model and was left out.";
            var damaged = EncodeImage(4, 4, "png")[..40];
            var fitted = await FitImages.FitAsync([TextItem("a"), ImageItem(damaged, "image/png")], ImageLimits.Default, Processor) as FittedImages.Fitted;
            Check(fitted is not null, "fitted");
            SameJson("""[{"type":"text","text":"a"}]""", fitted!.Content, "content");
            Equal(note, string.Join("|", fitted.Notes), "notes");
            var text = System.Text.Encoding.UTF8.GetBytes("not an image at all, though it says so");
            var other = await FitOne(ImageItem(text, "image/png"));
            Check(other.Image is null, "other bytes are left out");
            Equal(note, string.Join("|", other.Notes), "other bytes' note");
            var malformed = await FitOne(SessionTests.Obj(("type", "image"), ("data", "%%%"), ("mimeType", "image/png")));
            Check(malformed.Image is null, "malformed base64 is left out");
            Check(malformed.Notes[0].StartsWith("An image (image/png) cannot be prepared for the model and was left out: ", StringComparison.Ordinal),
                "malformed note: " + malformed.Notes[0]);
        });

        // ─── the fit-images extension ───────────────────────────────────────

        yield return Case(prefix + "extension.fits-a-tool-result-s-images-to-the-conversation-model-s-limits-before-it-is-committed", async () =>
        {
            var setup = Setup(new FauxOptions
            {
                Models =
                [
                    new FauxModelOptions("faux-1")
                    {
                        InputLimits = new DurableModelInputLimits
                        {
                            Images = new DurableModelImageInputLimits { Resize = new DurableModelImageResizeOptions { MaxWidth = 100 } },
                        },
                    },
                ],
            });
            setup.Registry.Install(FitImages.CreateExtension(Processor));
            var small = ImageItem(EncodeImage(8, 6, "png"), "image/png");
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "picture",
                Description = "A picture",
                Execute = (_, api, _) => Task.FromResult(api.CallId == "small"
                    ? new ToolExecutionResult { Output = JsonValue.Array(small) }
                    : new ToolExecutionResult
                    {
                        Output = JsonValue.Array(TextItem("here"), ImageItem(EncodeImage(400, 100, "png"), "image/png")),
                        Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Info, "the tool's own")],
                    }),
            }));
            setup.Faux.SetResponses(
                FauxAssistantMessage([FauxToolCall("picture", JsonObjectValue.Empty, "wide"), FauxToolCall("picture", JsonObjectValue.Empty, "small")],
                    stopReason: "toolUse"),
                FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            harness.Resume();
            await (await root.SubmitAsync(SubmissionDraft.Input("show"), context)).WaitAsync(context);
            var stored = (await AllEntries(root)).Where(entry => Entries.ToolResultEntry.Is(entry)).Select(entry => (JsonObjectValue)entry.Model![0])
                .ToDictionary(message => S(message["toolCallId"]), message => (JsonArrayValue)message["content"]);
            var wide = stored["wide"];
            SameJson("""{"type":"text","text":"here"}""", wide[0], "the text stays first");
            var decoded = DecodedImage(S(((JsonObjectValue)wide[1])["data"]));
            Equal((100, 25), (decoded.Width, decoded.Height), "the stored image's size");
            SameJson(SessionTests.Obj(("type", "text"), ("text",
                    "<harness>\n[info] the tool's own\n[info] Resized from 400x100 to 100x25. Multiply coordinates by 4.00 to map them to the original.\n</harness>"))
                .ToJson(), wide[2], "diagnostics");
            // An image that fits is stored as the tool made it, without a note.
            SameJson(JsonValue.Array(small).ToJson(), stored["small"], "small");
            await harness.CloseAsync(context);
        });

        yield return Case(prefix + "extension.leaves-nested-calls-results-to-the-program-that-made-them", async () =>
        {
            var registration = FitImages.CreateExtension(Processor).Hooks![0];
            Equal(HarnessTasks.ToolTask.Definition.Name, registration.Task, "the hook's task");
            // The handlers of a ToolTask hook registration.
            var afterTool = ((ToolHooks)registration.Handlers).AfterTool!;
            var result = new ToolExecutionResult { Output = JsonValue.Array(ImageItem(EncodeImage(4000, 10, "png"), "image/png")) };
            var call = O("""{"type":"toolCall","id":"n","name":"picture","arguments":{},"parent":{"taskId":1,"callId":"c"}}""");
            // A nested result is not looked at: the hook API, absent here, is never touched.
            Check(await afterTool(call, result, null!, context) is null, "a nested result is left alone");
        });

        // ─── Authored ───────────────────────────────────────────────────────

        yield return Case(prefix + "extension.leaves-a-result-without-images-alone", async () =>
        {
            var afterTool = ((ToolHooks)FitImages.CreateExtension(new FailingProcessor()).Hooks![0].Handlers).AfterTool!;
            var call = O("""{"type":"toolCall","id":"n","name":"picture","arguments":{}}""");
            // Neither the hook API nor the processor is touched for a result with no image, and for one with no output.
            Check(await afterTool(call, new ToolExecutionResult { Output = JsonValue.Array(TextItem("text")) }, null!, context) is null, "text only");
            Check(await afterTool(call, new ToolExecutionResult(), null!, context) is null, "no output");
        });

        yield return Case(prefix + "from-base64-decodes-as-atob-does", () =>
        {
            Equal("hi", System.Text.Encoding.ASCII.GetString(ImageFormats.FromBase64("aGk=")), "padded");
            // atob takes unpadded data and skips ASCII whitespace; Convert.FromBase64String alone refuses the first.
            Equal("hi", System.Text.Encoding.ASCII.GetString(ImageFormats.FromBase64("aGk")), "unpadded");
            Equal("hi", System.Text.Encoding.ASCII.GetString(ImageFormats.FromBase64(" a\nG\tk=\r\n")), "whitespace");
            Equal(0, ImageFormats.FromBase64("").Length, "empty");
            foreach (var malformed in new[] { "%%%", "a", "aGk=a", "a=Gk", "aGk==", "aGék" })
                Equal("The string to be decoded is not correctly encoded.",
                    Throws<FormatException>(() => ImageFormats.FromBase64(malformed), malformed).Message, malformed);
            var bytes = Enumerable.Range(0, 1000).Select(index => (byte)(index * 7)).ToArray();
            Check(bytes.AsSpan().SequenceEqual(ImageFormats.FromBase64(ImageFormats.ToBase64(bytes))), "round trip");
        });

        yield return Case(prefix + "image-limits-of-a-model-default-each-absent-limit", () =>
        {
            Equal(ImageLimits.Default, CodingTools.ImageLimitsOf(null), "no model");
            Equal(ImageLimits.Default, CodingTools.ImageLimitsOf(ImageTestModel(null, null)), "a model without limits");
            Equal(ImageLimits.Default with { MaxWidth = 100 },
                CodingTools.ImageLimitsOf(ImageTestModel(null, new DurableModelImageResizeOptions { MaxWidth = 100 })), "one limit");
            Equal(new ImageLimits(10, 20, 30),
                CodingTools.ImageLimitsOf(ImageTestModel(null, new DurableModelImageResizeOptions { MaxWidth = 10, MaxHeight = 20, MaxBytes = 30 })), "all");
        });
    }
}
