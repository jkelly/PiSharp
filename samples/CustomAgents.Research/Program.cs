// CustomAgents.Research: decision 0012's subagents on a research task. A lead agent splits a topic into parts and gives each part to its
// own researcher subagent, all in one turn, so they work in parallel. The researchers search posts on X first, with xAI's X search
// (x_search), and the web (web_search) to check facts. The lead merges what they found into one Markdown report, which this program writes
// to a file. Without --provider the models are scripted and the searches canned, so it runs offline. See ResearchOptions.Usage.
using PiSharp.CustomAgents.Research;

var options = ResearchOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(ResearchOptions.Usage);
    return 2;
}
return await ResearchRun.RunAsync(options, Console.Out, Environment.GetEnvironmentVariable);
