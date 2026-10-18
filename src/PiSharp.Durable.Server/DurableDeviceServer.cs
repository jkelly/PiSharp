// PiSharp-only (decision 0008). A Kestrel app that serves one device: what `pisharp durable serve` and the server tests run.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using PiSharp.Durable.Hosting;

namespace PiSharp.Durable.Server;

/// <summary>Builds the web app of a device.</summary>
public static class DurableDeviceServer
{
    /// <summary>A web app serving <paramref name="host"/>'s API at <paramref name="url"/> (port 0 picks a free port; read the bound address
    /// from the started app's <c>Urls</c>).</summary>
    public static WebApplication Build(DurableDeviceHost host, DurableDeviceServerOptions options, string url, LogLevel logLevel = LogLevel.Warning)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(url);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls(url);
        builder.Logging.SetMinimumLevel(logLevel);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", logLevel);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = Math.Max(options.MaxJsonBytes * 2L, 1024 * 1024);
        });
        var app = builder.Build();
        app.MapDurableDeviceApi(host, options);
        return app;
    }
}
