using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

internal static class PersistentSessionCompileConsumerTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("C1 original target-typed configuration OpenAsync compiles and reopens durable bytes", DefaultOpen),
        ("C1 original target-typed configuration with selected options compiles and reopens", SelectedOpen)
    ];

    // Keep these original consumer expressions target-typed. Explicit AgentConfiguration
    // constructors would hide the overload regression rather than prevent recurrence.
    private static Task<PersistentAgentSession> OpenDefault(string path, ModelDescriptor model,
        IChatTransport transport, Func<long> clock, Func<string> nextEntryId) =>
        PersistentAgentSession.OpenAsync(path, new(model, transport, []), clock, nextEntryId);

    private static Task<PersistentAgentSession> OpenSelected(string path, ModelDescriptor model,
        IChatTransport transport, Func<long> clock, Func<string> nextEntryId, PersistentAgentSessionOptions options) =>
        PersistentAgentSession.OpenAsync(path, new(model, transport, []), clock, nextEntryId, options);

    private static Task DefaultOpen() => Run(selected: false);
    private static Task SelectedOpen() => Run(selected: true);

    private static async Task Run(bool selected)
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.Combine(parent, "PiSharp-C1-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "consumer.jsonl");
        var model = new ModelDescriptor("consumer-model", "openai-responses", "consumer-provider");
        var transport = new NeverAcquireTransport();
        var nextId = 0;
        string NextId() => "consumer-entry-" + ++nextId;
        try
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "consumer-session", timestamp = "2026-10-01T00:00:00.000Z", cwd = directory }));
            string? leaf;
            await using (var created = await PersistentAgentSession.CreateAsync(path, header,
                new AgentConfiguration(model, transport, []), () => 123, NextId))
                leaf = created.Snapshot.Log.LeafId;
            var original = await File.ReadAllBytesAsync(path);
            await using (var opened = selected
                ? await OpenSelected(path, model, transport, () => 123, NextId,
                    new(UseLatestLeaf: false, SelectedLeafId: leaf))
                : await OpenDefault(path, model, transport, () => 123, NextId))
            {
                if (opened.Snapshot.Agent.Model != model || opened.Snapshot.Context.LeafId != leaf ||
                    opened.Snapshot.Log.CommittedByteLength != original.Length || opened.Snapshot.Agent.Messages.Length != 0)
                    throw new Exception("Original configuration consumer did not restore its selected durable state.");
            }
            var reopened = await File.ReadAllBytesAsync(path);
            if (!original.AsSpan().SequenceEqual(reopened))
                throw new Exception("Opening an original configuration consumer changed durable bytes.");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(directory))!.TrimEnd(Path.DirectorySeparatorChar) !=
                parent.TrimEnd(Path.DirectorySeparatorChar)) throw new Exception("Consumer cleanup escaped owned temporary parent.");
            File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
    }

    private sealed class NeverAcquireTransport : IChatTransport
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            throw new Exception("Opening a session unexpectedly acquired a provider.");
    }
}
