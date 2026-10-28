// CustomAgents.Examples: decision 0012's custom agents end to end. Usage:
//   CustomAgents.Examples [orders|team|all] [--provider <id> --model <id>] [--thinking <level>] [--dir <path>] [--crash-in <action>]
// Without --provider the models are scripted (FauxAgentModels) and the demo runs offline. With --provider and --model a real provider
// answers; its API key comes from its environment variable (for example OPENAI_API_KEY). Runs are kept in SQLite under --dir (default
// ./custom-agents-demo). --crash-in load|summarize exits the process inside that action of the orders workflow, after its work (and, for
// summarize, the model's answer) and before the step committed; run the same command again and the run resumes there.
using PiSharp.CustomAgents.Examples;

var options = DemoOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(DemoOptions.Usage);
    return 2;
}
return await Demo.RunAsync(options, Console.Out, Environment.GetEnvironmentVariable);
