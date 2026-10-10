// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/llama/index.ts (the /llama command outside
// interactive mode: ctx.ui.notify("/llama is available in interactive mode", "warning")) and packages/coding-agent/src/extensions/index.ts
// (llama.cpp is a built-in extension: not loaded, it registers no /llama).
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    /// <summary>The session's extension UI (RPC with extensions: extension_ui_request; print and JSON modes have none).</summary>
    internal IExtensionUiProvider? BuiltinCommandUi { get; set; }

    /// <summary>The Pi entry's built-in extensions (null outside it: all of them).</summary>
    internal PiSharp.Cli.Extensions.Pi.PiBuiltinExtensions? BuiltinExtensions { get; set; }

    /// <summary>Whether <paramref name="text"/> invokes the built-in llama.cpp extension's <c>/llama</c> in a Pi entry (no extension
    /// command of that name was registered; the interactive mode handles <c>/llama</c> itself).</summary>
    private bool IsLlamaCommand(string text)
    {
        if (PiReloadResources is null || !(text == "/llama" || text.StartsWith("/llama ", StringComparison.Ordinal))) return false;
        // Without the built-in llama.cpp extension (-builtin:llama.cpp, --no-extensions) there is no /llama: the text is a prompt.
        if (BuiltinExtensions?.IsEnabled(PiSharp.Cli.Extensions.Pi.PiBuiltinExtensions.Llama) == false) return false;
        try
        {
            var catalog = ExtensionCommandCatalog();
            return catalog.ValueKind != JsonValueKind.Array || !catalog.EnumerateArray().Any(row => row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "llama");
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { return true; }
    }

    /// <summary>index.ts: outside interactive mode the command only warns.</summary>
    private async Task NotifyLlamaUnavailableAsync(CancellationToken token)
    {
        if (BuiltinCommandUi is not { } provider) return;
        try
        {
            await using var scope = provider.OpenScope(new BuiltinContext(token));
            await scope.PublishAsync(new ExtensionUiNotify("/llama is available in interactive mode", ExtensionUiNotifyKind.Warning), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ObjectDisposedException or ArgumentException) { }
    }

    private sealed class BuiltinContext(CancellationToken token) : IExtensionContext
    {
        public string OwnerId => "builtin:llama.cpp";
        public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => token;
        public CancellationToken SessionCancellationToken => CancellationToken.None;
        public CancellationToken ExtensionLifetimeCancellationToken => CancellationToken.None;
    }
}
