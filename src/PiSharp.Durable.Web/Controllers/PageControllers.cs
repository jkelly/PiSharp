// PiSharp-only (decision 0008, PLAN.md §7 Routes and views). The pages: the network (with deep links to an agent) and the devices page, and
// the inspector panels the network page loads for the selected agent. Panels read the device through the hub's own client, so the browser
// never needs a token.
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using PiSharp.Durable.Client;
using PiSharp.Durable.Web.Hub;
using PiSharp.Durable.Web.Models;

namespace PiSharp.Durable.Web.Controllers;

/// <summary>The network page.</summary>
public sealed class NetworkController(DeviceMonitor monitor) : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => View("Index", new NetworkPage(monitor.Network(), Selection.None));

    [HttpGet("/d/{device}")]
    [HttpGet("/d/{device}/s/{session}")]
    [HttpGet("/d/{device}/s/{session}/a/{agent:long}")]
    public IActionResult Deep(string device, string? session, long? agent) =>
        View("Index", new NetworkPage(monitor.Network(), new Selection(device, session, session is null ? null : agent ?? 1)));
}

/// <summary>The devices page.</summary>
public sealed class DevicesController(DeviceMonitor monitor) : Controller
{
    [HttpGet("/devices")]
    public IActionResult Index() => View("Index", new DevicesPage(monitor.Network()));

    [HttpGet("/devices/table")]
    public IActionResult Table() => PartialView("_DeviceTable", new DevicesPage(monitor.Network()));
}

/// <summary>Inspector panels and the offline panel, as HTML fragments.</summary>
[Route("ui")]
public sealed class PanelsController(DeviceMonitor monitor, DeviceClients clients) : Controller
{
    [HttpGet("{device}/{session}/{agent:long}/session")]
    public async Task<IActionResult> Session(string device, string session, long agent, CancellationToken cancellationToken)
    {
        if (Online(device) is not { } known) return Offline(device);
        using var client = clients.For(device);
        var summary = DurableWire.Read<SessionSummary>(await client.GetAsync($"/v1/sessions/{Esc(session)}", null, cancellationToken))!;
        var detail = DurableWire.Read<AgentDetail>(await client.GetAsync($"/v1/sessions/{Esc(session)}/agents/{agent}", null, cancellationToken))!;
        var model = known.Info?.Models.FirstOrDefault(item => item.Id == detail.Config.Model?.ModelId && item.Provider == detail.Config.Model?.Provider);
        return PartialView("~/Views/Panels/_SessionTab.cshtml", new SessionTabModel(known, summary, detail, model, summary.Compaction ?? CompactionSettings.PiDurableDefaults));
    }

    [HttpGet("{device}/{session}/{agent:long}/tools")]
    public async Task<IActionResult> Tools(string device, string session, long agent, CancellationToken cancellationToken)
    {
        if (Online(device) is null) return Offline(device);
        using var client = clients.For(device);
        var detail = DurableWire.Read<AgentDetail>(await client.GetAsync($"/v1/sessions/{Esc(session)}/agents/{agent}", null, cancellationToken))!;
        return PartialView("~/Views/Panels/_ToolsTab.cshtml", new ToolsTabModel(detail));
    }

    [HttpGet("{device}/{session}/{agent:long}/extend")]
    public async Task<IActionResult> Extend(string device, string session, long agent, CancellationToken cancellationToken)
    {
        if (Online(device) is null) return Offline(device);
        using var client = clients.For(device);
        var extensions = DurableWire.Read<AgentExtensions>(await client.GetAsync($"/v1/sessions/{Esc(session)}/agents/{agent}/extensions", null, cancellationToken))!;
        return PartialView("~/Views/Panels/_ExtendTab.cshtml", new ExtendTabModel(extensions));
    }

    [HttpGet("{device}/{session}/{agent:long}/jobs")]
    public async Task<IActionResult> Jobs(string device, string session, long agent, CancellationToken cancellationToken)
    {
        if (Online(device) is not { } known) return Offline(device);
        using var client = clients.For(device);
        var jobs = (await client.GetAsync($"/v1/sessions/{Esc(session)}/agents/{agent}/jobs", null, cancellationToken)) as JsonObject;
        return PartialView("~/Views/Panels/_JobsTab.cshtml", new JobsTabModel(known, Display.Objects(jobs, "definitions").ToList(), Display.Objects(jobs, "runs").ToList()));
    }

    [HttpGet("{device}/{session}/runtime")]
    public async Task<IActionResult> Runtime(string device, string session, CancellationToken cancellationToken)
    {
        if (Online(device) is null) return Offline(device);
        using var client = clients.For(device);
        var terminal = await client.GetAsync($"/v1/sessions/{Esc(session)}/tasks?status=terminal&limit=12", null, cancellationToken);
        var inspection = await client.GetAsync($"/v1/sessions/{Esc(session)}/inspect", null, cancellationToken);
        return PartialView("~/Views/Panels/_RuntimeDetails.cshtml", (terminal as JsonArray ?? [], inspection as JsonObject ?? []));
    }

    [HttpGet("offline/{device}")]
    public IActionResult Offline(string device) =>
        monitor.Network().FirstOrDefault(item => item.Id == device) is { } known
            ? PartialView("~/Views/Panels/_OfflinePanel.cshtml", new OfflineModel(known))
            : NotFound();

    private NetworkDevice? Online(string device) => monitor.Network().FirstOrDefault(item => item.Id == device && item.Status == "online");

    private static string Esc(string value) => Uri.EscapeDataString(value);
}
