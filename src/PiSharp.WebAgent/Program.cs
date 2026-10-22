// PiSharp Web Agent (decision 0011): a local chat app and model playground on the durable runtime. It binds http://127.0.0.1:5180 unless
// --urls names another loopback address, keeps its data under %LOCALAPPDATA%\PiSharpWebAgent (PISHARP_WEBAGENT_DATA or --data <folder>
// move it), and with --faux runs offline on faux models. The core (hosting, data, contracts, shell) is WebAgentHost's; each area registers
// its services and maps its endpoints through its module, before the core's fallbacks fill in what no area registered.
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Settings;

WebAgentOptions options;
WebApplicationBuilder builder;
try
{
    (options, var hostArgs) = WebAgentOptions.FromCommandLine(args);
    builder = WebAgentHost.CreateBuilder(options, hostArgs);
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException)
{
    // A bad --data or --faux, or --urls beyond loopback: say why, without a stack trace, and start nothing.
    Console.Error.WriteLine("PiSharp Web Agent: " + error.Message);
    return 2;
}

builder.Services.AddWebAgentCore(options);
builder.Services.AddCredentialsArea(options);
builder.Services.AddChatArea(options);
builder.Services.AddRoutingArea(options);
builder.Services.AddSettingsArea(options);
builder.Services.AddWebAgentFallbacks(options);

var app = builder.Build();
app.UseWebAgentCore();
app.MapCredentialsArea();
app.MapChatArea();
app.MapRoutingArea();
app.MapSettingsArea();
app.MapWebAgentCore();
app.Run();
return 0;
