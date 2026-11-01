// Decision 0011 (run W4, R8): registration and endpoints of Presets. Program.cs calls AddPresetsArea after the Tool Bench's and
// MapPresetsArea after MapBenchArea. The pages are PresetsController's (GET /presets, /presets/grid, /presets/{id}/edit, /presets/picker);
// the JSON endpoints (writes are same-origin JSON POSTs through HostGuard; every answer is no-store; errors are {error: {code, message}}):
//   GET  /api/presets                     {presets: [{id, name, kind, model, tags, uses, lastUsed, pinned, parts}]}
//   GET  /api/presets/{id}                {preset}
//   POST /api/presets/preview             {page, capture, origin?} -> {valid, message, html} (the save modal's content for the page's request)
//   POST /api/presets                     {page, capture, name, description, tags, pinned, include, update?} -> {preset, created}
//   POST /api/presets/{id}                {name, description, tags, pinned, include} -> {preset} (the edit pane)
//   POST /api/presets/{id}/duplicate      -> {preset}
//   POST /api/presets/{id}/delete         -> {deleted}
//   GET  /api/presets/{id}/load?page=     {id, name, page, parts, notes, available, problem, connectUrl, connectLabel, payload} (nothing counted)
//   POST /api/presets/{id}/used           {page} -> {id, uses, lastUsed}
//   GET  /api/presets/{id}/export         the preset's file ({id}.preset.json, an attachment)
//   GET  /api/presets/export              every preset's file (pisharp-presets-YYYY-MM-DD.json, an attachment)
//   POST /api/presets/import              {text} -> {imported: [{id, name}], skipped: [ids], renamed: [{from, to}]}; 400 and nothing saved when
//                                         the file is not valid JSON or a preset in it is not valid.
// No answer carries a credential: a preset holds none (accounts are ids).
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.DataStorage;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Presets;

/// <summary>Registration and endpoints of Presets.</summary>
public static class PresetsModule
{
    /// <summary>The route prefix of the JSON endpoints.</summary>
    public const string Prefix = "/api/presets";

    /// <summary>The shared save modal.</summary>
    public const string SaveModalPartial = "~/Views/Shared/_SavePresetModal.cshtml";

    /// <summary>The shared load modal.</summary>
    public const string LoadModalPartial = "~/Views/Shared/_LoadPresetModal.cshtml";

