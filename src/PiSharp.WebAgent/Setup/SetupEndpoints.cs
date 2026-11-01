// Decision 0011 (run W3, SU): the wizard's JSON API under /api/setup. The wizard connects providers through the credentials area's own
// /api/credentials (the API key and ChatGPT sign-in modals are the Settings area's partials and scripts), so only what is the wizard's own is
// here: its state, Skip setup and Save and continue (the default model, saved through ISettingsStore's Defaults section, then the outcome).
// Writes are POSTs with JSON bodies, so HostGuard's cross-origin (403) and JSON-only (415) rules cover them; every answer is
// Cache-Control: no-store; errors are {error} with 400 (invalid input) or 409 (nothing connected yet), as in the other areas. Answers are in
// SettingsStore.JsonOptions (camelCase, enums as camelCase strings) and hold provider ids and setting values only, never a secret.
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Setup;

/// <summary>Maps <c>/api/setup</c>.</summary>
public static class SetupEndpoints
{
    /// <summary>The route prefix.</summary>
    public const string Prefix = "/api/setup";

    /// <summary>Maps the area's endpoints on <paramref name="routes"/>.</summary>
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapGet("", (SetupState state) => Json(Status(state)));
        api.MapPost("/skip", (SetupState state, CancellationToken token) =>
            RunAsync(async () => Json(new SkipAnswer(await state.SkipAsync(token).ConfigureAwait(false)))));
        api.MapPost("/finish", (FinishBody? body, SetupState state, CancellationToken token) =>
            RunAsync(async () =>
            {
                var wanted = body ?? throw new ArgumentException("Send a JSON body.");
                var finished = await state.FinishAsync(new SetupChoice(wanted.Provider, wanted.Model, wanted.Thinking, wanted.UseAsDefault ?? true), token)
                    .ConfigureAwait(false);
                return Json(finished);
            }));
        return routes;
    }

    /// <summary>The wizard's state as <c>GET /api/setup</c> answers it.</summary>
    public static SetupStatus Status(SetupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var setup = state.Current;
        var connected = state.Connected();
        return new SetupStatus(setup.Outcome, setup.At, state.Offline, !state.Offline && !setup.Done && connected.Count == 0,
            [.. connected.Select(entry => entry.Id)]);
    }

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (ArgumentException error) { return Json(new ErrorBody(SecretText.Scrub(error.Message)), StatusCodes.Status400BadRequest); }
        catch (InvalidOperationException error) { return Json(new ErrorBody(SecretText.Scrub(error.Message)), StatusCodes.Status409Conflict); }
    }

    private sealed record SkipAnswer(SetupSettings Setup);

    private sealed record ErrorBody(string Error);
}

/// <summary><c>GET /api/setup</c>.</summary>
/// <param name="Outcome">How the wizard ended (<c>finished</c>, <c>skipped</c>), or null when it never did.</param>
/// <param name="At">When it ended (UTC), or null.</param>
/// <param name="Offline">Whether the app runs on faux models (the wizard is never forced).</param>
/// <param name="RedirectsToSetup">Whether <c>/</c> and <c>/chat/new</c> send the user to <c>/setup</c> now.</param>
/// <param name="Connected">The ids of the connected providers.</param>
public sealed record SetupStatus(SetupOutcome? Outcome, DateTimeOffset? At, bool Offline, bool RedirectsToSetup, IReadOnlyList<string> Connected);

/// <summary><c>POST /api/setup/finish</c>: the wizard's Default model step.</summary>
/// <param name="Provider">The default provider, or null to leave the defaults as they are.</param>
/// <param name="Model">The default model of <paramref name="Provider"/>.</param>
/// <param name="Thinking">The default thinking level, or null for the model's default; clamped to the model's levels.</param>
/// <param name="UseAsDefault">Whether to save them as the defaults (the "Use as default in Chat and Playground" switch); default true.</param>
public sealed record FinishBody(string? Provider, string? Model, string? Thinking, bool? UseAsDefault);
