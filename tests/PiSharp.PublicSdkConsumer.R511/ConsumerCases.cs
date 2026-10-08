using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;
using PiSharp.PublicSdkConsumer.R511;

internal static class ConsumerCases
{
    internal static IEnumerable<(string Id, Func<Task> Run)> All() =>
    [
        ("public-sdk.registration-input-command", RegistrationInputCommand),
        ("public-sdk.fresh-owned-branch-and-ui-expiry", FreshViews),
        ("public-sdk.held-callback-disposal-original-join", HeldDisposal),
        ("public-sdk.capture-failure-before-plugin-entry", CaptureFailure)
    ];

    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Public SDK control failed."); }
    private sealed class ViewProvider : IExtensionSessionViewProvider
    {
        public ExtensionSessionSnapshot? Current = new("consumer-session", 7, "a", [JsonData.Parse("{\"id\":\"a\",\"unknown\":1.0}")]);
        public Exception? Failure;
        public int Captures;
        public IExtensionContext? LastContext;
        public ExtensionSessionSnapshot? Capture(IExtensionContext context)
        { Captures++; LastContext = context; if (Failure is { } failure) throw failure; return Current; }
    }
    private static async Task RegistrationInputCommand()
    {
        var provider = new ViewProvider();
        await using var registry = new ExtensionRegistry(null, new UnavailableExtensionUiProvider(), provider);
        var plugin = new PublicConsumer();
        await registry.ActivateAsync("public-consumer", plugin);
        var snapshot = registry.CaptureSnapshot();
        var dispatcher = new RegisteredExtensionEventDispatcher(registry, _ => { });
        using var operation = new CancellationTokenSource();
        using var session = new CancellationTokenSource();
        var images = JsonData.Parse("[{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\",\"opaque\":9007199254740993}]");
        var input = new ExtensionInputEvent("before", ExtensionInputSource.Rpc, images, "followUp");
        var result = await dispatcher.DispatchInputAsync(snapshot, input, operation.Token, session.Token);
        Require(result.Action == ExtensionInputAction.Transform && result.Event.Text == "before:public");
        Require(result.Event.Images?.ToString() == images.ToString() && result.Event.StreamingBehavior == "followUp");
        Require(result.Diagnostics.IsEmpty && plugin.InputCalls == 1 && input.Text == "before");
        Require(ReferenceEquals(provider.LastContext, plugin.RetainedContext));
        Require(plugin.RetainedContext!.OperationCancellationToken == operation.Token && plugin.RetainedContext.SessionCancellationToken == session.Token);
        await registry.InvokeCommandAsync(snapshot, "public-command", JsonData.Null);
        Require(plugin.CommandCalls == 1 && provider.Captures == 2 && plugin.RetainedView?.SessionId == "consumer-session");
    }
    private static async Task FreshViews()
    {
        var provider = new ViewProvider();
        await using var registry = new ExtensionRegistry(null, new UnavailableExtensionUiProvider(), provider);
        var plugin = new PublicConsumer();
        await registry.ActivateAsync("public-consumer", plugin);
        var registrations = registry.CaptureSnapshot();
        var value = JsonData.Parse("{\"opaque\":9007199254740993}");
        Require((await registry.InvokeToolAsync(registrations, "public-echo", value)).ToString() == value.ToString());
        var first = plugin.RetainedView!;
        var firstUi = plugin.RetainedUi!;
        Require(!ReferenceEquals(first, provider.Current) && first.SelectedLeafId == "a");
        Require(plugin.InitialUiOutcome?.UnavailableReason == ExtensionUiUnavailableReason.NoUi);
        provider.Current = new("consumer-session", 7, "b", [JsonData.Parse("{\"id\":\"b\"}")]);
        await registry.InvokeToolAsync(registrations, "public-echo", value);
        Require(plugin.ToolCalls == 2 && provider.Captures == 2 && plugin.RetainedView?.SelectedLeafId == "b");
        Require(!ReferenceEquals(first, plugin.RetainedView) && first.BranchEntries[0].ToString() == "{\"id\":\"a\",\"unknown\":1.0}");
        Require((await firstUi.InputAsync("expired")).UnavailableReason == ExtensionUiUnavailableReason.StaleContext);
    }
    private static async Task HeldDisposal()
    {
        var provider = new ViewProvider();
        await using var registry = new ExtensionRegistry(null, new UnavailableExtensionUiProvider(), provider);
        var plugin = new PublicConsumer { HoldTool = true };
        var scope = await registry.ActivateAsync("public-consumer", plugin);
        var captured = registry.CaptureSnapshot();
        var value = JsonData.Parse("{\"held\":true}");
        var original = registry.InvokeToolAsync(captured, "public-echo", value).AsTask();
        Task? close = null;
        Exception? primary = null;
        try
        {
            await plugin.Entered.Task;
            close = scope.DisposeAsync().AsTask();
            Require(ReferenceEquals(close, scope.DisposeAsync().AsTask()));
            Require(!original.IsCompleted && !close.IsCompleted && plugin.DisposalCalls == 0);
            Require(plugin.RetainedContext!.ExtensionLifetimeCancellationToken.IsCancellationRequested);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            plugin.Release.TrySetResult();
            try { await original; } catch (Exception error) { primary = primary is null ? error : new AggregateException(primary, error); }
            if (close is not null) try { await close; } catch (Exception error) { primary = primary is null ? error : new AggregateException(primary, error); }
        }
        if (primary is not null) throw primary;
        Require(original.IsCompletedSuccessfully && original.Result.ToString() == value.ToString());
        Require(close?.IsCompletedSuccessfully == true && plugin.DisposalCalls == 1);
        Require((await plugin.RetainedUi!.InputAsync("expired")).UnavailableReason == ExtensionUiUnavailableReason.StaleContext);
        try { await registry.InvokeToolAsync(captured, "public-echo", value); }
        catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.StaleSnapshot) { return; }
        throw new InvalidOperationException("Disposed owner still admitted.");
    }
    private static async Task CaptureFailure()
    {
        var marker = new InvalidOperationException("Capture marker.");
        var provider = new ViewProvider { Failure = marker };
        await using var registry = new ExtensionRegistry(null, null, provider);
        var plugin = new PublicConsumer();
        var owner = await registry.ActivateAsync("public-consumer", plugin);
        var original = registry.InvokeToolAsync(registry.CaptureSnapshot(), "public-echo", JsonData.EmptyObject).AsTask();
        Exception? observed = null;
        try { await original; } catch (Exception error) { observed = error; }
        Require(original.IsFaulted && !original.IsCanceled && ReferenceEquals(observed, marker));
        var inventory = original.Exception!;
        Require(inventory.InnerExceptions.Count == 1 && ReferenceEquals(inventory.InnerExceptions[0], marker));
        Require(plugin.ToolCalls == 0 && provider.Captures == 1);
        await owner.DisposeAsync();
        Require(plugin.DisposalCalls == 1);
    }
}
