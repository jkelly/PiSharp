using System.Collections.Immutable;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.Extensions;

internal static class OriginalUiDialogBrokerTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("original-ui.four-dialog-arguments-timeout-and-exact-outcomes", Arguments),
        ("original-ui.held-actual-input-joins-full-multifault-and-faulted-oce", HeldFault),
        ("original-ui.actual-canceled-editor-and-nonvalue-no-default", Cancellation),
        ("original-ui.unsupported-signal-invalid-editor-options-refuse-before-effects", Refusal)
    ];
    private static void Check(bool value) { if (!value) throw new IOException("Original dialog source control failed."); }
    internal sealed record CapturedOriginal(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    internal sealed class ControlOriginalFailure(CapturedOriginal[] originals, Exception[] failures)
        : IOException("Dialog criteria and all actual original fault vectors.", new AggregateException(
            failures.Concat(originals.SelectMany(original => new Exception?[] { original.Aggregate, original.Direct }).OfType<Exception>())))
    {
        internal CapturedOriginal[] Originals { get; } = originals;
    }
    private sealed class Observation(string phase, Task original)
    {
        internal readonly string Phase = phase; internal readonly Task Original = original;
        internal AggregateException? Aggregate; internal Exception? Direct; internal bool AggregateCaptured, Joined;
        internal CapturedOriginal Snapshot() => new(Phase, Original, Aggregate, Direct);
    }
    private static void SeedCallbackAggregate(Observation callback, OriginalUiDialogOriginalException? carrier)
    {
        if (!callback.AggregateCaptured && carrier is { Evidence: AggregateException aggregate } && ReferenceEquals(carrier.Original, callback.Original))
        { callback.Aggregate = aggregate; callback.AggregateCaptured = true; }
    }
    private static async Task Observe(Observation record)
    {
        if (record.Joined) return;
        try { await record.Original; }
        catch (Exception error)
        {
            record.Direct ??= error;
            if (!record.AggregateCaptured)
            { record.Aggregate = record.Original.IsFaulted ? record.Original.Exception : null; record.AggregateCaptured = true; }
        }
        finally { record.Joined = true; }
    }
    private static void ThrowCriteriaFailure(List<Exception> failures, params Observation[] records)
    { if (failures.Count != 0) throw new ControlOriginalFailure(records.Select(record => record.Snapshot()).ToArray(), failures.ToArray()); }
    private static async Task Arguments()
    {
        var ui = new Ui();
        var input = OriginalUiDialogBroker.InvokeAsync("ui.input", JsonData.Parse("[\"title\",\"placeholder\",{\"timeout\":123.5}]"), true, ui, default);
        await input; Check(ui.Method == "input" && ui.Title == "title" && ui.Secondary == "placeholder" && ui.Options?.TimeoutMilliseconds == 123.5);
        Check(input.Result.Json!.Value.GetProperty("valueJson").GetString() == "\"actual callback value\"");
        var editor = OriginalUiDialogBroker.InvokeAsync("ui.editor", JsonData.Parse("[\"edit\",\"line1\\nline2\\uD800\"]"), false, ui, default);
        await editor; Check(ui.Method == "editor" && ui.Secondary == "line1\nline2\uD800" && ui.Options is null);
        var select = OriginalUiDialogBroker.InvokeAsync("ui.select", JsonData.Parse("[\"select\",[\"a\",\"b\"],{\"timeout\":0}]"), true, ui, default);
        await select; Check(ui.Method == "select" && ui.Choices.SequenceEqual(new[] { "a", "b" }) && ui.Options?.TimeoutMilliseconds == 0);
        var confirm = OriginalUiDialogBroker.InvokeAsync("ui.confirm", JsonData.Parse("[\"confirm\",\"message\"]"), false, ui, default);
        await confirm; Check(confirm.Result.Json!.Value.GetProperty("value").GetBoolean() && ui.Method == "confirm" && ui.Secondary == "message");
        var omitted = OriginalUiDialogBroker.InvokeAsync("ui.input", JsonData.Parse("[\"input\"]"), false, ui, default);
        await omitted; Check(ui.Secondary is null && ui.Options is null && ui.Calls == 5);
    }
    private static async Task HeldFault()
    {
        var held = new TaskCompletionSource<ExtensionUiOutcome<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Ui { Callback = () => new(held.Task) };
        var original = held.Task;
        var outer = OriginalUiDialogBroker.InvokeAsync("ui.input", JsonData.Parse("[\"held\"]"), false, ui, default);
        var shared = new IOException("shared input fault"); var oce = new OperationCanceledException("faulted callback OCE");
        var callbackRecord = new Observation("held-input-callback", original);
        var outerRecord = new Observation("held-input-broker", outer);
        OriginalUiDialogOriginalException? carrier = null; var failures = new List<Exception>(); var expectedGraphPassed = false;
        try
        {
            Check(ui.Calls == 1 && !outer.IsCompleted && !original.IsCompleted);
            held.SetException(new Exception[] { shared, new AggregateException(shared, oce) });
            await Observe(outerRecord); carrier = outerRecord.Direct as OriginalUiDialogOriginalException;
            SeedCallbackAggregate(callbackRecord, carrier);
            Check(carrier is not null && ReferenceEquals(carrier.Original, original) && ReferenceEquals(carrier.Direct, shared) &&
                carrier.Evidence is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 &&
                ReferenceEquals(aggregate.InnerExceptions[0], shared) && aggregate.InnerExceptions[1] is AggregateException nested &&
                ReferenceEquals(nested.InnerExceptions[0], shared) && ReferenceEquals(nested.InnerExceptions[1], oce) && outer.IsFaulted && !outer.IsCanceled);
            expectedGraphPassed = true;
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            held.TrySetResult(ExtensionUiOutcome<string>.Cancelled());
            // Join broker first to retain its first raw fault and obtain the first cached callback
            // aggregate. Independently join the actual callback even when prior criteria failed.
            await Observe(outerRecord); carrier ??= outerRecord.Direct as OriginalUiDialogOriginalException;
            SeedCallbackAggregate(callbackRecord, carrier); await Observe(callbackRecord);
            if (!expectedGraphPassed)
            { if (failures.Count == 0) failures.Add(new IOException("Held callback graph was not acknowledged.")); }
            else if (!ReferenceEquals(callbackRecord.Direct, carrier!.Direct) || !ReferenceEquals(outerRecord.Direct, carrier))
                failures.Add(new IOException("Held original direct fault identities changed during join."));
        }
        ThrowCriteriaFailure(failures, callbackRecord, outerRecord);
        var faultedOce = Task.FromException<ExtensionUiOutcome<string>>(oce); ui.Callback = () => new(faultedOce);
        var faulted = OriginalUiDialogBroker.InvokeAsync("ui.editor", JsonData.Parse("[\"faulted\"]"), false, ui, default);
        var oceCallbackRecord = new Observation("faulted-oce-editor-callback", faultedOce);
        var oceOuterRecord = new Observation("faulted-oce-editor-broker", faulted);
        OriginalUiDialogOriginalException? oceCarrier = null; var oceGraphPassed = false;
        try
        {
            await Observe(oceOuterRecord); oceCarrier = oceOuterRecord.Direct as OriginalUiDialogOriginalException;
            SeedCallbackAggregate(oceCallbackRecord, oceCarrier); await Observe(oceCallbackRecord);
            Check(faulted.IsFaulted && !faulted.IsCanceled && oceCarrier?.Original == faultedOce && ReferenceEquals(oceCarrier.Direct, oce) &&
                ReferenceEquals(oceCallbackRecord.Direct, oce) && oceCallbackRecord.Aggregate is { InnerExceptions.Count: 1 } raw &&
                ReferenceEquals(raw.InnerExceptions[0], oce));
            oceGraphPassed = true;
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            await Observe(oceOuterRecord); oceCarrier ??= oceOuterRecord.Direct as OriginalUiDialogOriginalException;
            SeedCallbackAggregate(oceCallbackRecord, oceCarrier); await Observe(oceCallbackRecord);
            if (!oceGraphPassed && failures.Count == 0) failures.Add(new IOException("Faulted OCE graph was not acknowledged."));
        }
        ThrowCriteriaFailure(failures, oceCallbackRecord, oceOuterRecord);
    }
    private static async Task Cancellation()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        var actual = Task.FromCanceled<ExtensionUiOutcome<string>>(source.Token);
        var ui = new Ui { Callback = () => new(actual) };
        var call = OriginalUiDialogBroker.InvokeAsync("ui.editor", JsonData.Parse("[\"cancel\"]"), false, ui, default);
        var failures = new List<Exception>();
        try
        {
            try { await call; throw new IOException("Canceled callback returned value."); }
            catch (OperationCanceledException error) { Check(error.CancellationToken == source.Token && call.IsCanceled && actual.IsCanceled); }
        }
        catch (Exception error) { failures.Add(call.Exception ?? error); failures.Add(error); }
        finally
        {
            try { await actual; }
            catch (OperationCanceledException error) when (actual.IsCanceled && error.CancellationToken == source.Token) { }
            catch (Exception error) { failures.Add(actual.Exception ?? error); failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Dialog cancellation originals.", failures);
        foreach (var outcome in new[] { ExtensionUiOutcome<string>.Cancelled(), ExtensionUiOutcome<string>.TimedOut(), ExtensionUiOutcome<string>.Unavailable(ExtensionUiUnavailableReason.Disconnected) })
        {
            ui.Callback = () => ValueTask.FromResult(outcome);
            var original = OriginalUiDialogBroker.InvokeAsync("ui.input", JsonData.Parse("[\"nonvalue\"]"), false, ui, default);
            await original; var value = original.Result.Json!.Value;
            Check(value.GetProperty("presence").GetString() == "undefined" && !value.TryGetProperty("value", out _));
        }
    }
    private static async Task Refusal()
    {
        var ui = new Ui();
        foreach (var tuple in new[] { ("ui.input", "[\"x\",null,{\"signal\":{}}]", true), ("ui.editor", "[\"x\",\"y\",{}]", true),
            ("ui.input", "[\"x\",null,{\"timeout\":\"wrong\"}]", true), ("ui.select", "[\"x\",false]", false) })
        {
            var original = OriginalUiDialogBroker.InvokeAsync(tuple.Item1, JsonData.Parse(tuple.Item2), tuple.Item3, ui, default);
            Exception? failure = null; try { await original; } catch (Exception error) { failure = error; }
            Check(failure is not null && original.IsFaulted && ui.Calls == 0);
        }
    }
    private sealed class Ui : IExtensionUi
    {
        internal int Calls; internal string? Method, Title, Secondary; internal ImmutableArray<string> Choices; internal ExtensionUiDialogOptions? Options;
        internal Func<ValueTask<ExtensionUiOutcome<string>>> Callback = () => ValueTask.FromResult(ExtensionUiOutcome<string>.FromValue("actual callback value"));
        public ExtensionUiCapabilities Capabilities => new(ExtensionUiMode.Tui, 1, 1, [ExtensionUiFeature.Input, ExtensionUiFeature.Editor, ExtensionUiFeature.Select, ExtensionUiFeature.Confirm]);
        private ValueTask<ExtensionUiOutcome<string>> Invoke(string method, string title, string? secondary, ExtensionUiDialogOptions? options)
        { Calls++; Method = method; Title = title; Secondary = secondary; Options = options; return Callback(); }
        public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Invoke("input", title, placeholder, options);
        public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default) => Invoke("editor", title, prefill, null);
        public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default)
        { Choices = choices; return Invoke("select", title, null, options); }
        public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; Method = "confirm"; Title = title; Secondary = message; Options = options; return ValueTask.FromResult(ExtensionUiOutcome<bool>.FromValue(true)); }
        public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
