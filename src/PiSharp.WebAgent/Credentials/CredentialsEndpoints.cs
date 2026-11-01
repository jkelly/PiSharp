// Decision 0011 (run W2, WU5): the credentials area's JSON API under /api/credentials, for the provider and account pages (WU9) and their
// modals. Reads answer accounts as metadata (a masked hint at most); a key goes in (add, replace, test before saving) and never comes back
// out, and no answer, log line or error repeats it. Writes are POSTs with JSON bodies, so HostGuard's cross-origin (403) and JSON-only
// (415) rules cover them; every answer is Cache-Control: no-store. Errors are {error} with 400 (invalid input), 404 (no such account or
// sign-in) or 409 (not possible now: a read-only account, offline, a sign-in of another identity, an unreadable credentials.json).
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace PiSharp.WebAgent.Credentials;

/// <summary>Maps <c>/api/credentials</c>.</summary>
public static class CredentialsEndpoints
{
    /// <summary>The route prefix.</summary>
    public const string Prefix = "/api/credentials";

    /// <summary>Maps the area's endpoints on <paramref name="routes"/>.</summary>
    public static IEndpointRouteBuilder MapCredentialsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapGet("/accounts", (string? provider, IAccountStore store) =>
            Run(() => Json(new AccountList(store.List(string.IsNullOrWhiteSpace(provider) ? null : provider.Trim())))));
        api.MapGet("/accounts/{id}", (string id, IAccountStore store) =>
            Run(() => store.Get(id) is { } account ? Json(account) : NotFound($"No account {id}.")));
        api.MapPost("/accounts", (AddKeyBody? body, IAccountStore store, CancellationToken token) =>
            RunAsync(async () =>
            {
                var request = new ApiKeyAccountRequest(Required(body).Provider ?? "", body!.ApiKey) { Label = body.Label, Settings = body.Settings };
                return Json(await store.AddApiKeyAsync(request, token).ConfigureAwait(false));
            }));
        api.MapPost("/accounts/{id}/key", (string id, ReplaceKeyBody? body, IAccountStore store, CancellationToken token) =>
            RunAsync(async () =>
            {
                if (body?.ApiKey is null && body?.Settings is null) throw new ArgumentException("Enter an API key.");
                return Json(await store.ReplaceApiKeyAsync(id, body.ApiKey, body.Settings, token).ConfigureAwait(false));
            }));
        api.MapPost("/accounts/{id}/rename", (string id, RenameBody? body, IAccountStore store, CancellationToken token) =>
            RunAsync(async () => Json(await store.RenameAsync(id, Required(body).Label ?? "", token).ConfigureAwait(false))));
        api.MapPost("/accounts/{id}/activate", (string id, IAccountStore store, CancellationToken token) =>
            RunAsync(async () => Json(await store.SetActiveAsync(id, token).ConfigureAwait(false))));
        api.MapPost("/accounts/{id}/remove", (string id, IAccountStore store, CancellationToken token) =>
            RunAsync(async () => Json(await store.RemoveAsync(id, token).ConfigureAwait(false))));
        api.MapPost("/accounts/{id}/test", (string id, IConnectionTester tester, CancellationToken token) =>
            RunAsync(async () => Json(await tester.TestAccountAsync(id, token).ConfigureAwait(false))));
        api.MapPost("/test", (TestKeyBody? body, IConnectionTester tester, CancellationToken token) =>
            RunAsync(async () => Json(await tester.TestKeyAsync(Required(body).Provider ?? "", body!.ApiKey ?? "", body.Settings, token).ConfigureAwait(false))));

