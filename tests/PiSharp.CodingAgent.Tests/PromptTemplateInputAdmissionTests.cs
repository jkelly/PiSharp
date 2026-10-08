using PiSharp.Agent;
using PiSharp.CodingAgent.Resources;
using PiSharp.Contracts;

internal static class PromptTemplateInputAdmissionTests
{
    public const string Prefix = "prompt template admission ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "normal command precedes raw handlers and template", CommandFirst),
        (Prefix + "raw handler transform expands once without command redispatch", HandlerThenTemplate),
        (Prefix + "explicit queue rejects commands without executing or reducing", QueueCommands),
        (Prefix + "steer and follow-up expand after handlers preserving mode", QueueTemplates),
        (Prefix + "handled input stops template and image transformations", Handled),
        (Prefix + "continue null-image and changed-image decisions retain semantics", Images),
        (Prefix + "extension defaults off and explicit opt-in dispatches first", ExtensionPolicy),
        (Prefix + "normal opt-out still runs input handlers", Disabled),
        (Prefix + "existing native bounds validate expanded and transformed values", Bounds),
        (Prefix + "pre and pending handler cancellation join original operation", HandlerCancellation),
        (Prefix + "pending command cancellation joins original command before returning", CommandCancellation),
        (Prefix + "original handler and command failures propagate unchanged", Failures)
    ];

    private static async Task CommandFirst()
    {
        var commands = new Commands(_ => true); var handlers = new Handler(_ => throw new Exception("Command reached input handlers"));
        var admission = new PromptTemplateInputAdmission(Catalog("template $1"), PromptTemplateInputOperation.Prompt, handlers, commands);
        Equal(PromptInputAction.Handled, (await admission.ReduceAsync(new("/review raw"), default)).Action);
        Equal(1, commands.Executed); Equal(0, commands.Lookups); Equal(0, handlers.Calls);
    }

    private static async Task HandlerThenTemplate()
    {
        var image = Image("AA=="); var commands = new Commands(_ => false);
        var handlers = new Handler(input => { Equal("/review raw", input.Text); return new(PromptInputAction.Transform, "/review '$1 literal'", image); });
        var admission = new PromptTemplateInputAdmission(Catalog("Expanded $1"), PromptTemplateInputOperation.Prompt, handlers, commands);
        var decision = await admission.ReduceAsync(new("/review raw"), default);
        Equal("Expanded $1 literal", decision.Text); Equal(image.ToString(), decision.Images!.ToString());
        Equal(1, commands.Executed); Equal("/review raw", commands.LastText); Equal(1, handlers.Calls);
    }

    private static async Task QueueCommands()
    {
        foreach (var operation in new[] { PromptTemplateInputOperation.Steer, PromptTemplateInputOperation.FollowUp })
        {
            var commands = new Commands(_ => true); var handlers = new Handler(_ => throw new Exception("Queue command reached handler"));
            var admission = new PromptTemplateInputAdmission(Catalog("wrong"), operation, handlers, commands);
            try { await admission.ReduceAsync(new("/review raw", PromptInputSource.Rpc), default); }
            catch (PromptInputAdmissionException error) when (error.Failure == PromptInputAdmissionFailure.InvalidInput)
            { Equal(0, commands.Executed); Equal(1, commands.Lookups); Equal(0, handlers.Calls); continue; }
            throw new InvalidOperationException("Explicit queue accepted an extension command.");
        }
    }

    private static async Task QueueTemplates()
    {
        foreach (var operation in new[] { PromptTemplateInputOperation.Steer, PromptTemplateInputOperation.FollowUp })
        {
            var mode = operation == PromptTemplateInputOperation.Steer ? PromptInputStreamingBehavior.Steer : PromptInputStreamingBehavior.FollowUp;
            var commands = new Commands(_ => false);
            var handlers = new Handler(input => { Equal((PromptInputStreamingBehavior?)mode, input.StreamingBehavior); return new(PromptInputAction.Transform, "/review changed"); });
            var admission = new PromptTemplateInputAdmission(Catalog("$1"), operation, handlers, commands);
            Equal("changed", (await admission.ReduceAsync(new("/review raw", PromptInputSource.Rpc, StreamingBehavior: mode), default)).Text);
            Equal(1, commands.Lookups); Equal(0, commands.Executed);
        }
    }

    private static async Task Handled()
    {
        var handled = new PromptInputDecision(PromptInputAction.Handled, "ignored", JsonData.Parse("null"));
        var admission = new PromptTemplateInputAdmission(Catalog("not submitted"), PromptTemplateInputOperation.Prompt, new Handler(_ => handled));
        var actual = await admission.ReduceAsync(new("/review raw", Images: Image("AA==")), default);
        Equal(true, ReferenceEquals(handled, actual));
    }

    private static async Task Images()
    {
        var original = Image("AA=="); var replacement = Image("AQ==");
        foreach (var returned in new JsonData?[] { null, JsonData.Parse("null"), replacement })
        {
            var handler = new Handler(_ => new(PromptInputAction.Transform, "/review updated", returned));
            var admission = new PromptTemplateInputAdmission(Catalog("$1"), PromptTemplateInputOperation.Prompt, handler);
            var decision = await admission.ReduceAsync(new("/review raw", Images: original), default);
            Equal((ReferenceEquals(returned, replacement) ? replacement : original).ToString(), decision.Images!.ToString());
        }
        var continuing = new PromptTemplateInputAdmission(Catalog("$1"), PromptTemplateInputOperation.Prompt,
            new Handler(_ => new(PromptInputAction.Continue)));
        Equal(original.ToString(), (await continuing.ReduceAsync(new("/review raw", Images: original), default)).Images!.ToString());
    }

    private static async Task ExtensionPolicy()
    {
        var commands = new Commands(_ => false); var handlers = new Handler(_ => new(PromptInputAction.Continue));
        var admission = new PromptTemplateInputAdmission(Catalog("$1"), PromptTemplateInputOperation.ExtensionMessage, handlers, commands);
        Equal(PromptInputAction.Continue, (await admission.ReduceAsync(new("/review raw", PromptInputSource.Extension), default)).Action);
        Equal(0, commands.Executed); Equal(1, handlers.Calls);
        admission = new(Catalog("$1"), PromptTemplateInputOperation.ExtensionMessage, handlers, commands, expandTemplates: true);
        Equal("raw", (await admission.ReduceAsync(new("/review raw", PromptInputSource.Extension), default)).Text);
        Equal(1, commands.Executed); Equal(2, handlers.Calls);
    }

    private static async Task Disabled()
    {
        var commands = new Commands(_ => true);
        var admission = new PromptTemplateInputAdmission(Catalog("wrong"), PromptTemplateInputOperation.Prompt,
            new Handler(_ => new(PromptInputAction.Transform, "/review changed")), commands, expandTemplates: false);
        Equal("/review changed", (await admission.ReduceAsync(new("/review raw"), default)).Text); Equal(0, commands.Executed);
    }

    private static async Task Bounds()
    {
        var admission = new PromptTemplateInputAdmission(Catalog(new string('x', 30)), PromptTemplateInputOperation.Prompt,
            limits: new(MaximumTextCharacters: 20));
        var bounded = false;
        try { await admission.ReduceAsync(new("/review"), default); }
        catch (PromptInputAdmissionException error) when (error.Failure == PromptInputAdmissionFailure.ResourceLimit) { bounded = true; }
        Equal(true, bounded);
        var invalid = new PromptTemplateInputAdmission(Catalog("$1"), PromptTemplateInputOperation.Prompt,
            new Handler(_ => new(PromptInputAction.Transform, "\uD800")));
        try { await invalid.ReduceAsync(new("raw"), default); }
        catch (PromptInputAdmissionException error) when (error.Failure == PromptInputAdmissionFailure.InvalidInput) { return; }
        throw new InvalidOperationException("Invalid transformed scalar was accepted.");
    }

    private static async Task HandlerCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var callbacks = 0;
        var admission = new PromptTemplateInputAdmission(Catalog("$1"), PromptTemplateInputOperation.Prompt,
            new AsyncHandler((_, _) => { callbacks++; return ValueTask.FromResult(new PromptInputDecision(PromptInputAction.Continue)); }));
        await Canceled(admission.ReduceAsync(new("/review raw"), cancellation.Token).AsTask(), cancellation.Token); Equal(0, callbacks);
        using var pendingCancellation = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); var joined = false;
        admission = new(Catalog("$1"), PromptTemplateInputOperation.Prompt, new AsyncHandler(async (_, token) =>
        { Equal(pendingCancellation.Token, token); entered.SetResult(); await release.Task; joined = true; return new(PromptInputAction.Continue); }));
        var pending = admission.ReduceAsync(new("/review raw"), pendingCancellation.Token).AsTask();
        try
        {
            await Task.WhenAny(entered.Task, pending);
            if (!entered.Task.IsCompleted) { await pending; throw new InvalidOperationException("Original handler was not entered."); }
            pendingCancellation.Cancel(); Equal(false, pending.IsCompleted);
        }
        finally { release.TrySetResult(); await Canceled(pending, pendingCancellation.Token); }
        Equal(true, joined);
    }

    private static async Task CommandCancellation()
    {
        using var cancellation = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); var joined = false;
        var handlers = new Handler(_ => throw new Exception("Canceled command reached handlers"));
        var commands = new AsyncCommands(async (_, token) =>
        { Equal(cancellation.Token, token); entered.SetResult(); await release.Task; joined = true; return false; });
        var admission = new PromptTemplateInputAdmission(Catalog("$1"), PromptTemplateInputOperation.Prompt, handlers, commands);
        var pending = admission.ReduceAsync(new("/review raw"), cancellation.Token).AsTask();
        try
        {
            await Task.WhenAny(entered.Task, pending);
            if (!entered.Task.IsCompleted) { await pending; throw new InvalidOperationException("Original command was not entered."); }
            cancellation.Cancel(); Equal(false, pending.IsCompleted);
        }
        finally { release.TrySetResult(); await Canceled(pending, cancellation.Token); }
        Equal(true, joined); Equal(0, handlers.Calls);
    }

    private static async Task Failures()
    {
        using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var original = new OperationCanceledException("Original handler failure", foreign.Token);
        var admission = new PromptTemplateInputAdmission(Catalog("$1"), PromptTemplateInputOperation.Prompt, new Handler(_ => throw original));
        var observed = false;
        try { await admission.ReduceAsync(new("/review raw"), default); }
        catch (OperationCanceledException error) when (ReferenceEquals(error, original)) { observed = true; }
        Equal(true, observed);
        var commandError = new InvalidOperationException("Original command failure");
        admission = new(Catalog("$1"), PromptTemplateInputOperation.Prompt, commands: new AsyncCommands((_, _) => throw commandError));
        try { await admission.ReduceAsync(new("/review raw"), default); }
        catch (InvalidOperationException error) when (ReferenceEquals(error, commandError)) { return; }
        throw new InvalidOperationException("Original command failure changed or was lost.");
    }

    private static PromptTemplateCatalogSnapshot Catalog(string body) => PromptTemplateCatalogBuilder.Build([
        new PromptTemplateReadText(new("caller://review", "review.md",
            new("caller://review", "supplied", PromptTemplateSourceScope.Temporary, PromptTemplateSourceOrigin.TopLevel)), body)],
        _ => throw new Exception("Unexpected YAML decoding"));
    private static JsonData Image(string data) => JsonData.Parse("[{\"type\":\"image\",\"data\":\"" + data + "\",\"mimeType\":\"image/png\"}]");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Canceled(Task task, CancellationToken token)
    {
        try { await task; }
        catch (OperationCanceledException error) when (error.CancellationToken == token) { return; }
        throw new InvalidOperationException("Original cancellation was not retained.");
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
    private sealed class Handler(Func<PromptInput, PromptInputDecision> callback) : IPromptInputAdmission
    {
        internal int Calls { get; private set; }
        public ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
        { Calls++; return ValueTask.FromResult(callback(input)); }
    }
    private sealed class AsyncHandler(Func<PromptInput, CancellationToken, ValueTask<PromptInputDecision>> callback) : IPromptInputAdmission
    { public ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token) => callback(input, token); }
    private sealed class Commands(Func<string, bool> registered) : IPromptTemplateCommandAdmission
    {
        internal int Executed { get; private set; } internal int Lookups { get; private set; } internal string? LastText { get; private set; }
        public bool IsRegisteredCommand(string text) { Lookups++; return registered(text); }
        public ValueTask<bool> TryExecuteAsync(string text, CancellationToken token)
        { Executed++; LastText = text; return ValueTask.FromResult(registered(text)); }
    }
    private sealed class AsyncCommands(Func<string, CancellationToken, ValueTask<bool>> execute) : IPromptTemplateCommandAdmission
    {
        public bool IsRegisteredCommand(string text) => false;
        public ValueTask<bool> TryExecuteAsync(string text, CancellationToken token) => execute(text, token);
    }
}
