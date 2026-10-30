// Decision 0011 (run W2, WU9): the settings pages' JSON API under /api/settings, beside the credentials area's /api/credentials (WU5), which
// the provider and account pages use for every key and sign-in. Here: the Defaults, Tools and Models sections of ISettingsStore (read, save,
// reset), model visibility, "forget a provider" (its stored keys and sign-ins and its hidden models; environment variables stay) and the
// folder browser. Writes are POSTs with JSON bodies, so HostGuard's cross-origin (403) and JSON-only (415) rules cover them; every answer is
// Cache-Control: no-store; errors are {error} with 400 (invalid input), 404 (no such provider) or 409 (not possible now), as in WU5. A
// section is answered in SettingsStore.JsonOptions (camelCase, enums as camelCase strings). No answer holds a secret.
using PiSharp.WebAgent.AgentCompare;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Settings;

/// <summary>Maps <c>/api/settings</c>.</summary>
public static class SettingsEndpoints
{
    /// <summary>The route prefix.</summary>
    public const string Prefix = "/api/settings";

    /// <summary>The most allow-list entries <c>POST /api/settings/tools</c> takes.</summary>
    public const int MaximumAllowRules = 200;

    /// <summary>The longest bash command pattern.</summary>
    public const int MaximumCommandLength = 512;

    /// <summary>The tools the allow-list editor offers (the web-approval extension asks for these; read-only tools never ask).</summary>
    public static IReadOnlyList<string> ApprovalTools { get; } = ["bash", "edit", "write"];

    /// <summary>Maps the area's endpoints on <paramref name="routes"/>.</summary>
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapGet("/defaults", (ISettingsStore store) => Json(store.Get<DefaultsSettings>()));
        api.MapPost("/defaults", (DefaultsBody? body, ISettingsStore store, IModelCatalog catalog, CancellationToken token) =>
            RunAsync(async () => Json(await store.SaveAsync(CheckDefaults(Required(body), catalog), token).ConfigureAwait(false))));
        api.MapPost("/defaults/reset", (ISettingsStore store, CancellationToken token) =>
            RunAsync(async () => Json(await store.ResetAsync<DefaultsSettings>(token).ConfigureAwait(false))));

        api.MapGet("/tools", (ISettingsStore store) => Json(store.Get<ToolsSettings>()));
        api.MapPost("/tools", (ToolsBody? body, ISettingsStore store, CancellationToken token) =>
            RunAsync(async () => Json(await store.SaveAsync(CheckTools(Required(body)), token).ConfigureAwait(false))));
        api.MapPost("/tools/reset", (ISettingsStore store, CancellationToken token) =>
            RunAsync(async () => Json(await store.ResetAsync<ToolsSettings>(token).ConfigureAwait(false))));

        // Run W4, R6: agent compare's defaults (Settings › Tools, _AgentCompareDefaults). A field the body leaves out keeps its stored value.
        api.MapGet("/agent-compare", (ISettingsStore store) => Json(store.Get<AgentCompareSettings>()));
        api.MapPost("/agent-compare", (AgentCompareBody? body, ISettingsStore store, CancellationToken token) =>
            RunAsync(async () =>
            {
                var request = Required(body);
                return Json(await store.UpdateAsync<AgentCompareSettings>(current => CheckAgentCompare(request, current), token).ConfigureAwait(false));
            }));
        api.MapPost("/agent-compare/reset", (ISettingsStore store, CancellationToken token) =>
            RunAsync(async () => Json(await store.ResetAsync<AgentCompareSettings>(token).ConfigureAwait(false))));

        api.MapGet("/models", (ISettingsStore store) => Json(store.Get<ModelsSettings>()));
        api.MapPost("/models/visibility", (VisibilityBody? body, ISettingsStore store, IModelCatalog catalog, CancellationToken token) =>
            RunAsync(async () =>
            {
                var keys = VisibilityKeys(Required(body), catalog);
                var saved = await store.UpdateAsync<ModelsSettings>(current => SetHidden(current, keys, body!.Hidden), token).ConfigureAwait(false);
                return Json(new VisibilityAnswer(saved.Hidden ?? [], keys.Count));
            }));

        api.MapPost("/providers/{id}/forget", (string id, ProviderDirectory directory, IAccountStore accounts, ISettingsStore store, CancellationToken token) =>
            RunAsync(async () =>
            {
                var entry = directory.Find(id) ?? throw new KeyNotFoundException($"No provider {id}.");
                var removed = 0;
                foreach (var account in entry.StoredAccounts)
                {
                    await accounts.RemoveAsync(account.Id, token).ConfigureAwait(false);
                    removed++;
                }
                var prefix = entry.Id + "/";
                var unhidden = 0;
                await store.UpdateAsync<ModelsSettings>(current =>
                {
                    var keep = (current.Hidden ?? []).Where(key => !key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                    unhidden = (current.Hidden?.Count ?? 0) - keep.Count;
                    return new ModelsSettings(keep);
                }, token).ConfigureAwait(false);
                return Json(new ForgetAnswer(entry.Id, removed, unhidden));
            }));

        api.MapGet("/folders", (string? path) => Run(() => Json(FolderBrowser.List(path))));
        return routes;
    }

