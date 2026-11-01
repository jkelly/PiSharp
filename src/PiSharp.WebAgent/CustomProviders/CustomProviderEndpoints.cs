// Decision 0011 (run W3, CP): the custom providers area's JSON API under /api/custom-providers, for Settings › Custom providers and its
// editor. Reads answer metadata and drafts (a stored key as its masked hint at most); a key goes in once, with a save or a test, and never
// comes back out, and no answer, log line or error repeats it. Writes are POSTs with JSON bodies (HostGuard's cross-origin and JSON-only
// rules cover them); every answer is Cache-Control: no-store; the export is the one download (models.json, redacted). Errors are {error}
// with 400 (invalid input), 404 (no such provider or override) or 409 (not possible now: models.json is invalid, the vault cannot be read).
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Credentials;

namespace PiSharp.WebAgent.CustomProviders;

/// <summary>Maps <c>/api/custom-providers</c>.</summary>
public static class CustomProviderEndpoints
{
    /// <summary>The route prefix.</summary>
    public const string Prefix = "/api/custom-providers";

    /// <summary>Maps the area's endpoints on <paramref name="routes"/>.</summary>
    public static IEndpointRouteBuilder MapCustomProviderEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapGet("/", (CustomProviderService service) => Run(() => Json(List(service))));
        api.MapGet("/export", (CustomProviderService service) => Run(() =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(service.Export()), "application/json", CustomProviderStore.FileName)));
        api.MapGet("/{id}", (string id, CustomProviderService service) =>
            Run(() => service.Draft(id) is { } draft ? Json(draft) : NotFound($"There is no custom provider {id}.")));
        api.MapPost("/", (CustomProviderDraft? body, CustomProviderService service, CancellationToken token) =>
            RunAsync(async () => Json(Row(service, await service.SaveAsync(Required(body), token).ConfigureAwait(false)))));
        api.MapPost("/preview", (CustomProviderDraft? body, CustomProviderService service) => Run(() => Json(service.Preview(Required(body)))));
        api.MapPost("/test", (CustomProviderDraft? body, CustomProviderTester tester, CancellationToken token) =>
            RunAsync(async () => Json(await tester.TestAsync(Required(body), token).ConfigureAwait(false))));
        api.MapPost("/import", (ImportBody? body, CustomProviderService service, CancellationToken token) =>
            RunAsync(async () => Json(await service.ImportAsync(Required(body).Text ?? "", token).ConfigureAwait(false))));
        api.MapPost("/variable", (VariableBody? body, CustomProviderService service) =>
            Run(() => CustomProviderJson.IsVariable(Required(body).Name?.Trim().TrimStart('$')) ? Json(new VariableState(service.IsSet(body!.Name!.Trim().TrimStart('$'))))
                : throw new ArgumentException("Enter a variable name: letters, digits and underscores.")));
        api.MapPost("/overrides", (OverrideBody? body, CustomProviderService service, CancellationToken token) =>
            RunAsync(async () =>
            {
                var request = Required(body);
                JsonObject? fields = null;
                if (request.Json is { } text)
                {
                    fields = CustomProviderJson.ParseObject(text, out var error) ?? throw new ArgumentException(error?.Replace("models.json", "The override") ?? "Enter a JSON object.");
                }
                var saved = await service.SaveOverrideAsync(request.Provider ?? "", request.Model ?? "", fields, request.Field, request.Value, token).ConfigureAwait(false);
                return Json(new OverrideSaved(saved is not null));
            }));
        api.MapPost("/overrides/remove", (OverrideBody? body, CustomProviderService service, CancellationToken token) =>
            RunAsync(async () => await service.RemoveOverrideAsync(Required(body).Provider ?? "", body!.Model ?? "", token).ConfigureAwait(false)
                ? Json(new OverrideSaved(false)) : NotFound("That model has no override.")));
        api.MapPost("/{id}/remove", (string id, CustomProviderService service, CancellationToken token) =>
            RunAsync(async () => Json(await service.RemoveAsync(id, token).ConfigureAwait(false))));
        api.MapPost("/{id}/duplicate", (string id, CustomProviderService service, CancellationToken token) =>
            RunAsync(async () => Json(Row(service, await service.DuplicateAsync(id, token).ConfigureAwait(false)))));
        api.MapPost("/{id}/test", (string id, CustomProviderTester tester, CancellationToken token) =>
            RunAsync(async () => Json(await tester.TestSavedAsync(id, token).ConfigureAwait(false))));
        return routes;
    }

    /// <summary>The list answer: the file's state and every provider's row.</summary>
    internal static CustomProviderList List(CustomProviderService service)
    {
        var snapshot = service.Current;
        return new CustomProviderList(snapshot.Path, snapshot.Exists, snapshot.Valid, snapshot.Error, snapshot.Saved, [.. snapshot.Providers.Select(provider => Row(service, provider))],
            snapshot.Overrides.Count);
    }

    /// <summary>One provider's row: metadata, how its key is set up (a masked hint at most), and its last test.</summary>
    internal static CustomProviderRowInfo Row(CustomProviderService service, CustomProviderInfo provider) =>
        new(provider, service.KeySource(provider), service.StoredKey(provider.Id)?.Hint, provider.KeyVariable is { } variable ? service.IsSet(variable) : null,
            service.LastTest(provider.Id));

    private static T Required<T>(T? body) where T : class => body ?? throw new ArgumentException("Send a JSON body.");

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, CredentialVault.JsonOptions, statusCode: status);

    private static IResult NotFound(string message) => Error(StatusCodes.Status404NotFound, message);

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
        InvalidOperationException or CredentialUnavailableException or InvalidDataException or IOException or UnauthorizedAccessException => StatusCodes.Status409Conflict,
        _ => null,
    };

    private sealed record ErrorBody(string Error);

    private sealed record VariableState(bool Set);

    private sealed record OverrideSaved(bool Exists);
}

/// <summary>GET /api/custom-providers.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Exists">Whether it exists.</param>
/// <param name="Valid">Whether it is valid (or missing).</param>
/// <param name="Error">Why it is not, or null.</param>
/// <param name="Saved">When it was last written.</param>
/// <param name="Providers">The custom providers.</param>
/// <param name="Overrides">How many model overrides it has.</param>
public sealed record CustomProviderList(string Path, bool Exists, bool Valid, string? Error, DateTimeOffset? Saved, IReadOnlyList<CustomProviderRowInfo> Providers, int Overrides);

/// <summary>A provider as the list answers it.</summary>
/// <param name="Provider">Its metadata.</param>
/// <param name="Key">Where its key comes from now.</param>
/// <param name="Hint">The stored key's masked hint, or null.</param>
/// <param name="VariableSet">Whether its key's variable is set on the web host (null: no variable).</param>
/// <param name="LastTest">Its last connection test since the app started, or null.</param>
public sealed record CustomProviderRowInfo(CustomProviderInfo Provider, CustomKeySource Key, string? Hint, bool? VariableSet, CustomProviderTest? LastTest);

/// <summary>POST /api/custom-providers/import: a models.json text. <see cref="ToString"/> never shows it (it may hold keys).</summary>
public sealed record ImportBody(string? Text)
{
    /// <inheritdoc/>
    public override string ToString() => "ImportBody [redacted]";
}

/// <summary>POST /api/custom-providers/variable.</summary>
public sealed record VariableBody(string? Name);

/// <summary>POST /api/custom-providers/overrides: the whole override as JSON text (<paramref name="Json"/>), or one field and its value.</summary>
public sealed record OverrideBody(string? Provider, string? Model, string? Json, string? Field, string? Value);
