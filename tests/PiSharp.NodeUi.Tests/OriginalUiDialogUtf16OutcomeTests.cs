using System.Collections.Immutable;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.ExtensionHost.Protocol;

internal static class OriginalUiDialogUtf16OutcomeTests
{
    internal sealed record CapturedOriginal(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly List<CapturedOriginal> retained = [];
    internal static CapturedOriginal[] RawCapturedOriginals { get { lock (retained) return retained.ToArray(); } }
    internal static async Task RunAsync()
    {
        var tasks = new List<(string Role, Task Original)>(); var failures = new List<Exception>();
        try
        {
            foreach (var (value, expected) in new[] { ("", "\"\""), ("\u00e9", "\"\\u00e9\""),
                (new string('\ud800', 65_536), "\"" + string.Concat(Enumerable.Repeat("\\ud800", 65_536)) + "\""), ("\ud800", "\"\\ud800\""),
                ("\udc00", "\"\\udc00\""), ("x\ud800\"\\\n\udc00\ud83d\ude00", "\"x\\ud800\\\"\\\\\\u000a\\udc00\\ud83d\\ude00\"") })
            {
                var ui = new Ui(value, tasks);
                var original = OriginalUiDialogBroker.InvokeAsync("ui.editor", JsonData.Parse("[\"actual editor\"]"), false, ui, CancellationToken.None);
                tasks.Add(("actual-broker-editor-" + tasks.Count, original)); var outcome = await original;
                if (outcome.Json!.Value.GetProperty("outcome").GetString() != "value" ||
                    outcome.Json.Value.GetProperty("presence").GetString() != "json" ||
                    outcome.Json.Value.GetProperty("valueJson").GetString() != expected ||
                    outcome.Json.Value.TryGetProperty("value", out _))
                    throw new IOException("Original editor result UTF16 code units changed.");
                var codec = new WorkerFrameCodec();
                var encoded = codec.Encode(new WorkerMessage(WorkerMessageKind.Response, 1, 1, 1, Value: outcome));
                var decoded = codec.Decode(encoded);
                var nested = decoded.Value!.Json!.Value.GetProperty("valueJson").GetString()!;
                if (nested != expected || nested.Length > 393_218 || nested.Any(unit => unit > 126) ||
                    decoded.Value!.Json!.Value.EnumerateObject().Count() != 3)
                    throw new IOException("Actual strict worker codec changed bounded ASCII dialog outcome.");
            }
            var strict = new WorkerFrameCodec();
            try
            {
                strict.Encode(new WorkerMessage(WorkerMessageKind.Response, 1, 1, 1,
                    Value: WorkerValue.FromJson(JsonData.Parse("{\"value\":\"\\ud800\"}"))));
                throw new IOException("The shared worker codec unexpectedly accepted direct lone surrogate JSON.");
            }
            catch (WorkerProtocolException error) when (error.Failure == WorkerProtocolFailure.InvalidUnicode) { }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            foreach (var (role, original) in tasks)
            {
                AggregateException? aggregate = null; Exception? direct = null;
                try { await original; } catch (Exception error) { direct = error; if (original.IsFaulted) aggregate = original.Exception; }
                lock (retained) retained.Add(new(role, original, aggregate, direct));
                if (direct is not null) failures.Add(aggregate ?? direct);
            }
        }
        if (failures.Count != 0) throw new AggregateException("UTF16 result criteria and original inventory.", failures);
    }
    private sealed class Ui(string value, List<(string Role, Task Original)> tasks) : IExtensionUi
    {
        public ExtensionUiCapabilities Capabilities => new(ExtensionUiMode.Rpc, 1, 1, [ExtensionUiFeature.Editor]);
        public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default)
        { if (title != "actual editor" || prefill is not null) throw new IOException("Actual editor arguments differ."); var original = Task.FromResult(ExtensionUiOutcome<string>.FromValue(value)); tasks.Add(("actual-native-editor-" + tasks.Count, original)); return new(original); }
        public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
