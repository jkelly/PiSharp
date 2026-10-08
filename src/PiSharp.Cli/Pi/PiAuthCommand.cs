// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/cli/auth-command.ts, cli/auth-check.ts,
// cli/credential-print.ts and the auth branch of packages/coding-agent/src/main.ts (runAuthCommand).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Models;

namespace PiSharp.Cli.Pi;

/// <summary><c>pisharp auth print-api-key|print-bearer-token|check</c>.</summary>
internal static partial class PiAuthCommand
{
    private sealed class AuthCommandError(string message) : Exception(message);
    private enum Kind { Check, ApiKey, BearerToken }

    private static string Name(Kind kind) => kind == Kind.Check ? "auth check" : kind == Kind.ApiKey ? "auth print-api-key" : "auth print-bearer-token";
    private static string Usage(Kind kind) => kind switch
    {
        Kind.Check => $"{PiConfig.DisplayName} auth check --provider <provider> [--json] [--credentials] [--no-refresh]",
        Kind.ApiKey => $"{PiConfig.DisplayName} auth print-api-key --provider <provider> [--model <model>]",
        _ => $"{PiConfig.DisplayName} auth print-bearer-token --provider <provider> [--model <model>] [--min-expiry <duration>]"
    };

    internal static string HelpText => $"""
        Usage:
          {PiConfig.DisplayName} auth print-api-key [--provider <provider>] [--model <model>]
          {PiConfig.DisplayName} auth print-bearer-token [--provider <provider>] [--model <model>] [--min-expiry <duration>]
          {PiConfig.DisplayName} auth check [--provider <provider>] [--model <model>] [--json] [--credentials] [--no-refresh]

        Auth commands require at least one of --provider or --model. Checks refresh expired OAuth credentials by default; --no-refresh prevents this. --credentials emits the credential, or includes it in JSON output.
        """.Replace("\r\n", "\n");

