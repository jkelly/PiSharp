// PiSharp Durable Web (decision 0008): the hub and UI for durable devices. It binds 127.0.0.1:5181 unless --urls says otherwise, keeps its
// registry under %LOCALAPPDATA%\PiSharpDurableWeb, and lists this computer's `pisharp durable serve` as `local`.
using Microsoft.AspNetCore.DataProtection;
using PiSharp.Durable.Web.Hub;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5181");
// The content root is the output folder (so appsettings.json is found from any working directory); a build output serves wwwroot through
// its static web assets manifest, a publish output from its own wwwroot.
builder.WebHost.UseStaticWebAssets();
var hub = builder.Configuration.GetSection("Hub").Get<HubOptions>() ?? new HubOptions();
Directory.CreateDirectory(hub.DataDirectory);
builder.Services.AddSingleton(hub);
var protection = builder.Services.AddDataProtection().SetApplicationName("PiSharpDurableWeb")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Join(hub.DataDirectory, "keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<LocalDevice>();
builder.Services.AddSingleton<DeviceMonitor>();
builder.Services.AddHostedService(services => services.GetRequiredService<DeviceMonitor>());
builder.Services.AddSingleton<DeviceProxy>();
builder.Services.AddSingleton<StreamMux>();
builder.Services.AddSingleton<FragmentRenderer>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddSingleton<DeviceClients>();
builder.Services.AddHttpClient(DeviceProxy.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddControllersWithViews();

var app = builder.Build();
app.UseMiddleware<HostGuard>();
app.UseStaticFiles();
app.MapGet("/api/stream", (HttpContext http, StreamMux mux) => mux.OpenAsync(http));
app.MapPut("/api/stream/{streamId}", async (HttpContext http, string streamId, StreamMux mux) =>
{
    try
    {
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<StreamSubscriptions>(http.Request.Body,
            PiSharp.Durable.Client.DurableWire.Options, http.RequestAborted).ConfigureAwait(false);
        return Results.Json(new { subs = mux.Subscribe(streamId, body?.Subs ?? []) });
    }
    catch (KeyNotFoundException closed)
    {
        return Results.Json(new { error = new { code = "not_found", message = closed.Message } }, statusCode: 404);
    }
    catch (Exception failure) when (failure is ArgumentException or System.Text.Json.JsonException)
    {
        return Results.Json(new { error = new { code = "invalid", message = failure.Message } }, statusCode: 400);
    }
});
app.Map("/api/devices/{id}/v1/{**path}", (HttpContext http, string id, string? path, DeviceProxy proxy) => proxy.HandleAsync(http, id, path ?? ""));
app.MapControllers();
app.Run();

/// <summary>The body of <c>PUT /api/stream/{streamId}</c>.</summary>
internal sealed record StreamSubscriptions(IReadOnlyList<string> Subs);