        api.MapGet("/signin", (ISignInFlows flows) => Run(() => Json(new SignInMethodList(flows.Methods))));
        api.MapPost("/signin", (StartSignInBody? body, ISignInFlows flows, CancellationToken token) =>
            RunAsync(async () =>
            {
                var start = Required(body);
                var request = new SignInRequest(start.Provider ?? "", ParseMethod(start.Method))
                {
                    AccountId = string.IsNullOrWhiteSpace(start.AccountId) ? null : start.AccountId.Trim(), Label = start.Label, Domain = start.Domain,
                };
                return Json(await flows.StartAsync(request, token).ConfigureAwait(false));
            }));
        api.MapGet("/signin/{flowId}", (string flowId, ISignInFlows flows) =>
            Run(() => flows.Get(flowId) is { } status ? Json(status) : NotFound("That sign-in is over or unknown; start another.")));
        api.MapPost("/signin/{flowId}/code", (string flowId, SignInCodeBody? body, ISignInFlows flows, CancellationToken token) =>
            RunAsync(async () => Json(await flows.SubmitCodeAsync(flowId, Required(body).Code ?? "", token).ConfigureAwait(false))));
        api.MapPost("/signin/{flowId}/cancel", (string flowId, ISignInFlows flows) =>
            Run(() => flows.Cancel(flowId) is { } status ? Json(status) : NotFound("That sign-in is over or unknown.")));
        return routes;
    }

    /// <summary><c>browser</c>, <c>deviceCode</c> (or <c>device_code</c>), as <see cref="SignInMethod"/>.</summary>
    public static SignInMethod ParseMethod(string? text) => text?.Trim() switch
    {
        "browser" => SignInMethod.Browser,
        "deviceCode" or "device_code" => SignInMethod.DeviceCode,
        _ => throw new ArgumentException("Choose a sign-in method: browser or deviceCode."),
    };

    private static T Required<T>(T? body) where T : class => body ?? throw new ArgumentException("Send a JSON body.");

    private static IResult Json(object value, int status = StatusCodes.Status200OK) =>
        Results.Json(value, CredentialVault.JsonOptions, statusCode: status);

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
        InvalidOperationException or CredentialUnavailableException or InvalidDataException => StatusCodes.Status409Conflict,
        _ => null,
    };

    private sealed record AccountList(IReadOnlyList<AccountInfo> Accounts);

    private sealed record SignInMethodList(IReadOnlyList<SignInMethodInfo> Methods);

    private sealed record ErrorBody(string Error);
}

/// <summary>POST /api/credentials/accounts. <see cref="ToString"/> never shows the key.</summary>
public sealed record AddKeyBody(string? Provider, string? ApiKey, string? Label, Dictionary<string, string>? Settings)
{
    /// <inheritdoc/>
    public override string ToString() => $"AddKeyBody ({Provider}) [redacted]";
}

/// <summary>POST /api/credentials/accounts/{id}/key: a new key, new settings, or both. <see cref="ToString"/> never shows the key.</summary>
public sealed record ReplaceKeyBody(string? ApiKey, Dictionary<string, string>? Settings)
{
    /// <inheritdoc/>
    public override string ToString() => "ReplaceKeyBody [redacted]";
}

/// <summary>POST /api/credentials/accounts/{id}/rename.</summary>
public sealed record RenameBody(string? Label);

/// <summary>POST /api/credentials/test: a key tested before it is saved. <see cref="ToString"/> never shows the key.</summary>
public sealed record TestKeyBody(string? Provider, string? ApiKey, Dictionary<string, string>? Settings)
{
    /// <inheritdoc/>
    public override string ToString() => $"TestKeyBody ({Provider}) [redacted]";
}

/// <summary>POST /api/credentials/signin.</summary>
/// <param name="Provider"><c>openai-codex</c> or <c>github-copilot</c>.</param>
/// <param name="Method"><c>browser</c> or <c>deviceCode</c>.</param>
/// <param name="AccountId">The account to sign in again, or null.</param>
/// <param name="Label">The new account's name, or null.</param>
/// <param name="Domain">A GitHub Enterprise domain, or null.</param>
public sealed record StartSignInBody(string? Provider, string? Method, string? AccountId, string? Label, string? Domain);

/// <summary>POST /api/credentials/signin/{flowId}/code: the pasted authorization code or redirect URL. <see cref="ToString"/> never shows it.</summary>
public sealed record SignInCodeBody(string? Code)
{
    /// <inheritdoc/>
    public override string ToString() => "SignInCodeBody [redacted]";
}
