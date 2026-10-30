// Decision 0011 (run W4, R1): registration and endpoints of the Playground. Program.cs calls AddPlaygroundArea after AddRunsArea and
// MapPlaygroundArea after MapRunsArea. The page itself is PlaygroundController's (GET /playground, and the fragments it swaps in); runs go
// through R0's /api/runs (the run engine: guarded by the Spend Guard, recorded in runs.db). The JSON endpoints here only read:
//   POST /api/playground/estimate   a RunSpec -> {valid, message, html, verdict, chip, over, pane, tokens, parts, refused}: the estimate by
//                                   Run (the Spend Guard's preflight; nothing reserved) and the request's input tokens.
//   POST /api/playground/code       a RunSpec -> View code {subtitle, csharp, curl, json, provider, notes} (no secret: placeholders);
//                                   ?run= the run the page shows adds inspectorUrl ("Open last run in inspector") when runs.db has it.
//   GET  /api/playground/model      ?provider=&model=&account= -> {capabilities, auth: {providerName, accountLabel, billing, source, tone,
//                                   offline}} for Model & auth (the account by its label only).
// Writes are JSON POSTs (HostGuard: same origin, JSON only); every answer is no-store; errors are {error: {code, message}} as /api/runs's.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Playground;

/// <summary>Registration and endpoints of the Playground.</summary>
public static class PlaygroundModule
{
    /// <summary>The route prefix of the JSON endpoints.</summary>
    public const string Prefix = "/api/playground";

    /// <summary>The estimate partial (also for Compare's "Run all" and the Tool Bench).</summary>
    public const string EstimatePartial = "/Views/Playground/_RunEstimate.cshtml";

    /// <summary>Registers the area's services. Call it after <c>AddRunsArea</c>.</summary>
    public static IServiceCollection AddPlaygroundArea(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton<PlaygroundPages>();
        return services;
    }

    /// <summary>Maps <c>/api/playground</c>.</summary>
    public static WebApplication MapPlaygroundArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });
        api.MapPost("/estimate", (JsonObject? body, PlaygroundPages pages, IServiceProvider services, CancellationToken token) =>
            Answer(async () => Json(await EstimateJsonAsync(pages, Spec(body), services, token).ConfigureAwait(false))));
        api.MapPost("/code", (JsonObject? body, string? run, HttpRequest request, PlaygroundPages pages) =>
            Answer(() => Task.FromResult(Json(pages.Code(Spec(body), $"{request.Scheme}://{request.Host}", run)))));
        api.MapGet("/model", (string? provider, string? model, string? account, PlaygroundPages pages, CancellationToken token) => Answer(async () =>
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Name the provider and the model (?provider=&model=).");
            var found = await pages.ModelAsync(provider.Trim(), model.Trim(), string.IsNullOrWhiteSpace(account) ? null : account.Trim(), token).ConfigureAwait(false);
            if (found is not { } info) return Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, $"There is no model {provider}/{model}.");
            var auth = info.Auth;
            return Json(new
            {
                capabilities = info.Capabilities,
                auth = auth is null ? null : new { auth.ProviderName, auth.AccountLabel, billing = auth.BillingText, auth.Source, auth.Tone, auth.Offline },
            });
        }));
        return app;
    }

    /// <summary>The estimate answer of <paramref name="spec"/>, with the chip's markup.</summary>
    public static async Task<JsonObject> EstimateJsonAsync(PlaygroundPages pages, RunSpec spec, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(services);
        var answer = await pages.DraftAsync(spec, cancellationToken).ConfigureAwait(false);
        var html = "";
        if (answer.Estimate.Shown && services.GetService<FragmentRenderer>() is { } renderer)
            html = await renderer.RenderAsync(EstimatePartial, answer.Estimate).ConfigureAwait(false);
        return new JsonObject
        {
            ["valid"] = answer.Valid,
            ["message"] = answer.Message,
            ["html"] = html,
            ["verdict"] = answer.Estimate.Verdict,
            ["chip"] = answer.Estimate.Chip,
            ["over"] = answer.Estimate.Over,
            ["pane"] = answer.Estimate.Pane,
            ["tokens"] = answer.Tokens,
            ["parts"] = answer.Parts is null ? null : JsonSerializer.SerializeToNode(answer.Parts, SettingsStore.JsonOptions),
            ["refused"] = JsonSerializer.SerializeToNode(answer.Refused, SettingsStore.JsonOptions),
        };
    }

    private static RunSpec Spec(JsonObject? body)
    {
        if (body is null) throw new ArgumentException("Send the run as a JSON object.");
        var spec = (JsonObject)body.DeepClone();
        spec.Remove("spend");
        return RunSpec.Parse(spec);
    }

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult Error(int status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    private static async Task<IResult> Answer(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
    }
}
