// Decision 0011 (run W4, unit R9): Settings › Data & storage (the mockups' settings-data, route GET /settings/data), mapped 1:1: the
// locations with the usage meter (_StorageLocations), chats and their archive (_ChatStorage), compare workspaces (_CompareWorkspaces), run
// history's retention and payload settings (_DataRunHistory), export and import (_DataExport, _DataImportModal) and the danger zone
// (_DangerZone, _DataConfirmModal with its typed DELETE). The page is measured when it is asked for (DataStorageService.PageAsync) and every
// change goes through /api/data (data.js). No-store, since it lists what is stored on this machine.
using Microsoft.AspNetCore.Mvc;
using PiSharp.WebAgent.DataStorage;

namespace PiSharp.WebAgent.Controllers;

/// <summary>Settings › Data &amp; storage.</summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DataController(DataStorageService data) : Controller
{
    /// <summary>The page's route.</summary>
    public const string Route = "/settings/data";

    /// <summary>Settings › Data &amp; storage.</summary>
    [HttpGet(Route)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View("~/Views/Settings/Data.cshtml", await data.PageAsync(cancellationToken).ConfigureAwait(false));
}
