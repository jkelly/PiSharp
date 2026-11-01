// PiSharp-only (decision 0010): catalog version 7, the feedback records of M5. Events are authoritative and append-only (a reversal, a
// reset or a forgotten example is a new event); a profile row holds only its revision and settings, and its weights and examples are
// recomputed from the events whenever they are read.
namespace PiSharp.Durable.Hosting;

internal sealed partial class SessionCatalog
{
    private const string SchemaV7 = """
        CREATE TABLE preference_profiles (
          id TEXT PRIMARY KEY, revision INTEGER NOT NULL, settings TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL
        );
        CREATE TABLE feedback_events (
          seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, profile_id TEXT NOT NULL, request_id TEXT NOT NULL, request_hash TEXT NOT NULL,
          session_id TEXT, artifact_id TEXT, action TEXT NOT NULL, event TEXT NOT NULL, at INTEGER NOT NULL,
          UNIQUE (profile_id, request_id)
        );
        CREATE INDEX feedback_events_profile ON feedback_events(profile_id, seq);
        """;
}
