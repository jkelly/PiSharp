using PiSharp.Durable.Examples;

// Milestone M0 (decision 0005): samples/Durable.Examples ports Pi v1.1.0 packages/durable/test/examples 00-05. The printed values are
// the ones the pinned upstream prints under Node (node --conditions=source --experimental-strip-types test/examples/<name>.ts), as JSON
// in place of util.inspect. The file backends also reopen each example's storage in a new Session and read it back.
internal static partial class Program
{
    private static readonly string[] ExamplesUpstreamOutput =
    [
        "== 00-conversation",
        "standalone conversation: {\"id\":2}",
        "== 01-documents",
        "latest notes: {\"text\":\"after goodbye\"}",
        "notes at first entry: {\"text\":\"after hello\"}",
        "notes at second entry: {\"text\":\"after goodbye\"}",
        "== 02-forks",
        "fork transcript: [\"hello\"]",
        "fork notes: {\"text\":\"after hello\"}",
        "fork notes after edit: {\"text\":\"changed only in the fork\"}",
        "parent notes after edit: {\"text\":\"after goodbye\"}",
        "== 03-owned-conversations",
        "supervisor task: 3",
        "child conversation: {\"id\":4,\"owner\":{\"conversationId\":2,\"taskId\":3}}",
        "registry: {\"agents\":{\"researcher\":{\"conversationId\":4,\"requestId\":\"researcher:first-message:3\"}}}",
        "== 04-chord-state",
        "Chord notes: hydrate 0 {\"text\":\"first\"}",
        "Chord notes: update 1 {\"text\":\"published through Chord\"}",
        "== 05-watches",
        "watch baseline: {\"text\":\"first\"}",
        "watch update: {\"text\":\"observed asynchronously\"}",
    ];

    // What each example reads back after reopen, keyed by the example's last upstream line.
    private static readonly Dictionary<string, string[]> ExamplesReopened = new(StringComparer.Ordinal)
    {
        ["standalone conversation: {\"id\":2}"] = ["standalone conversation: {\"id\":2}"],
        ["notes at second entry: {\"text\":\"after goodbye\"}"] =
        [
            "latest notes: {\"text\":\"after goodbye\"}",
            "notes at first entry: {\"text\":\"after hello\"}",
            "notes at second entry: {\"text\":\"after goodbye\"}",
        ],
        ["parent notes after edit: {\"text\":\"after goodbye\"}"] =
        [
            "fork transcript: [\"hello\"]",
            "fork notes after edit: {\"text\":\"changed only in the fork\"}",
            "parent notes after edit: {\"text\":\"after goodbye\"}",
        ],
        ["registry: {\"agents\":{\"researcher\":{\"conversationId\":4,\"requestId\":\"researcher:first-message:3\"}}}"] =
        [
            "supervisor task: 3 example.supervisor",
            "child conversation: {\"id\":4,\"owner\":{\"conversationId\":2,\"taskId\":3}}",
            "registry: {\"agents\":{\"researcher\":{\"conversationId\":4,\"requestId\":\"researcher:first-message:3\"}}}",
        ],
        ["Chord notes: update 1 {\"text\":\"published through Chord\"}"] = ["Chord notes: hydrate 0 {\"text\":\"published through Chord\"}"],
        ["watch update: {\"text\":\"observed asynchronously\"}"] = ["watch baseline: {\"text\":\"observed asynchronously\"}"],
    };

    private static IEnumerable<(string, Func<Task>)> ExamplesCases()
    {
        yield return Case("examples.memory.upstream-output", async () => Lines(await RunExamplesAsync(ExampleStorage.Memory, null), ExamplesUpstreamOutput));
        foreach (var storage in new[] { ExampleStorage.Jsonl, ExampleStorage.Sqlite })
            yield return Case($"examples.{storage.ToString().ToLowerInvariant()}.reopen", async () =>
            {
                var expected = new List<string>();
                foreach (var line in ExamplesUpstreamOutput)
                {
                    expected.Add(line);
                    if (ExamplesReopened.TryGetValue(line, out var reopened)) { expected.Add("-- reopened"); expected.AddRange(reopened); }
                }
                Lines(await RunExamplesAsync(storage, Temp("examples-" + storage)), expected);
            });
        yield return Case("examples.select-by-number", async () =>
        {
            Lines(await RunExamplesAsync(ExampleStorage.Memory, null, "02"), ExamplesUpstreamOutput[6..11]);
            await ThrowsAsync<ArgumentException>(() => RunExamplesAsync(ExampleStorage.Memory, null, "99"), "unknown example");
        });
    }

    private static async Task<string> RunExamplesAsync(ExampleStorage storage, string? root, params string[] only)
    {
        var output = new StringWriter();
        await Examples.RunAsync(storage, root, output, only);
        return output.ToString();
    }

    private static void Lines(string output, IReadOnlyList<string> expected)
    {
        var actual = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        for (var index = 0; index < Math.Max(actual.Length, expected.Count); index++)
            Equal(index < expected.Count ? expected[index] : "<end>", index < actual.Length ? actual[index] : "<end>", "line " + (index + 1));
    }
}