    /// <summary>The defaults <paramref name="body"/> asks for, checked against the catalog (the model must exist).</summary>
    /// <exception cref="ArgumentException">Only one of provider and model, an unknown model, or an unknown thinking level.</exception>
    public static DefaultsSettings CheckDefaults(DefaultsBody body, IModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(catalog);
        var settings = new DefaultsSettings(body.Provider, body.Model, body.Thinking).Normalize();
        if (settings.Provider is { } provider && catalog.FindModel(provider, settings.Model!) is null)
            throw new ArgumentException($"There is no model {settings.Model} of {provider} in the catalog.");
        return settings;
    }

    /// <summary>The tools settings <paramref name="body"/> asks for: an existing folder, a known mode and a clean allow-list.</summary>
    /// <exception cref="ArgumentException">A missing or relative folder, an unknown mode, or an invalid allow-list entry.</exception>
    public static ToolsSettings CheckTools(ToolsBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var directory = string.IsNullOrWhiteSpace(body.WorkingDirectory) ? null : body.WorkingDirectory.Trim();
        if (directory is not null)
        {
            if (!Path.IsPathFullyQualified(directory))
                throw new ArgumentException($"The working directory must be a full path, such as {ToolsSettings.DefaultWorkingDirectory}.");
            if (!FolderBrowser.Exists(directory)) throw new ArgumentException($"There is no folder {directory}.");
        }
        var mode = ParseMode(body.ApprovalMode);
        var rules = body.AllowList ?? [];
        if (rules.Count > MaximumAllowRules) throw new ArgumentException($"The allow-list holds at most {MaximumAllowRules} entries.");
        var checkedRules = new List<ToolAllowRule>();
        foreach (var rule in rules)
        {
            var tool = rule?.Tool?.Trim() ?? "";
            if (tool.Length == 0 || tool.Length > 64 || !tool.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-'))
                throw new ArgumentException("Every allow-list entry needs a tool name, such as bash.");
            var command = string.IsNullOrWhiteSpace(rule!.Command) ? null : rule.Command.Trim();
            if (command is not null)
            {
                if (tool != "bash") throw new ArgumentException($"Only bash entries take a command pattern; a {tool} entry allows every {tool} call.");
                if (command.Length > MaximumCommandLength) throw new ArgumentException($"A command pattern is at most {MaximumCommandLength} characters.");
                if (command.Any(char.IsControl)) throw new ArgumentException("A command pattern is one line.");
            }
            checkedRules.Add(new ToolAllowRule(tool, command));
        }
        return new ToolsSettings(directory, mode, checkedRules).Normalize();
    }

    /// <summary>The agent compare defaults <paramref name="body"/> asks for, on top of <paramref name="current"/> (run W4, R6): a field it leaves
    /// out keeps its value; the result is normalized (AgentCompareSettings.Normalize checks the ranges and the excludes).</summary>
    /// <exception cref="ArgumentException">An unknown isolation or approval mode, or a value out of range.</exception>
    public static AgentCompareSettings CheckAgentCompare(AgentCompareBody body, AgentCompareSettings current)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(current);
        var isolation = body.Isolation is null ? current.Isolation : body.Isolation.Trim().ToLowerInvariant() switch
        {
            "automatic" or "auto" => WorkspaceIsolation.Automatic,
            "copy" => WorkspaceIsolation.Copy,
            "worktree" => WorkspaceIsolation.Worktree,
            _ => throw new ArgumentException("Choose an isolation: automatic or copy."),
        };
        if (body.CopyWarnGb is { } gb && (!double.IsFinite(gb) || gb < 0.1 || gb > 1000)) throw new ArgumentException("Warn above 0.1 to 1,000 GB.");
        var excludes = body.ExtraExcludes is null ? current.ExtraExcludes
            : AgentCompareSettings.NormalizeExcludes(body.ExtraExcludes.SelectMany(item => (item ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        return (current with
        {
            Isolation = isolation,
            ExtraExcludes = excludes,
            UseGitignore = body.UseGitignore ?? current.UseGitignore,
            CopyWarnGb = body.CopyWarnGb ?? current.CopyWarnGb,
            MaxTurns = body.MaxTurns ?? current.MaxTurns,
            MaxMinutes = body.MaxMinutes ?? current.MaxMinutes,
            Approval = body.Approval is null ? current.Approval : ParseMode(body.Approval),
            CheckTimeoutMinutes = body.CheckTimeoutMinutes ?? current.CheckTimeoutMinutes,
        }).Normalize();
    }

    /// <summary><c>ask</c>, <c>allowList</c> (or <c>allow-list</c>), <c>auto</c> as <see cref="ToolApprovalMode"/>; blank is
    /// <see cref="ToolApprovalMode.Ask"/>.</summary>
    /// <exception cref="ArgumentException">Another word.</exception>
    public static ToolApprovalMode ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "ask" => ToolApprovalMode.Ask,
        "allowlist" or "allow-list" => ToolApprovalMode.AllowList,
        "auto" => ToolApprovalMode.Auto,
        _ => throw new ArgumentException("Choose an approval mode: ask, allowList or auto."),
    };

    /// <summary>The <c>provider/model</c> keys a visibility change names: its <c>models</c>, or every model of its <c>provider</c>.</summary>
    /// <exception cref="ArgumentException">Neither, or a model the catalog does not list.</exception>
    public static IReadOnlyList<string> VisibilityKeys(VisibilityBody body, IModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(catalog);
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(body.Provider))
        {
            var provider = body.Provider.Trim();
            if (catalog.FindProvider(provider) is null) throw new ArgumentException($"The catalog lists no provider {provider}.");
            keys.AddRange(catalog.Models(provider).Select(model => ModelsSettings.Key(provider, model.Id)));
        }
        foreach (var key in body.Models ?? [])
        {
            var trimmed = key?.Trim() ?? "";
            var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || catalog.FindModel(trimmed[..slash], trimmed[(slash + 1)..]) is null)
                throw new ArgumentException($"“{(trimmed.Length > 80 ? trimmed[..79] + "…" : trimmed)}” is not a catalog model (provider/model).");
            if (!keys.Contains(trimmed, StringComparer.Ordinal)) keys.Add(trimmed);
        }
        if (keys.Count == 0) throw new ArgumentException("Name the models (provider/model) or a provider.");
        return keys;
    }

