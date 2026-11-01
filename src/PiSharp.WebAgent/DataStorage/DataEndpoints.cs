// Decision 0011 (run W4, unit R9): Settings › Data & storage's JSON API under /api/data. Writes are POSTs with JSON bodies, so HostGuard's
// cross-origin (403) and JSON-only (415) rules cover them; the two downloads (the settings file and the chats zip) are GETs with no side
// effect. Every answer is Cache-Control: no-store; errors are {error} with 400 (invalid input), 404 (nothing there), 409 (not possible now)
// or 413 (an import file over 4 MB), and their messages are scrubbed. The danger zone's four actions take {confirm: "DELETE"}, checked here
// as on the page. No answer holds a secret: the settings file never has a credential, and the import review shows custom providers redacted.
using System.Text.Json.Nodes;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>POST /api/data/open.</summary>
/// <param name="Target">One of <see cref="DataOpenTargets.All"/>.</param>
/// <param name="Run">The run, for <c>workspace</c>.</param>
/// <param name="Slot">The model's slot, for <c>workspace</c>.</param>
public sealed record DataOpenBody(string? Target, string? Run = null, string? Slot = null);

/// <summary>POST /api/data/history: run history's settings (a missing switch keeps its value; a null retention keeps runs forever).</summary>
public sealed record DataHistoryBody(int? RetentionDays, bool? StoreRawPayloads, bool? StoreSseEvents);

/// <summary>POST /api/data/workspaces/remove: a run's workspace (or, without a slot, all of the run's).</summary>
public sealed record DataWorkspaceBody(string? Run, string? Slot = null);

/// <summary>POST /api/data/import/preview and /api/data/import: the settings file (parsed), and for the import the ids of the items kept.</summary>
public sealed record DataImportBody(JsonObject? File, List<string>? Accepted = null);

/// <summary>POST /api/data/danger/{action}: the typed confirmation.</summary>
public sealed record DataConfirmBody(string? Confirm);

/// <summary>Maps <c>/api/data</c>.</summary>
public static class DataEndpoints
{
    /// <summary>The route prefix.</summary>
    public const string Prefix = "/api/data";

    /// <summary>The danger zone's actions.</summary>
    public static IReadOnlyList<string> DangerActions { get; } = ["credentials", "history", "chats", "settings"];

    /// <summary>Maps the area's endpoints on <paramref name="routes"/>.</summary>
    public static IEndpointRouteBuilder MapDataEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapGet("/facts", (DataStorageService data, CancellationToken token) => RunAsync(async () => Json(await data.PageAsync(token).ConfigureAwait(false))));

        api.MapPost("/open", (DataOpenBody? body, DataStorageService data, IFolderOpener opener, CancellationToken token) => RunAsync(async () =>
        {
            var request = Required(body);
            var folder = await data.OpenAsync(request.Target, request.Run, request.Slot, token).ConfigureAwait(false);
            return Json(new OpenAnswer(folder, opener.Name));
        }));

        api.MapPost("/history", (DataHistoryBody? body, DataStorageService data, Contracts.ISettingsStore store, CancellationToken token) => RunAsync(async () =>
        {
            var request = Required(body);
            var current = store.Get<RunHistorySettings>();
            var next = new RunHistorySettings(request.RetentionDays, request.StoreRawPayloads ?? current.StoreRawPayloads, request.StoreSseEvents ?? current.StoreSseEvents);
            return Json(await data.SaveHistoryAsync(next, token).ConfigureAwait(false));
        }));

        api.MapPost("/workspaces/remove", (DataWorkspaceBody? body, DataStorageService data, CancellationToken token) => RunAsync(async () =>
        {
            var request = Required(body);
            return Json(await data.RemoveWorkspaceAsync(request.Run, request.Slot, token).ConfigureAwait(false));
        }));

        api.MapPost("/workspaces/cleanup", (DataStorageService data, CancellationToken token) =>
            RunAsync(async () => Json(await data.CleanUpWorkspacesAsync(token).ConfigureAwait(false))));

        api.MapGet("/export", (DataStorageService data) => Run(() =>
        {
            var file = data.ExportFile();
            return Results.File(System.Text.Encoding.UTF8.GetBytes(file.Text), "application/json", file.FileName);
        }));

        api.MapGet("/export/chats", (DataStorageService data, CancellationToken token) => RunAsync(async () =>
        {
            var (zip, name, _) = await data.ExportChatsAsync(token).ConfigureAwait(false);
            return Results.File(zip, "application/zip", name);
        }));

        api.MapPost("/import/preview", (HttpContext http, DataImportBody? body, DataStorageService data) => Run(() =>
        {
            TooLarge(http);
            return Json(data.Export.Plan(Required(body).File));
        }));

        api.MapPost("/import", (HttpContext http, DataImportBody? body, DataStorageService data, CancellationToken token) => RunAsync(async () =>
        {
            TooLarge(http);
            var request = Required(body);
            if (request.Accepted is not { Count: > 0 } accepted) throw new ArgumentException("Choose at least one change to import.");
            return Json(await data.Export.ApplyAsync(request.File, accepted, token).ConfigureAwait(false));
        }));

        api.MapPost("/danger/{action}", (string action, DataConfirmBody? body, DataStorageService data, CancellationToken token) => RunAsync(async () =>
        {
            var confirm = Required(body).Confirm;
            var result = action switch
            {
                "credentials" => await data.DeleteCredentialsAsync(confirm, token).ConfigureAwait(false),
                "history" => await data.ClearHistoryAsync(confirm, token).ConfigureAwait(false),
                "chats" => await data.DeleteChatsAsync(confirm, token).ConfigureAwait(false),
                "settings" => await data.ResetSettingsAsync(confirm, token).ConfigureAwait(false),
                _ => throw new KeyNotFoundException("There is no such action. Choose " + string.Join(", ", DangerActions) + "."),
            };
            return Json(result);
        }));
        return routes;
    }

    private static void TooLarge(HttpContext http)
    {
        if (http.Request.ContentLength is { } length && length > DataStorageOptions.MaxImportBytes + 64 * 1024)
            throw new FileTooLargeException($"That file is too large to import ({DataStorageOptions.MaxImportBytes / (1024 * 1024)} MB at most).");
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
        FileTooLargeException => StatusCodes.Status413PayloadTooLarge,
        AgentCompareException compare => compare.Status,
        ChatException chat => chat.Status,
        WorkspaceException { Code: WorkspaceException.NotFound } => StatusCodes.Status404NotFound,
        WorkspaceException { Code: WorkspaceException.Invalid } => StatusCodes.Status400BadRequest,
        WorkspaceException => StatusCodes.Status409Conflict,
        ArgumentException => StatusCodes.Status400BadRequest,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException => StatusCodes.Status409Conflict,
        _ => null,
    };

    private sealed class FileTooLargeException(string message) : Exception(message);

    private sealed record OpenAnswer(string Folder, string Opener);

    private sealed record ErrorBody(string Error);
}
