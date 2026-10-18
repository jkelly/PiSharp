// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/tui.ts (runDurableTui).
// Upstream runs on Node's event loop and resolves when the user exits; here the TUI runs on its own UiLoop, view changes from the runtime
// (any thread) are posted to it, and RunAsync completes with exit code 0 when the user exits or the token is cancelled. Controller calls are
// fire-and-forget as upstream's `void controller.x()`: the runtime reports failures as notices. PiSharp-only since pi-durable 1.2.0 (durable
// work list area l): once the session failed, the editor sends nothing and RunAsync returns 1 when the user exits.
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Pi;
using PiSharp.Durable;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Durable;

/// <summary>Where the TUI draws and which agent directory it reads keybindings from; tests pass a virtual terminal.</summary>
/// <param name="CreateTerminal">The terminal on the TUI's loop; the process's terminal when null.</param>
/// <param name="AgentDir">pi's agent directory (keybindings.json, themes, TUI logs); the directory of the settings' global
/// settings.json when null.</param>
internal sealed record DurableTuiOptions(Func<UiLoop, ITerminal>? CreateTerminal = null, string? AgentDir = null);

/// <summary>The `pisharp durable` terminal UI (upstream <c>runDurableTui</c>): renders <see cref="IOpenDurable.View"/> and drives
/// <see cref="IOpenDurable.Controller"/> until the user exits.</summary>
internal static class DurableTui
{
    /// <summary>Runs the TUI until the user exits; returns the process exit code: 0, or 1 when the session failed (<see cref="DurableView.Failure"/>).
    /// The caller closes <paramref name="durable"/>.</summary>
    public static Task<int> RunAsync(IOpenDurable durable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(durable);
        return RunAsync(durable, new DurableTuiOptions(), cancellationToken);
    }

