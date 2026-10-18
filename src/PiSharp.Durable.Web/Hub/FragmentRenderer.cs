// PiSharp-only (decision 0008, PLAN.md §7 "the hub reduces, Razor renders"). Renders a Razor partial to a string outside a request, so the
// stream mux can push the same markup a page load renders: one markup source for the first paint and every update.
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace PiSharp.Durable.Web.Hub;

/// <summary>Renders partial views to HTML strings.</summary>
public sealed class FragmentRenderer(IRazorViewEngine engine, ITempDataProvider tempData, IServiceScopeFactory scopes)
{
    /// <summary>The HTML of the partial at <paramref name="path"/> (for example <c>~/Views/Fragments/_TranscriptItem.cshtml</c>).</summary>
    public async Task<string> RenderAsync(string path, object? model)
    {
        using var scope = scopes.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var found = engine.GetView(executingFilePath: null, viewPath: path, isMainPage: false);
        if (!found.Success) throw new InvalidOperationException($"No view {path}: searched {string.Join(", ", found.SearchedLocations ?? [])}.");
        await using var writer = new StringWriter();
        var context = new ViewContext(action, found.View, new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model },
            new TempDataDictionary(http, tempData), writer, new HtmlHelperOptions());
        await found.View.RenderAsync(context).ConfigureAwait(false);
        return writer.ToString();
    }
}