    private static ModelsSettings SetHidden(ModelsSettings current, IReadOnlyList<string> keys, bool hidden)
    {
        var list = (current.Hidden ?? []).ToList();
        if (hidden) list.AddRange(keys.Where(key => !list.Contains(key, StringComparer.Ordinal)));
        else list.RemoveAll(key => keys.Contains(key, StringComparer.Ordinal));
        return new ModelsSettings(list);
    }

    private static T Required<T>(T? body) where T : class => body ?? throw new ArgumentException("Send a JSON body.");

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult Error(int status, string message) => Json(new ErrorBody(SecretText.Scrub(message)), status);

    private static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (Exception error) when (Status(error) is { } status) { return Error(status, error.Message); }
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) when (Status(error) is { } status) { return Error(status, error.Message); }
    }

    private static int? Status(Exception error) => error switch
    {
        ArgumentException => StatusCodes.Status400BadRequest,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        InvalidOperationException or CredentialUnavailableException or InvalidDataException => StatusCodes.Status409Conflict,
        _ => null,
    };

    private sealed record VisibilityAnswer(IReadOnlyList<string> Hidden, int Changed);

    private sealed record ForgetAnswer(string Provider, int RemovedAccounts, int ShownModels);

    private sealed record ErrorBody(string Error);
}

/// <summary>POST /api/settings/defaults: both or neither of provider and model; thinking is a level or null (the model's default).</summary>
public sealed record DefaultsBody(string? Provider, string? Model, string? Thinking);

/// <summary>POST /api/settings/tools.</summary>
/// <param name="WorkingDirectory">An existing folder as a full path, or null for the user's home.</param>
/// <param name="ApprovalMode"><c>ask</c>, <c>allowList</c> or <c>auto</c>.</param>
/// <param name="AllowList">The allow-list: <c>{tool, command?}</c> entries; a command pattern only for bash.</param>
public sealed record ToolsBody(string? WorkingDirectory, string? ApprovalMode, List<AllowRuleBody?>? AllowList);

/// <summary>An allow-list entry of <see cref="ToolsBody"/>.</summary>
public sealed record AllowRuleBody(string? Tool, string? Command);

/// <summary>POST /api/settings/models/visibility: hide or show <c>models</c> (<c>provider/model</c> keys) or every model of <c>provider</c>.</summary>
public sealed record VisibilityBody(List<string?>? Models, string? Provider, bool Hidden);

/// <summary>POST /api/settings/agent-compare (run W4, R6): agent compare's defaults; a field left out (null) keeps its stored value.</summary>
/// <param name="Isolation"><c>automatic</c> or <c>copy</c> (<c>worktree</c> is accepted too).</param>
/// <param name="ExtraExcludes">Folders or patterns a copy also skips (each entry may hold several, comma-separated).</param>
/// <param name="UseGitignore">Whether a copy also skips .gitignore patterns.</param>
/// <param name="CopyWarnGb">A copy larger than this many GB asks first (0.1 to 1,000).</param>
/// <param name="MaxTurns">Turns per model (1 to 200).</param>
/// <param name="MaxMinutes">Minutes per model (1 to 240).</param>
/// <param name="Approval"><c>ask</c>, <c>allowList</c> or <c>auto</c>.</param>
/// <param name="CheckTimeoutMinutes">The check command's timeout in minutes (1 to 240).</param>
public sealed record AgentCompareBody(string? Isolation = null, List<string?>? ExtraExcludes = null, bool? UseGitignore = null, double? CopyWarnGb = null,
    int? MaxTurns = null, int? MaxMinutes = null, string? Approval = null, int? CheckTimeoutMinutes = null);