    /// <summary>Runs the TUI with <paramref name="options"/>.</summary>
    internal static async Task<int> RunAsync(IOpenDurable durable, DurableTuiOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(durable);
        ArgumentNullException.ThrowIfNull(options);
        var source = durable.View;
        var controller = durable.Controller;
        // pi's theme controller and capability overrides read pi's interactive settings; durable mode has pi's settings files, read-only.
        var settings = new InteractiveSettings((System.Text.Json.Nodes.JsonObject)durable.Settings.Merged.DeepClone());
        var agentDir = options.AgentDir ?? Path.GetDirectoryName(durable.Settings.GlobalPath) ?? "";
        using var loop = new UiLoop("pi-durable");
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.UnhandledException += error => exited.TrySetException(error);
        using var registration = cancellationToken.Register(() => exited.TrySetResult());
        DurableTuiView? view = null;
        InteractiveThemeController? themes = null;
        IDisposable? subscription = null;
        var stopped = false;
        var applyPending = 0;
        try
        {
            await loop.InvokeAsync(() =>
            {
                TerminalImage.SetCapabilityOverrides(settings.TerminalCapabilityOverrides);
                Themes.AgentDirectory = agentDir;
                // The system theme until the controller resolves the user's theme against the terminal's colors.
                Themes.InitTheme();
                void Exit() => exited.TrySetResult();

                void SelectModel()
                {
                    var snapshot = source.Current;
                    var current = DurableTuiState.AgentOf(snapshot.Conversation).Model;
                    bool IsCurrent(DurableModelSummary model) => current is not null && model.Provider == current.Provider && model.ModelId == current.ModelId;
                    var items = snapshot.Models.OrderByDescending(IsCurrent)
                        .Select(model => new SelectItem($"{model.Provider}/{model.ModelId}", model.ModelId, model.Provider)).ToList();
                    var selector = new DurableListSelector("Select model:", items, value =>
                    {
                        view!.RestoreEditor();
                        var separator = value.IndexOf('/', StringComparison.Ordinal);
                        Fire(() => controller.SetModelAsync(new ModelRef(value[..separator], value[(separator + 1)..])));
                    }, () => view!.RestoreEditor());
                    view!.Mount(selector);
                }

                void SelectConversation()
                {
                    var snapshot = source.Current;
                    var items = Enumerable.Reverse(snapshot.Conversations).Select(candidate => new SelectItem(
                        candidate.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        candidate.Label,
                        $"{(candidate.Id == snapshot.Conversation.Conversation.Id ? "(shown) " : "")}{candidate.Title ?? ""}")).ToList();
                    var selector = new DurableListSelector("Switch to:", items, value =>
                    {
                        view!.RestoreEditor();
                        Fire(() => controller.SwitchConversationAsync(new ConversationId(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture))));
                    }, () => view!.RestoreEditor());
                    view!.Mount(selector);
                }

                view = new DurableTuiView(options.CreateTerminal?.Invoke(loop) ?? new ProcessTerminal(loop), loop, source.Current.Session.Cwd, agentDir,
                    new DurableTuiHandlers(
                        Submit: text =>
                        {
                            var trimmed = TextUtils.JsTrim(text);
                            // A failed session takes nothing more; its alert says how to reopen it.
                            if (trimmed.Length == 0 || source.Current.Failure is not null) return;
                            if (trimmed == "/model") { SelectModel(); return; }
                            if (trimmed == "/tasks") { Fire(controller.ToggleTasksAsync); return; }
                            // PiSharp-only (decision 0007): /tier <tier> for the shown conversation; /tier none clears it.
                            if (trimmed.StartsWith("/tier ", StringComparison.Ordinal))
                            {
                                var tier = TextUtils.JsTrim(trimmed["/tier".Length..]);
                                Fire(() => controller.SetServiceTierAsync(tier is "none" or "clear" ? null : tier));
                                return;
                            }
                            if (trimmed == "/agents") { SelectConversation(); return; }
                            if (trimmed == "/compact" || trimmed.StartsWith("/compact ", StringComparison.Ordinal))
                            {
                                var instructions = TextUtils.JsTrim(trimmed["/compact".Length..]);
                                Fire(() => controller.CompactAsync(instructions.Length > 0 ? instructions : null));
                                return;
                            }
                            Fire(() => controller.SubmitAsync(trimmed, WhenBusy.Steer));
                        },
                        FollowUp: text =>
                        {
                            if (source.Current.Failure is null) Fire(() => controller.SubmitAsync(text, WhenBusy.FollowUp));
                        },
                        Abort: () => Fire(controller.AbortAsync),
                        Exit: Exit,
                        SelectModel: SelectModel,
                        CycleThinking: () => Fire(controller.CycleThinkingAsync)),
                    // Tool cards as interactive mode makes them: images shown or not, their width, the output padding.
                    new ToolExecutionOptions(settings.ShowImages, settings.ImageWidthCells, settings.OutputPad));

                // pi's theme handling: the theme setting (also light/dark pairs) resolved against the terminal's reported colors.
                themes = new InteractiveThemeController(view.Ui, () => settings, message => Console.Error.WriteLine(message),
                    () => view.Ui.RequestRender(), null);
                // Changes arrive on the runtime's threads; one pending apply renders the newest view.
                subscription = source.Subscribe(() =>
                {
                    if (Interlocked.Exchange(ref applyPending, 1) != 0) return;
                    loop.Post(() =>
                    {
                        Volatile.Write(ref applyPending, 0);
                        if (!stopped) view.Apply(source.Current);
                    });
                });
                view.Start();
                themes.ApplyFromSettings();
                view.Apply(source.Current);
            });
            await exited.Task;
            return source.Current.Failure is null ? 0 : 1;
        }
        finally
        {
            try
            {
                await loop.InvokeAsync(() =>
                {
                    stopped = true;
                    subscription?.Dispose();
                    themes?.Dispose();
                    view?.Stop();
                });
            }
            catch (Exception) when (exited.Task.IsFaulted)
            {
                // The loop's failure is what the caller sees.
            }
            loop.Stop();
        }
    }

    /// <summary>Upstream's <c>void controller.x()</c>: starts the request and leaves its outcome to the runtime, which reports failures as
    /// notices.</summary>
    private static void Fire(Func<Task> request)
    {
        Task task;
        try { task = request(); }
        catch (Exception) { return; }
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
