// Decision 0011 (run W4, R8): Presets (GET /presets, after the mockups' presets.html) and the fragments presets.js and presets-host.js swap
// in: the grid for the filters (GET /presets/grid, the same query string as the page), the edit pane of the shared save modal (GET
// /presets/{id}/edit) and the shared load modal's content for a page (GET /presets/picker?page=). ?type= is another name for ?in= (the Tool
// Bench links to /presets?type=bench). The JSON endpoints are PresetsModule's (/api/presets).
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.Presets;

namespace PiSharp.WebAgent.Controllers;

/// <summary>The Presets page and fragments.</summary>
public sealed class PresetsController(PresetService presets) : Controller
{
    /// <summary>Presets.</summary>
    [HttpGet("/presets")]
    public IActionResult Index(string? q, string? @in, string? type, string? sort, [FromQuery(Name = "tag")] string[]? tag) =>
        View(presets.Page(PresetQuery.From(q, @in ?? type, sort, tag)));

    /// <summary>The grid for the filters: <c>_PresetGrid</c>.</summary>
    [HttpGet("/presets/grid")]
    public IActionResult Grid(string? q, string? @in, string? type, string? sort, [FromQuery(Name = "tag")] string[]? tag)
    {
        Response.Headers.CacheControl = "no-store";
        return PartialView("_PresetGrid", presets.Page(PresetQuery.From(q, @in ?? type, sort, tag)));
    }

    /// <summary>The save modal's edit pane for preset <paramref name="id"/>.</summary>
    [HttpGet("/presets/{id}/edit")]
    public IActionResult Edit(string id)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            return PartialView(PresetsModule.SaveModalPartial, presets.Edit(id));
        }
        catch (KeyNotFoundException error)
        {
            return NotFound(error.Message);
        }
    }

    /// <summary>The load modal's content for <paramref name="page"/>.</summary>
    [HttpGet("/presets/picker")]
    public IActionResult Picker(string? page)
    {
        Response.Headers.CacheControl = "no-store";
        return PartialView(PresetsModule.LoadModalPartial, presets.Picker(page));
    }
}
