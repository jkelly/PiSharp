using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;
namespace PiSharp.Samples.ResearchWorkbench;
public static class Workflow {
  static readonly string Time = "2026-10-01T00:00:00.000Z";
  static SessionEntry Message(SessionEntryCodec codec, string id, string? parent, string role, string content) => codec.Parse(JsonSerializer.Serialize(new {
    type = "message", id, parentId = parent, timestamp = Time, message = new {
      role, content = new[] { new { type = "text", text = content } }, timestamp = 0,
      api = "fixture", provider = "fixture", model = "fixture", stopReason = "stop",
      usage = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0,
        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 } }
    }
  }));
  public static WorkflowSpec Create(SampleContext c) => new("research-workbench", "Research workbench", "Capture cited claims in a native PiSharp session, then fork a follow-up question.", [
    new("capture_evidence", "Persist question and cited source claims as a v3 session log", SampleJson.Schema(), async (_, ct) => {
      var codec = new SessionEntryCodec(); var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "research-source", timestamp = Time, cwd = c.Workspace }));
      var entries = new List<SessionEntry> { Message(codec, "question", null, "user", c.Input.Text("question")) }; string parent = "question";
      foreach (var source in c.Input["sources"]!.AsArray()) { ct.ThrowIfCancellationRequested(); var id = source!["id"]!.GetValue<string>();
        if (entries.Any(e => e.Id == id)) throw new FormatException("Duplicate source ID.");
        entries.Add(Message(codec, id, parent, "assistant", $"[{id}] {source["title"]}: {source["claim"]}")); parent = id;
      }
      var path = c.ResolvePath("source-session.jsonl"); await using (var store = await SessionLogStore.CreateNewAsync(path, header, cancellationToken: ct)) await store.AppendAsync(entries.ToImmutableArray(), ct);
      c.State["sourceLeaf"] = parent; return new JsonObject { ["path"] = "source-session.jsonl", ["sources"] = entries.Count - 1, ["leafId"] = parent };
    }),
    new("branch_inquiry", "Fork source session at evidence leaf using the native branch planner", SampleJson.Schema(), async (_, ct) => {
      var sourcePath = c.ResolvePath("source-session.jsonl"); await using var store = await SessionLogStore.OpenAsync(sourcePath, cancellationToken: ct);
      var plan = new SessionBranchPlanner().Fork(new SessionForkPlanRequest(store.Snapshot.Header, store.Snapshot.Entries,
        c.State.Text("sourceLeaf"), SessionForkPosition.At, "research-branch", Time, "research-source"), ct);
      var codec = new SessionEntryCodec(); var question = Message(codec, "follow-up", plan.LeafId, "user", c.Input.Text("branchQuestion"));
      var branchPath = c.ResolvePath("branch-session.jsonl"); await using (var branch = await SessionLogStore.CreateNewAsync(branchPath, plan.Header, cancellationToken: ct)) {
        if (!plan.Entries.IsEmpty) await branch.AppendAsync(plan.Entries, ct); await branch.AppendAsync([question], ct);
      }
      var read = await new SessionLogReader().ReadFileAsync(branchPath, ct);
      if (read.Status != SessionLogReadStatus.Complete) throw new InvalidOperationException("Published branch did not validate.");
      return new JsonObject { ["path"] = "branch-session.jsonl", ["entries"] = read.ValidatedPrefix.Length - 1, ["parentSession"] = "research-source" };
    })
  ], [new("capture_evidence", new()), new("branch_inquiry", new())]);
}
