// Durable.Chat: see ChatApp.cs. Usage:
//   Durable.Chat --provider <id> --model <id> [--thinking <level>] [--storage memory|jsonl|sqlite] [--dir <path>] [--continue]
//                [--tools [--cwd <path>]] ["<prompt>"]
// The provider's API key comes from its environment variable (for example ANTHROPIC_API_KEY or OPENAI_API_KEY).
using PiSharp.Durable.Chat;

var options = ChatApp.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(ChatApp.Usage);
    return 2;
}
return await ChatApp.RunAsync(options, Console.Out, new() { Environment = Environment.GetEnvironmentVariable });
