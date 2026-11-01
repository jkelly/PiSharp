// PiSharp-only (no upstream Pi source): disposable in-memory migration simulation.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.DatabaseMigrationReviewer;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"migrationId\":{\"type\":\"string\"}}", "migrationId");
        JsonObject Migration(JsonObject a)
        {
            var id = a.Text("migrationId");
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,60}$")) throw new ArgumentException("Invalid migration ID.");
            var found = (c.Input["migrations"] as JsonArray ?? throw new ArgumentException("migrations required.")).OfType<JsonObject>().Where(m => m.Text("id") == id).ToArray();
            return found.Length == 1 ? found[0] : throw new ArgumentException("Migration must exist uniquely.");
        }
        return new("database-migration-reviewer", "Database migration reviewer",
            "Run simulate_migration, review_rollback, and write_migration_review for each requested migration. This is an in-memory model, not real database execution. Do not claim SQL, transaction, lock or performance validation.",
            [new("simulate_migration", "Apply supported structured operations to a disposable in-memory schema and rows; record data loss and constraint failures.", schema, (a, ct) =>
            {
                var m = Migration(a); var before = c.Input["database"]?.AsObject() ?? throw new ArgumentException("database required.");
                ValidateDatabase(before);
                var after = (JsonObject)before.DeepClone(); var findings = new JsonArray();
                Apply(after, m["operations"] as JsonArray ?? throw new ArgumentException("operations required."), findings);
                var result = new JsonObject { ["migrationId"] = a.Text("migrationId"), ["simulation"] = "in-memory-only", ["before"] = before.DeepClone(), ["after"] = after, ["findings"] = findings, ["realDatabaseExecuted"] = false };
                c.State[a.Text("migrationId") + "-source"] = c.Input.ToJsonString();
                c.State[a.Text("migrationId")] = result.DeepClone(); return Task.FromResult<JsonNode?>(result);
            }), new("review_rollback", "Simulate declared rollback and compare schema plus row data to the original; report unsupported operations and loss.", schema, (a, ct) =>
            {
                var m = Migration(a);
                if (c.State[a.Text("migrationId") + "-source"]?.GetValue<string>() != c.Input.ToJsonString()) throw new InvalidOperationException("Migration evidence changed; simulate again.");
                var result = c.State[a.Text("migrationId")]?.AsObject() ?? throw new InvalidOperationException("Simulate this migration first.");
                var rollback = m["rollback"] as JsonArray; var findings = (JsonArray)result["findings"]!.DeepClone();
                var restored = (JsonObject)result["after"]!.DeepClone();
                if (rollback is null) findings.Add("Missing rollback operations.");
                else Apply(restored, rollback, findings);
                var match = JsonNode.DeepEquals(result["before"], restored);
                if (!match) findings.Add("Rollback does not restore original schema and row values.");
                result["rollbackRestoresOriginal"] = rollback is not null && match;
                result["reviewFindings"] = findings;
                result["status"] = findings.Count == 0 && rollback is not null && match ? "ready-for-database-validation" : "blocked";
                return Task.FromResult<JsonNode?>(result.DeepClone());
            }), new("write_migration_review", "Persist model results and database validation limitations to a local report.", schema, async (a, ct) =>
            {
                Migration(a);
                if (c.State[a.Text("migrationId") + "-source"]?.GetValue<string>() != c.Input.ToJsonString()) throw new InvalidOperationException("Migration evidence changed; simulate again.");
                var result = c.State[a.Text("migrationId")]?.AsObject();
                if (result?["status"] is null) throw new InvalidOperationException("Review rollback first.");
                var path = "migration-" + a.Text("migrationId") + ".json"; await c.WriteJsonAsync(path, result, ct);
                return new JsonObject { ["artifact"] = path, ["status"] = result.Text("status"), ["simulation"] = "in-memory-only" };
            })], [new("simulate_migration", new() { ["migrationId"] = "rename-display" }), new("review_rollback", new() { ["migrationId"] = "rename-display" }), new("write_migration_review", new() { ["migrationId"] = "rename-display" })]);
    }

    private static void ValidateDatabase(JsonObject database)
    {
        var tables = database["tables"] as JsonObject ?? throw new ArgumentException("tables object required.");
        foreach (var pair in tables)
        {
            var table = pair.Value?.AsObject() ?? throw new ArgumentException("Table object required.");
            var columns = table["columns"] as JsonObject ?? throw new ArgumentException("columns object required.");
            var rows = table["rows"] as JsonArray ?? throw new ArgumentException("rows array required.");
            foreach (var column in columns) if (column.Value is not JsonObject definition || definition["nullable"] is null) throw new ArgumentException("Each column requires nullable metadata.");
            foreach (var node in rows)
            {
                var row = node?.AsObject() ?? throw new ArgumentException("Rows must be objects.");
                if (row.Any(p => !columns.ContainsKey(p.Key))) throw new ArgumentException("Row has unknown column.");
                foreach (var column in columns)
                    if (column.Value!["nullable"]!.GetValue<bool>() == false && row[column.Key] is null) throw new ArgumentException("Existing row violates non-null column.");
            }
        }
    }

    private static void Apply(JsonObject database, JsonArray operations, JsonArray findings)
    {
        if (operations.Count == 0) findings.Add("No operations declared.");
        foreach (var node in operations)
        {
            if (node is not JsonObject op) { findings.Add("Invalid operation object."); continue; }
            var tableName = op.Text("table");
            if (database["tables"]?[tableName] is not JsonObject table) { findings.Add("Missing table: " + tableName); continue; }
            var columns = table["columns"]!.AsObject(); var rows = table["rows"]!.AsArray(); var name = op.Text("column");
            if (string.IsNullOrWhiteSpace(name)) { findings.Add("Missing column name."); continue; }
            switch (op.Text("kind"))
            {
                case "renameColumn":
                    var target = op.Text("to");
                    if (!columns.ContainsKey(name) || string.IsNullOrWhiteSpace(target) || columns.ContainsKey(target)) { findings.Add("Invalid rename: " + name); break; }
                    // Rebuild in place so a rename followed by its inverse retains original order.
                    Rename(columns, name, target);
                    foreach (var row in rows.OfType<JsonObject>()) if (row.ContainsKey(name)) Rename(row, name, target);
                    break;
                case "dropColumn":
                    if (!columns.ContainsKey(name)) { findings.Add("Missing dropped column: " + name); break; }
                    var lost = rows.OfType<JsonObject>().Count(r => r[name] is not null);
                    findings.Add($"Dropping {tableName}.{name} removes schema and {lost} non-null row values.");
                    columns.Remove(name); foreach (var row in rows.OfType<JsonObject>()) row.Remove(name);
                    break;
                case "addColumn":
                    if (columns.ContainsKey(name) || op["nullable"] is null) { findings.Add("Invalid added column: " + name); break; }
                    var nullable = op["nullable"]!.GetValue<bool>();
                    if (!nullable && op["default"] is null && rows.Count > 0) { findings.Add("Non-null addition lacks backfill: " + name); break; }
                    columns[name] = new JsonObject { ["nullable"] = nullable };
                    foreach (var row in rows.OfType<JsonObject>()) row[name] = op["default"]?.DeepClone();
                    break;
                default: findings.Add("Unsupported operation: " + op.Text("kind")); break;
            }
        }
    }

    private static void Rename(JsonObject value, string oldName, string newName)
    {
        var pairs = value.Select(p => new KeyValuePair<string, JsonNode?>(p.Key == oldName ? newName : p.Key, p.Value?.DeepClone())).ToArray();
        value.Clear(); foreach (var pair in pairs) value[pair.Key] = pair.Value;
    }
}
