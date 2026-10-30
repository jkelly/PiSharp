// Decision 0011 (run W5, E7): Pin as test case's JSON API, under /api/evals/pins (EvalRoutes.ApiPins). Writes are JSON POSTs (HostGuard's
// same-origin and JSON-only rules cover them) and idempotent: the modal sends the case id (and a new suite's id) it was opened with.
//
//   GET  /api/evals/pins/modal      ?source&run | &chat&agent?&entry | &suite | &case: _TestCaseModal's content (PinsController); a pin that
//                                   can't be made is a 200 fragment that says why (data-pin-refused); 404 for a run, chat, suite or case
//                                   that does not exist
//   POST /api/evals/pins            {target: {source, run? | chat, agent?, entry? | suite? | case?}, caseId, name?, tags?, suiteId? |
//                                   newSuite: {id, name}, checks?, systemPrompt?, prompt?, maxTokens?} -> {created, case, suite: {id, name,
//                                   paused}, suiteCreated, url, toast: {title, body, link}}
//   POST /api/evals/pins/checks     {target, add: {kind, value}} -> {item: {check, label, code, summary, limit, verdict, detail}}: a check
//                                   from the quick Add check panel, with what it makes of the reference output
// Errors are {error: {code, message}}: 400 invalid, 404 not_found, 409 conflict (a pin that can't be made says why). Every answer is
// Cache-Control: no-store, written with EvalJson.Options.
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Pins;

/// <summary>Maps <c>/api/evals/pins</c>.</summary>
public static class PinEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    public static IEndpointRouteBuilder MapPinEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(EvalRoutes.ApiPins);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });
        api.MapPost("", (JsonObject? body, PinService pins, CancellationToken cancellationToken) => HandleAsync(async () =>
        {
            var saved = await pins.SaveAsync(PinSaveRequest.FromJson(body), cancellationToken).ConfigureAwait(false);
            return Json(SavedJson(saved));
        }));
        api.MapPost("/checks", (JsonObject? body, PinService pins, CancellationToken cancellationToken) => HandleAsync(async () =>
        {
            if (body is null) throw new ArgumentException("Send a JSON body.");
            var target = PinTarget.FromJson(body["target"] as JsonObject);
            var add = body["add"] as JsonObject ?? throw new ArgumentException("Say which check to add.");
            var check = pins.QuickCheck(Text(add, "kind"), Text(add, "value"));
            var row = await pins.CheckRowAsync(target, check, cancellationToken).ConfigureAwait(false);
            return Json(new JsonObject { ["item"] = RowJson(row) });
        }));
        return routes;
    }

    /// <summary>A save's answer.</summary>
    public static JsonObject SavedJson(PinSaved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var url = EvalRoutes.Suite(saved.Suite.Id);
        return new JsonObject
        {
            ["created"] = saved.Created,
            ["case"] = EvalJson.ToNode(saved.Case),
            ["suite"] = new JsonObject
            {
                ["id"] = saved.Suite.Id,
                ["name"] = saved.Suite.Name,
                ["paused"] = saved.Suite.Schedule is { Enabled: true, Paused: not null },
            },
            ["suiteCreated"] = saved.SuiteCreated,
            ["url"] = url,
            ["toast"] = new JsonObject { ["title"] = "Test case saved", ["body"] = saved.Toast, ["link"] = url },
        };
    }

    /// <summary>A check row as JSON (pin.js builds the row from it with textContent).</summary>
    public static JsonObject RowJson(PinCheckRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new JsonObject
        {
            ["check"] = JsonNode.Parse(row.Json),
            ["label"] = row.Label,
            ["code"] = row.Code,
            ["summary"] = row.Summary,
            ["limit"] = row.Limit,
            ["verdict"] = row.Verdict,
            ["detail"] = row.Detail,
        };
    }

    private static string? Text(JsonObject json, string key) => json[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new ArgumentException($"{key} is text."),
    };

    private static IResult Json(JsonNode value, int status = StatusCodes.Status200OK) => Results.Json(value, EvalJson.Options, statusCode: status);

    private static IResult Error(int status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (KeyNotFoundException error)
        {
            return Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, error.Message);
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
        catch (PinRefusedException error)
        {
            return Error(StatusCodes.Status409Conflict, SpendErrors.Conflict, error.Message);
        }
        catch (InvalidOperationException error)
        {
            return Error(StatusCodes.Status409Conflict, SpendErrors.Conflict, error.Message);
        }
        catch (ChatException error)
        {
            return Error(error.Status, error.Code, error.Message);
        }
    }
}
