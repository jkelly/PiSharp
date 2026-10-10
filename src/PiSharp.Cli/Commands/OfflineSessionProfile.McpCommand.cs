// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts (registerCommand("mcp") outside
// interactive mode: no arguments notify formatStatus(); login, logout and reconnect act on one server through ctx.ui) and
// packages/coding-agent/src/extensions/index.ts (mcp is a built-in extension: not loaded, it registers no /mcp).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Extensions;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    /// <summary>The Pi entry's current MCP server manager outside interactive mode (the interactive mode runs <c>/mcp</c> itself).</summary>
    internal Func<McpServerManager?>? McpManager { get; set; }

    /// <summary>Whether <paramref name="text"/> invokes the built-in mcp extension's <c>/mcp</c> in a Pi entry outside interactive mode
    /// (no extension command of that name was registered).</summary>
    private bool IsMcpCommand(string text)
    {
        if (PiReloadResources is null || McpManager?.Invoke() is null || !(text == "/mcp" || text.StartsWith("/mcp ", StringComparison.Ordinal))) return false;
        if (BuiltinExtensions?.IsEnabled(PiSharp.Cli.Extensions.Pi.PiBuiltinExtensions.Mcp) == false) return false;
        try
        {
            var catalog = ExtensionCommandCatalog();
            return catalog.ValueKind != JsonValueKind.Array || !catalog.EnumerateArray().Any(row => row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "mcp");
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { return true; }
    }

    /// <summary>index.ts /mcp handler with ctx.mode other than "tui": the status, or login/logout/reconnect, through the session's
    /// extension UI (none in print and JSON mode, where notifications go nowhere).</summary>
    private async Task RunMcpCommandAsync(string text, CancellationToken token)
    {
        if (McpManager?.Invoke() is not { } manager) return;
        var provider = BuiltinCommandUi;
        IExtensionUiScope? scope = null;
        try
        {
            scope = provider?.OpenScope(new BuiltinMcpContext(token));
            await manager.ExecuteCommandAsync(text.Length > 4 ? text[5..] : "", new McpCommandUi(scope), token).ConfigureAwait(false);
        }
        finally { if (scope is not null) await scope.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class McpCommandUi(IExtensionUiScope? scope) : IMcpCommandUi
    {
        public bool HasUi => scope is not null && scope.Capabilities.Mode is ExtensionUiMode.Rpc or ExtensionUiMode.Tui;
        public bool IsTui => false;
        public void Notify(string message, string level)
        {
            if (scope is null) return;
            var kind = level switch { "warning" => ExtensionUiNotifyKind.Warning, "error" => ExtensionUiNotifyKind.Error, _ => ExtensionUiNotifyKind.Info };
            try { scope.PublishAsync(new ExtensionUiNotify(message, kind)).AsTask().GetAwaiter().GetResult(); }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ObjectDisposedException or ArgumentException) { }
        }
        public async Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken token)
        {
            if (scope is null) return null;
            var outcome = await scope.SelectAsync(title, [.. options], cancellationToken: token).ConfigureAwait(false);
            return outcome.Kind == ExtensionUiOutcomeKind.Value ? outcome.Value : null;
        }
        public async Task<string?> InputAsync(string title, string placeholder, CancellationToken token)
        {
            if (scope is null) return null;
            var outcome = await scope.InputAsync(title, placeholder, cancellationToken: token).ConfigureAwait(false);
            return outcome.Kind == ExtensionUiOutcomeKind.Value ? outcome.Value : null;
        }
        public Task ShowManagerAsync(Func<IMcpManagerUi, Task> manage) => throw new NotSupportedException("The MCP manager view needs the interactive terminal.");
    }

    private sealed class BuiltinMcpContext(CancellationToken token) : IExtensionContext
    {
        public string OwnerId => "builtin:mcp";
        public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => token;
        public CancellationToken SessionCancellationToken => CancellationToken.None;
        public CancellationToken ExtensionLifetimeCancellationToken => CancellationToken.None;
    }
}