    /// <summary>Registers the area's services, its webagent.db table and its export part.</summary>
    public static IServiceCollection AddPresetsArea(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddWebAgentMigration(PresetStore.Migration);
        services.TryAddSingleton(provider => new PresetStore(provider.GetRequiredService<WebAgentDb>(), provider.GetService<ILogger<PresetStore>>(), provider.GetService<TimeProvider>()));
        services.TryAddSingleton(provider => new PresetService(provider.GetRequiredService<PresetStore>(), provider, provider.GetService<TimeProvider>()));
        services.TryAddSingleton(provider => new PresetsExportPart(provider.GetRequiredService<PresetStore>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataExportPart, PresetsExportPart>(provider => provider.GetRequiredService<PresetsExportPart>()));
        return services;
    }

    /// <summary>Maps <c>/api/presets</c>.</summary>
    public static WebApplication MapPresetsArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });
        api.MapGet("", (PresetService presets) => Answer(() => Json(new
        {
            presets = presets.List().Select(preset => new
            {
                id = preset.Id,
                name = preset.Name,
                kind = preset.Kind,
                model = preset.ModelId,
                tags = preset.Tags,
                uses = preset.Uses,
                lastUsed = preset.LastUsed,
                pinned = preset.Pinned,
                parts = preset.Parts,
            }),
        })));
        api.MapGet("/export", (PresetService presets) => Answer(() =>
        {
            var (name, json) = presets.ExportAll();
            return File(name, json);
        }));
        api.MapPost("/import", (JsonObject? body, PresetService presets) => Answer(() =>
        {
            var text = Text(Required(body), "text", trim: false) ?? throw new ArgumentException("Send the file's text ({text}).");
            var result = presets.Import(text);
            return Json(new
            {
                imported = result.Imported.Select(preset => new { id = preset.Id, name = preset.Name }),
                skipped = result.Skipped,
                renamed = result.Renamed.Select(pair => new { from = pair.From, to = pair.To }),
            });
        }));
        api.MapPost("/preview", (JsonObject? body, PresetService presets, FragmentRenderer renderer) => AnswerAsync(async () =>
        {
            var json = Required(body);
            var view = presets.Preview(Text(json, "page"), json["capture"], Text(json, "origin"));
            var html = await renderer.RenderAsync(SaveModalPartial, view).ConfigureAwait(false);
            return Json(new { valid = view.Error is null, message = view.Error, html });
        }));
        api.MapPost("", (JsonObject? body, PresetService presets) => Answer(() =>
        {
            var json = Required(body);
            var update = Text(json, "update");
            var preset = presets.Save(Text(json, "page"), json["capture"], Meta(json), update);
            return Json(new { preset = PresetJson.Node(preset), created = update is null });
        }));
        api.MapGet("/{id}", (string id, PresetService presets) => Answer(() => Json(new { preset = presets.Require(id) })));
        api.MapPost("/{id}", (string id, JsonObject? body, PresetService presets) => Answer(() =>
            Json(new { preset = PresetJson.Node(presets.UpdateMeta(id, Meta(Required(body)))) })));
        api.MapPost("/{id}/duplicate", (string id, PresetService presets) => Answer(() => Json(new { preset = PresetJson.Node(presets.Duplicate(id)) })));
        api.MapPost("/{id}/delete", (string id, PresetService presets) => Answer(() =>
        {
            presets.Delete(id);
            return Json(new { deleted = id });
        }));
        api.MapGet("/{id}/load", (string id, string? page, PresetService presets, CancellationToken token) => AnswerAsync(async () =>
        {
            var answer = await presets.LoadAsync(id, page, token).ConfigureAwait(false);
            return Json(new
            {
                id = answer.Preset.Id,
                name = answer.Preset.Name,
                kind = answer.Preset.Kind,
                page = answer.Plan.Page,
                parts = answer.Plan.Parts,
                notes = answer.Plan.Notes,
                available = answer.Availability.Ok,
                problem = answer.Availability.Problem,
                connectUrl = answer.Availability.ConnectUrl,
                connectLabel = answer.Availability.ConnectLabel,
                payload = answer.Plan.Payload,
            });
        }));
        api.MapPost("/{id}/used", (string id, JsonObject? body, PresetService presets) => Answer(() =>
        {
            var preset = presets.Used(id, Text(Required(body), "page"));
            return Json(new { id = preset.Id, uses = preset.Uses, lastUsed = preset.LastUsed });
        }));
        api.MapGet("/{id}/export", (string id, PresetService presets) => Answer(() =>
        {
            var (name, json) = presets.Export(id);
            return File(name, json);
        }));
        return app;
    }

    /// <summary>A preset's name, description, tags, pin and include list from a JSON body.</summary>
    /// <exception cref="ArgumentException">A value has the wrong type.</exception>
    public static PresetMeta Meta(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var tags = json["tags"] switch
        {
            null => [],
            JsonArray list => PresetRules.Tags(list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new ArgumentException("tags is a list of strings."))),
            JsonValue value when value.TryGetValue<string>(out var text) => PresetRules.SplitTags(text),
            _ => throw new ArgumentException("tags is a list of strings."),
        };
        IReadOnlyList<string>? include = json["include"] switch
        {
            null => null,
            JsonArray list => [.. list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) && PresetParts.All.Contains(text) ? text
                : throw new ArgumentException($"include lists the parts to keep: {string.Join(", ", PresetParts.All)}."))],
            _ => throw new ArgumentException("include is a list of parts."),
        };
        var pinned = json["pinned"] is JsonValue pin && pin.TryGetValue<bool>(out var on) && on;
        return new PresetMeta(Text(json, "name") ?? "", Text(json, "description", trim: false), tags, pinned, include);
    }

    private static JsonObject Required(JsonObject? body) => body ?? throw new ArgumentException("Send a JSON body.");

    private static string? Text(JsonObject body, string name, bool trim = true) =>
        body[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? trim ? text.Trim() : text : null;

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult File(string name, string json) =>
        Results.File(Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", name);

    private static IResult Error(int status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    private static IResult Answer(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
        catch (KeyNotFoundException error)
        {
            return Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, error.Message);
        }
    }

    private static async Task<IResult> AnswerAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            return Error(StatusCodes.Status400BadRequest, SpendErrors.Invalid, error.Message);
        }
        catch (KeyNotFoundException error)
        {
            return Error(StatusCodes.Status404NotFound, SpendErrors.NotFound, error.Message);
        }
    }
}
