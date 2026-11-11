// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): cli/auth-login.ts and cli/auth-logout.ts.
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;

namespace PiSharp.Cli.Pi;

internal static partial class PiAuthCommand
{
    private static async Task<int> RunLoginLogoutAsync(string[] args, PiHost host, ProviderLoginHost? supplied, CancellationToken token)
    {
        var login = args[1] == "login";
        string? reference = null;
        var apiKey = false;
        var browser = true;
        async Task<int> Error(string message)
        {
            await PiCommand.Line(host.Stderr, "Error: " + message).ConfigureAwait(false);
            return 1;
        }
        for (var index = 2; index < args.Length; index++)
        {
            var arg = args[index];
            if (login && arg == "--api-key") apiKey = true;
            else if (login && arg == "--no-browser") browser = false;
            else if (arg == "--provider" && reference is null && index + 1 < args.Length) reference = args[++index];
            else if (!arg.StartsWith('-') && reference is null) reference = arg;
            else return await Error($"Usage: {PiConfig.DisplayName} auth {args[1]} <provider>" + (login ? " [--api-key] [--no-browser]" : "")).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(reference)) return await Error($"Usage: {PiConfig.DisplayName} auth {args[1]} <provider>").ConfigureAwait(false);
        if (apiKey && !browser) return await Error("--no-browser only applies to account sign-in; omit it with --api-key.").ConfigureAwait(false);
        var options = ProviderAuthCatalog.FindLoginOptions(reference);
        var provider = options.FirstOrDefault()?.Provider;
        var runtime = host.LiveRuntime;
        var authPath = supplied?.AuthPath ?? runtime.AuthPath;
        if (authPath is null) return await Error("Credential storage is not configured.").ConfigureAwait(false);
        var store = supplied?.Store ?? new AuthJsonCredentialStore(authPath, runtime.Time);
        try
        {
            if (!login)
            {
                var id = provider?.Id ?? reference;
                var stored = await store.ListAsync(token).ConfigureAwait(false);
                var credential = stored.FirstOrDefault(entry => entry.Provider == id);
                if (provider is null && credential.Provider is null)
                {
                    await Error($"Unknown provider \"{SafeAuthText(reference)}\".").ConfigureAwait(false);
                    await PiCommand.Line(host.Stderr, "Stored credentials: " + (stored.Count == 0 ? "none" : string.Join(", ", stored.Select(entry => SafeAuthText(entry.Provider)).Order(StringComparer.Ordinal)))).ConfigureAwait(false);
                    return 1;
                }
                var name = SafeAuthText(provider?.Name ?? id);
                if (credential.Provider is null)
                    await PiCommand.Line(host.Stdout, $"No stored credentials for {name}. Environment variables and models.json config are unchanged.").ConfigureAwait(false);
                else
                {
                    await store.DeleteAsync(id, token).ConfigureAwait(false);
                    await PiCommand.Line(host.Stdout, credential.Type == "oauth" ? $"Logged out of {name}."
                        : $"Removed stored API key for {name}. Environment variables and models.json config are unchanged.").ConfigureAwait(false);
                }
                return 0;
            }
            if (provider is null)
            {
                await Error($"Unknown provider \"{SafeAuthText(reference)}\".").ConfigureAwait(false);
                await PiCommand.Line(host.Stderr, "Providers: " + string.Join(", ", ProviderAuthCatalog.LoginOptions("oauth").Concat(ProviderAuthCatalog.LoginOptions("api_key"))
                    .Select(option => option.Provider.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))).ConfigureAwait(false);
                return 1;
            }
            if (!host.StdinIsTty || !host.StdoutIsTty) return await Error("auth login requires an interactive terminal.").ConfigureAwait(false);
            var method = !apiKey && provider.OAuth is not null ? "oauth" : "api_key";
            var option = options.FirstOrDefault(option => option.AuthType == method);
            if (option is null) return await Error($"{provider.Name} has no API key login. Omit --api-key to sign in.").ConfigureAwait(false);
            var loginHost = supplied ?? new ProviderLoginHost(store, runtime.CreateAuthHttp ?? (() => new HttpClient()),
                url => PiSharp.Cli.Interactive.Mode.Utilities.BrowserOpener.OpenBrowser(url), runtime.Time)
            {
                AllProviders = true, ReadEnvironment = runtime.ReadEnvironment,
                GetDeviceId = () => DeviceIdentity.GetOrCreate(Path.GetDirectoryName(authPath)!), IsCopilotCatalogModel = CopilotCatalog.Contains
            };
            await loginHost.LoginAsync(option, new ShellLoginInteraction(host, loginHost, method == "oauth", browser), token).ConfigureAwait(false);
            await PiCommand.Line(host.Stdout, (method == "oauth" ? $"Logged in to {provider.Name}." : $"Saved API key for {provider.Name}.") + $" Credentials saved to {authPath}").ConfigureAwait(false);
            await RefreshLoginCatalogAsync(provider.Id, provider.Name, host.Stdout, async refreshToken =>
            {
                var registry = await (runtime with { Credentials = () => store }).CreateModelRegistryAsync(refreshToken).ConfigureAwait(false);
                return await registry.RefreshAsync(allowNetwork: true, force: null, providers: [provider.Id], refreshToken).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            if (provider.Id == "radius")
            {
                var agentDir = Path.GetDirectoryName(Path.GetFullPath(authPath))!;
                try
                {
                    if (PiSharp.Cli.Mcp.RadiusMcpServer.Plan(provider.Id, agentDir, host.Cwd) is { } plan)
                        await PiCommand.Line(host.Stdout, $"{(plan.Apply() ? "Updated" : "Added")} MCP server \"{plan.Name}\" in {plan.ConfigPath} to use the {provider.Name} login.").ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                { await PiCommand.Line(host.Stderr, $"Could not update {Path.Combine(agentDir, "mcp.json")}: {SafeAuthText(error.Message)}").ConfigureAwait(false); }
            }
            return 0;
        }
        catch (OperationCanceledException) { return await Error("Login cancelled.").ConfigureAwait(false); }
        catch (Exception) { return await Error(login ? $"Failed to log in to {SafeAuthText(provider?.Name ?? reference)}." : "Failed to remove stored credentials.").ConfigureAwait(false); }
    }

    private static string SafeAuthText(string text) => PiSharp.Tui.Pi.TextUtils.ReplaceControlCharacters(text);

    /// <summary>The credential is saved; refresh failures and cancellation are diagnostic only.</summary>
    internal static async Task RefreshLoginCatalogAsync(string providerId, string providerName, TextWriter output,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, Exception>>> refresh, CancellationToken token,
        TimeSpan? refreshTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(refreshTimeout ?? TimeSpan.FromSeconds(15));
        try
        {
            var errors = await refresh(timeout.Token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
                await PiCommand.Line(output, $"Skipped the {providerName} model catalog refresh; {PiConfig.DisplayName} refreshes it on next start.").ConfigureAwait(false);
            else if (timeout.IsCancellationRequested)
                await PiCommand.Line(output, $"The {providerName} model catalog refresh timed out; {PiConfig.DisplayName} refreshes it on next start.").ConfigureAwait(false);
            else if (errors.Count > 0)
                await PiCommand.Line(output, $"The {providerName} model catalog could not be refreshed; {PiConfig.DisplayName} retries on next start.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await PiCommand.Line(output, $"Skipped the {providerName} model catalog refresh; {PiConfig.DisplayName} refreshes it on next start.").ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { await PiCommand.Line(output, $"The {providerName} model catalog refresh timed out; {PiConfig.DisplayName} refreshes it on next start.").ConfigureAwait(false); }
        catch (Exception error)
        { await PiCommand.Line(output, $"The {providerName} model catalog could not be refreshed: {SafeAuthText(error.Message)}").ConfigureAwait(false); }
    }

    private sealed class ShellLoginInteraction(PiHost host, ProviderLoginHost loginHost, bool account, bool browser) : IProviderAuthInteraction
    {
        public async Task<string> PromptAsync(AuthPrompt prompt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (prompt.Kind == AuthPromptKind.Select && prompt.Options is { Count: > 0 } options)
            {
                if (account && browser) return options[0].Id;
                var available = account && !browser && options.Count > 1 ? options.Skip(1).ToList() : options.ToList();
                if (account && available.Count == 1) return available[0].Id;
                await PiCommand.Line(host.Stdout, prompt.Message).ConfigureAwait(false);
                for (var i = 0; i < available.Count; i++) await PiCommand.Line(host.Stdout, $"  {i + 1}. {available[i].Label}").ConfigureAwait(false);
                while (true)
                {
                    var answer = await ReadLineAsync(false, token).ConfigureAwait(false);
                    if (int.TryParse(answer, out var number) && number > 0 && number <= available.Count) return available[number - 1].Id;
                    await PiCommand.Line(host.Stdout, $"Enter a number from 1 to {available.Count}.").ConfigureAwait(false);
                }
            }
            await host.Stdout.WriteAsync(prompt.Message + (prompt.Placeholder is { Length: > 0 } placeholder ? $" ({placeholder})" : "") + ": ").ConfigureAwait(false);
            await host.Stdout.FlushAsync(token).ConfigureAwait(false);
            return await ReadLineAsync(prompt.Kind == AuthPromptKind.Secret, token).ConfigureAwait(false);
        }

        private async Task<string> ReadLineAsync(bool secret, CancellationToken token)
        {
            // Console.In's synchronized reader can block before yielding a task. Polling key input keeps a raced
            // manual-code prompt cancellable when the browser callback completes, without leaving an orphan read.
            if (ReferenceEquals(host.Stdin, Console.In) && !Console.IsInputRedirected)
            {
                var input = new System.Text.StringBuilder();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Console.KeyAvailable) { await Task.Delay(25, token).ConfigureAwait(false); continue; }
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) { await PiCommand.Line(host.Stdout, "").ConfigureAwait(false); return input.ToString(); }
                    if (key.Key == ConsoleKey.Backspace && input.Length > 0)
                    {
                        input.Length--;
                        if (!secret) await host.Stdout.WriteAsync("\b \b").ConfigureAwait(false);
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        input.Append(key.KeyChar);
                        if (!secret) await host.Stdout.WriteAsync(key.KeyChar).ConfigureAwait(false);
                    }
                    await host.Stdout.FlushAsync(token).ConfigureAwait(false);
                }
            }
            return await host.Stdin.ReadLineAsync(token).ConfigureAwait(false) ?? throw OAuthFlows.Cancelled(token);
        }

        public void Notify(AuthEvent authEvent)
        {
            if (authEvent.Message is { } message) host.Stdout.WriteLine(message);
            var url = authEvent.Kind == AuthEventKind.AuthUrl ? authEvent.Url : authEvent.Kind == AuthEventKind.DeviceCode ? authEvent.VerificationUri : null;
            if (url is not null)
            {
                host.Stdout.WriteLine($"Open this URL to sign in: {SafeAuthText(url)}");
                if (authEvent.UserCode is { } code) host.Stdout.WriteLine($"Enter code: {SafeAuthText(code)}");
                if (browser && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") loginHost.OpenBrowser(url);
            }
            if (authEvent.Instructions is { } instructions) host.Stdout.WriteLine(instructions);
            foreach (var link in authEvent.Links ?? []) host.Stdout.WriteLine($"{link.Label}: {SafeAuthText(link.Url)}");
            host.Stdout.Flush();
        }
    }
}