    [GeneratedRegex(@"^(\d+)(ms|s|m|h)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Duration();

    internal static async Task<int> RunAsync(string[] args, PiHost host, CancellationToken token)
    {
        var err = host.Stderr;
        async Task<int> Fail(string message, int code)
        {
            await PiCommand.Line(err, host.Color ? PiCommand.Red + $"Error: {message}\u001b[39m" : $"Error: {message}").ConfigureAwait(false);
            return code;
        }
        // Source isAuthCommandHelp.
        if (args.Length < 2 || args[1] == "help" || args.Contains("--help") || args.Contains("-h"))
        {
            await PiCommand.Line(host.Stdout, HelpText).ConfigureAwait(false);
            return 0;
        }
        Kind kind; var commandArgs = new List<string>(); bool json = false, credentials = false, noRefresh = false; long? minExpiry = null;
        try
        {
            kind = args[1] switch
            {
                "check" => Kind.Check, "print-api-key" => Kind.ApiKey, "print-bearer-token" => Kind.BearerToken,
                _ => throw new AuthCommandError($"Unknown auth command \"{args[1]}\". Use \"{PiConfig.DisplayName} auth print-api-key\", \"{PiConfig.DisplayName} auth print-bearer-token\", or \"{PiConfig.DisplayName} auth check\".")
            };
            for (var index = 2; index < args.Length; index++)
            {
                var arg = args[index];
                if (arg == "--min-expiry")
                {
                    if (kind != Kind.BearerToken) throw new AuthCommandError("--min-expiry is only supported by print-bearer-token");
                    var value = ++index < args.Length ? args[index] : null;
                    var match = value is null ? null : Duration().Match(value);
                    if (match is not { Success: true }) throw new AuthCommandError("--min-expiry must use a duration such as 30m or 1h");
                    var amount = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    minExpiry = amount * match.Groups[2].Value.ToLowerInvariant() switch { "ms" => 1, "s" => 1_000, "m" => 60_000, _ => 3_600_000 };
                    continue;
                }
                if (arg is "--json" or "--credentials" or "--no-refresh")
                {
                    if (kind != Kind.Check) throw new AuthCommandError($"{arg} is only supported by auth check");
                    if (arg == "--json") json = true; else if (arg == "--credentials") credentials = true; else noRefresh = true;
                    continue;
                }
                commandArgs.Add(arg);
            }
        }
        catch (AuthCommandError error) { return await Fail(error.Message, 1).ConfigureAwait(false); }

        var parsed = PiArgs.Parse(commandArgs);
        if (parsed.UnknownFlags.Count > 0)
        {
            var option = parsed.UnknownFlags.Keys.First();
            await PiCommand.Line(err, host.Color ? PiCommand.Red + $"Unknown option --{option} for \"{Name(kind)}\".\u001b[39m" : $"Unknown option --{option} for \"{Name(kind)}\".").ConfigureAwait(false);
            await PiCommand.Line(err, host.Color ? PiCommand.Dim + $"Use \"{PiConfig.DisplayName} --help\" or \"{Usage(kind)}\".\u001b[22m" : $"Use \"{PiConfig.DisplayName} --help\" or \"{Usage(kind)}\".").ConfigureAwait(false);
            return 1;
        }
        try
        {
            if (parsed.Diagnostics.Count > 0) throw new AuthCommandError(string.Join("\n", parsed.Diagnostics.Select(diagnostic => diagnostic.Message)));
            var (provider, model) = Validate(parsed, kind);
            var runtime = host.LiveRuntime;
            if (noRefresh) runtime = runtime with { CreateAuthHttp = null };
            var registry = await runtime.CreateModelRegistryAsync(token).ConfigureAwait(false);
            var store = runtime.AuthPath is null ? null : new PiSharp.Cli.Authentication.AuthJsonCredentialStore(runtime.AuthPath, runtime.Time);
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            if (store is not null) foreach (var (id, type) in await store.ListAsync(token).ConfigureAwait(false)) types[id] = type;
            if (kind != Kind.Check)
            {
                var credential = await PrintAsync(kind, provider, model, registry, store, types, minExpiry ?? 30 * 60_000, runtime, token).ConfigureAwait(false);
                await PiCommand.Line(host.Stdout, credential).ConfigureAwait(false);
                return 0;
            }
            JsonObject result;
            string? value = null;
            try
            {
                result = Check(provider, model, registry, types);
                if (credentials && result["status"]?.GetValue<string>() == "ready")
                {
                    value = await CredentialAsync(result["provider"]!.GetValue<string>(), registry, store, types, runtime, token).ConfigureAwait(false);
                    if (value is null) result = new JsonObject { ["status"] = "not_ready", ["provider"] = result["provider"]!.GetValue<string>(), ["reason"] = "credential_not_available" };
                }
            }
            catch (AuthCommandError) { throw; }
            catch (Exception) { result = new JsonObject { ["status"] = "invalid", ["provider"] = provider ?? model, ["reason"] = "invalid_state" }; }
            if (value is not null) result["credentials"] = value;
            var status = result["status"]!.GetValue<string>();
            await PiCommand.Line(host.Stdout, json ? PiJson.Stringify(result) : value ?? status).ConfigureAwait(false);
            return status == "ready" ? 0 : status == "not_ready" ? 1 : 2;
        }
        catch (AuthCommandError error) { return await Fail(error.Message, kind == Kind.Check ? 2 : 1).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { return await Fail(kind == Kind.Check ? error.Message : "Failed to resolve credential", kind == Kind.Check ? 2 : 1).ConfigureAwait(false); }
    }

    /// <summary>Source validateAuthCommandArgs.</summary>
    private static (string? Provider, string? Model) Validate(PiArgs args, Kind kind)
    {
        var provider = args.Provider is { } p && PiArgs.JsTrim(p).Length > 0 ? PiArgs.JsTrim(p) : null;
        var model = args.Model is { } m && PiArgs.JsTrim(m).Length > 0 ? PiArgs.JsTrim(m) : null;
        if (args.ApiKey is not null || args.Messages.Count > 0 || args.FileArgs.Count > 0) throw new AuthCommandError("Auth commands only accept --provider and --model");
        if (provider is null && model is null)
            throw new AuthCommandError(kind == Kind.Check ? "Auth checks require --provider <provider> or --model <model>" : "Credential printing requires --provider <provider> or --model <model>");
        return (provider, model);
    }

    /// <summary>Source checkProviderAuth.</summary>
    private static JsonObject Check(string? cliProvider, string? cliModel, ModelRegistry registry, Dictionary<string, string> types)
    {
        var provider = cliProvider;
        if (cliModel is not null)
        {
            var resolved = ModelResolver.ResolveCliModel(cliProvider, cliModel, null, registry.GetAll(), registry.HasConfiguredAuth);
            if (resolved.Error is not null || resolved.Model is null) throw new AuthCommandError(resolved.Error ?? $"Unable to resolve model \"{cliModel}\"");
            provider = resolved.Model.Provider;
        }
        if (provider is null) throw new AuthCommandError("Unable to resolve an auth provider");
        if (registry.GetError() is not null) return new() { ["status"] = "invalid", ["provider"] = provider, ["reason"] = "invalid_state" };
        if (!registry.GetProviderIds().Contains(provider)) return new() { ["status"] = "not_ready", ["provider"] = provider, ["reason"] = "provider_not_found" };
        if (registry.CheckAuth(provider) is null) return new() { ["status"] = "not_ready", ["provider"] = provider, ["reason"] = "credentials_not_configured" };
        return new() { ["status"] = "ready", ["provider"] = provider, ["authType"] = types.TryGetValue(provider, out var type) && type == "oauth" ? "oauth" : "api_key" };
    }

    /// <summary>Source getProviderCredential: a stored OAuth access token, else the request auth's API key or bearer token.</summary>
    private static async Task<string?> CredentialAsync(string provider, ModelRegistry registry, PiSharp.Cli.Authentication.AuthJsonCredentialStore? store,
        Dictionary<string, string> types, PiSharp.Cli.Commands.LiveSessionRuntime runtime, CancellationToken token)
    {
        if (types.TryGetValue(provider, out var type) && type == "oauth" && store is not null)
            return (await store.ReadAsync(provider, token).ConfigureAwait(false))?.Access;
        var model = registry.GetAll().FirstOrDefault(entry => entry.Provider == provider);
        return model is null ? null : FromAuth(registry.ResolveRequestAuth(model, out _));
    }

    private static string? FromAuth(ModelRequestAuth? auth)
    {
        if (auth?.ApiKey is { Length: > 0 } key) return key;
        var authorization = auth?.Headers?.FirstOrDefault(pair => pair.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)).Value;
        var match = authorization is null ? null : Regex.Match(authorization, @"^Bearer\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match is { Success: true } ? match.Groups[1].Value : null;
    }

    /// <summary>Source resolveCredentialForPrint. Bearer tokens are the stored OAuth access tokens that stay valid for the minimum expiry
    /// (PiSharp does not refresh them here).</summary>
    private static async Task<string> PrintAsync(Kind kind, string? cliProvider, string? cliModel, ModelRegistry registry,
        PiSharp.Cli.Authentication.AuthJsonCredentialStore? store, Dictionary<string, string> types, long minExpiry,
        PiSharp.Cli.Commands.LiveSessionRuntime runtime, CancellationToken token)
    {
        var providers = new List<(string Id, RegistryModel? Model)>();
        if (cliProvider is not null)
        {
            var id = registry.GetProviderIds().FirstOrDefault(provider => provider == cliProvider) ??
                throw new AuthCommandError($"Unknown provider \"{cliProvider}\". Use --list-models to see available providers.");
            if (cliModel is not null)
            {
                var resolved = ModelResolver.ResolveCliModel(id, cliModel, null, registry.GetAll(), registry.HasConfiguredAuth);
                if (resolved.Error is not null || resolved.Model is null) throw new AuthCommandError(resolved.Error ?? "Unable to resolve the requested provider/model");
                providers.Add((id, resolved.Model));
            }
            else providers.Add((id, null));
        }
        else
        {
            foreach (var id in registry.GetProviderIds())
            {
                if (!types.ContainsKey(id)) continue;
                var resolved = ModelResolver.ResolveCliModel(id, cliModel, null, registry.GetAll(), registry.HasConfiguredAuth);
                if (resolved.Model is not null && resolved.Error is null && resolved.Warning?.Contains("Using custom model id", StringComparison.Ordinal) != true)
                    providers.Add((id, resolved.Model));
            }
            if (providers.Count == 0) throw new AuthCommandError($"Model \"{cliModel}\" not found. Use --list-models to see available models.");
        }
        var found = new List<(string Provider, string Value)>();
        foreach (var (id, model) in providers)
        {
            types.TryGetValue(id, out var type);
            if (kind == Kind.ApiKey && type == "oauth") continue;
            if (kind == Kind.BearerToken && type != "oauth") continue;
            string? value;
            if (kind == Kind.BearerToken)
            {
                var stored = store is null ? null : await store.ReadAsync(id, token).ConfigureAwait(false);
                var now = (runtime.Time ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
                value = stored is not null && stored.ExpiresUnixMilliseconds - now >= minExpiry ? stored.Access : null;
            }
            else
            {
                var target = model ?? registry.GetAll().FirstOrDefault(entry => entry.Provider == id);
                value = target is null ? null : FromAuth(registry.ResolveRequestAuth(target, out _));
            }
            if (value is not null) found.Add((id, value));
        }
        if (found.Count == 1) return found[0].Value;
        if (found.Count == 0)
        {
            var first = providers.FirstOrDefault().Id;
            var type = first is null ? null : types.GetValueOrDefault(first);
            if (cliProvider is not null && kind == Kind.ApiKey && type == "oauth") throw new AuthCommandError($"Provider \"{first}\" is configured with OAuth, not an API key");
            if (cliProvider is not null && kind == Kind.BearerToken && type != "oauth") throw new AuthCommandError($"Provider \"{first}\" is not configured with an OAuth bearer token");
            throw new AuthCommandError($"No usable {(kind == Kind.ApiKey ? "API key" : "OAuth bearer token")} is configured");
        }
        throw new AuthCommandError($"Multiple configured providers matched ({string.Join(", ", found.Select(item => item.Provider))}). Specify --provider.");
    }
}
