using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

internal sealed partial class SessionCatalog
{
    private const string SchemaV4 = """
        CREATE TABLE job_schedules (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, value TEXT NOT NULL);
        CREATE INDEX job_schedules_session ON job_schedules(session_id);
        CREATE TABLE trigger_credentials (
          id TEXT PRIMARY KEY, label TEXT NOT NULL, schedule_id TEXT, hash BLOB NOT NULL,
          created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL
        );
        """;

    public IReadOnlyList<JobSchedule> Schedules()
    {
        lock (gate)
        {
            using var command = Command(connection, null, "SELECT value FROM job_schedules ORDER BY id;", []);
            using var reader = command.ExecuteReader();
            var result = new List<JobSchedule>();
            while (reader.Read()) result.Add(JobSchedule.FromJson((JsonObjectValue)JsonValue.Parse(reader.GetString(0))));
            return result;
        }
    }

    public void SaveSchedule(JobSchedule schedule)
    {
        lock (gate) Execute(connection, null,
            "INSERT INTO job_schedules (id, session_id, value) VALUES ($id, $session, $value) ON CONFLICT(id) DO UPDATE SET value = excluded.value;",
            ("$id", schedule.Id), ("$session", schedule.SessionId), ("$value", schedule.ToJson().ToJson()));
    }

    public JobSchedule? Schedule(string id)
    {
        lock (gate)
        {
            var value = Scalar(connection, null, "SELECT value FROM job_schedules WHERE id = $id;", ("$id", id)) as string;
            return value is null ? null : JobSchedule.FromJson((JsonObjectValue)JsonValue.Parse(value));
        }
    }

    public void DeleteSchedule(string id)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM trigger_credentials WHERE schedule_id = $id;", ("$id", id));
            Execute(connection, transaction, "DELETE FROM job_schedules WHERE id = $id;", ("$id", id));
            transaction.Commit();
        }
    }

    public void AddTriggerCredential(TriggerCredential value, byte[] hash)
    {
        lock (gate) Execute(connection, null,
            "INSERT INTO trigger_credentials (id, label, schedule_id, hash, created_at, expires_at) VALUES ($id,$label,$schedule,$hash,$created,$expires);",
            ("$id", value.Id), ("$label", value.Label), ("$schedule", value.ScheduleId), ("$hash", hash), ("$created", value.CreatedAt), ("$expires", value.ExpiresAt));
    }

    public IReadOnlyList<(TriggerCredential Metadata, byte[] Hash)> TriggerCredentials()
    {
        lock (gate)
        {
            using var command = Command(connection, null, "SELECT id,label,schedule_id,created_at,expires_at,hash FROM trigger_credentials ORDER BY created_at,id;", []);
            using var reader = command.ExecuteReader();
            var result = new List<(TriggerCredential, byte[])>();
            while (reader.Read()) result.Add((new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4)), (byte[])reader.GetValue(5)));
            return result;
        }
    }

    public void RevokeTriggerCredential(string id)
    { lock (gate) Execute(connection, null, "DELETE FROM trigger_credentials WHERE id = $id;", ("$id", id)); }
}
