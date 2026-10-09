// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/opencode-headers.ts (withOpenCodeSessionHeader),
// providers/opencode.ts and opencode-go.ts (every API stream wrapped), and the agent's sessionId on every request (agent.ts).
using System.Net;
using System.Text;
using PiSharp.Contracts;

internal static partial class Program
{
    /// <summary>A live OpenCode session sends x-opencode-session with the agent session id on its main requests, for each API
    /// the opencode provider serves (anthropic-messages, google-generative-ai, openai-completions, openai-responses).</summary>
    private static Task LiveOpenCodeSessionHeader() => WithLiveRoot("live-opencode-session", async root =>
    {
        foreach (var (provider, model) in new[] { ("opencode", "claude-haiku-4-5"), ("opencode", "gemini-3-flash"), ("opencode", "big-pickle"),
            ("opencode", "gpt-5"), ("opencode-go", "deepseek-v4-flash"), ("opencode-go", "claude-haiku-5-5") })
        {
            var endpoint = new LiveEndpoint(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("""{"error":{"message":"capture","type":"invalid_request_error"}}""", Encoding.UTF8, "application/json") });
            var workspace = Path.Combine(root, provider + "-" + model.Replace('/', '-')); Directory.CreateDirectory(workspace);
            await using var rpc = new LiveRpc(LiveArgs(workspace, provider, model),
                new(Env(("OPENCODE_API_KEY", "opencode-key")), () => endpoint));
            string? sessionId;
            try { sessionId = (await rpc.Command("state", new { type = "get_state" })).GetProperty("sessionId").GetString(); }
            catch (InvalidOperationException error) { throw new InvalidOperationException(provider + "/" + model + ": " + error.Message + rpc.Error, error); }
            await rpc.Prompt("p", "hello");
            await rpc.Finish();
            var requests = endpoint.Snapshot();
            Check(requests.Length > 0, provider + "/" + model + " sent no request");
            foreach (var request in requests)
                Equal(sessionId, request.Headers.TryGetValue("x-opencode-session", out var value) ? value : null, provider + "/" + model + " x-opencode-session");
        }
    });

}
