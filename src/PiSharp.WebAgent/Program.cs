// PiSharp Web Agent (decision 0011): a local chat app and model playground on the durable runtime. It binds http://127.0.0.1:5180 unless
// --urls names another loopback address, keeps its data under %LOCALAPPDATA%\PiSharpWebAgent (PISHARP_WEBAGENT_DATA or --data <folder>
// move it), and with --faux runs offline on faux models. The core (hosting, data, contracts, shell) is WebAgentHost's; each area registers
// its services and maps its endpoints through its module, before the core's fallbacks fill in what no area registered.
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Settings;
using PiSharp.WebAgent.Spend;
using PiSharp.WebAgent.Setup;
using PiSharp.WebAgent.About;
using PiSharp.WebAgent.CustomProviders;
using PiSharp.WebAgent.Runs;

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
builder.Services.AddSetupArea(options);
builder.Services.AddAboutArea(options);
builder.Services.AddCustomProvidersArea(options);
builder.Services.AddWebAgentFallbacks(options);
// Spend Guard (run W3): after the fallbacks, so the guard decorates the app's final IDurableModels (--faux-metered prices the faux ones).
builder.Services.AddSpendArea(options);
// Agent compare workspaces (run W4, R4): a git worktree or a copy per model under <data>\workspaces, cleaned at start and daily.
PiSharp.WebAgent.AgentCompare.Workspaces.WorkspacesModule.AddAgentCompareWorkspaces(builder.Services, options);
// Run engine and history (run W4, R0): after the Spend Guard, so the recorder wraps the guarded models; runs.db, /api/runs.
builder.Services.AddRunsArea(options);
// Agent compare runner (run W4, R5): after the run history, so its device models are the recorder over the Spend Guard; /api/compare/agent.
PiSharp.WebAgent.AgentCompare.Runner.AgentCompareModule.AddAgentCompareRunner(builder.Services, options);
// Playground (run W4, R1): GET /playground and /api/playground (the estimate by Run, View code, a model's options and account).
PiSharp.WebAgent.Playground.PlaygroundModule.AddPlaygroundArea(builder.Services, options);
// History and the Run inspector (run W4, R3): GET /history, GET /runs/{id} and /api/history (export, clear, re-run, downloads, saved filters).
PiSharp.WebAgent.Runs.RunHistoryModule.AddRunHistoryArea(builder.Services, options);

var app = builder.Build();
app.UseWebAgentCore();
app.MapCredentialsArea();
app.MapChatArea();
app.MapRoutingArea();
app.MapSettingsArea();
app.MapSpendArea();
app.MapSetupArea();
app.MapAboutArea();
app.MapCustomProvidersArea();
app.MapRunsArea();
PiSharp.WebAgent.AgentCompare.Runner.AgentCompareModule.MapAgentCompareRunner(app);
PiSharp.WebAgent.Playground.PlaygroundModule.MapPlaygroundArea(app);
PiSharp.WebAgent.Runs.RunHistoryModule.MapRunHistoryArea(app);
app.MapWebAgentCore();
app.Run();
return 0;
