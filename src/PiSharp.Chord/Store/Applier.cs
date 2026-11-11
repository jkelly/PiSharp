// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/applier.ts
//
// The applier (slice 6a §13, slice 6b §1.6): recorded writes applied as they were recorded, inside a backend transaction, checked against the
// store's own records (a trust boundary: archives, a replica's batches). One code path for import, bootstrap and batch apply, for every
// write kind; a recorded count the store does not reproduce stops applying (Diverged, S17). A recorded write is its JSON here, as the
// outbox and archives carry it (upstream types it StoreWrite once checkWrite's schema passed, and reads its fields); the applier reads the
// fields it needs and decodes `where` and `set` into the expression vocabulary (Expression.cs) as it checks them, then applies the table
// writes with the table writes' own appliers (TableWrites.cs) and the documents with the engine's (Engine.cs). JavaScript's own-key order
// is the order a recorded map's entries are read in.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

/// <summary>A recorded write the store does not reproduce (a count, a revision): applying stops, the store diverged (S17; upstream
/// <c>Diverged</c>).</summary>
internal sealed class Diverged(string message) : Exception(message);

internal static partial class StoreInternal
{
    /// <summary>Applies <paramref name="write"/> in <paramref name="step"/>; a write the records refuse throws <c>invalid/args</c>, one they
    /// do not reproduce <see cref="Diverged"/> (upstream <c>applyWrite</c>).</summary>
    internal static async Task ApplyWriteAsync(Step step, JsonObject write, string what)
    {
        switch (ChordJson.AsString(write["kind"]))
        {
            case "table":
            {
                var definition = DefinitionOf(write);
                CheckTable(definition, what);
                await ApplyTableAsync(step, definition).ConfigureAwait(false);
                return;
            }
            case "index":
            {
                var index = new TableWrite.Index(TableOf(write), (string)write["name"]!, Names(write["columns"]));
                CheckIndex(RecordOf(step, index.Table, what), index, what);
                await ApplyIndexAsync(step, index).ConfigureAwait(false);
                return;
            }
            case "dropIndex":
            {
                var drop = new TableWrite.DropIndex(TableOf(write), (string)write["name"]!);
                if (!RecordOf(step, drop.Table, what).Indexes.ContainsKey(drop.Name))
                    throw DataInternal.Invalid("args", $"{what}: no index {ChordJson.Quote(drop.Name)}");
                await ApplyDropIndexAsync(step, drop).ConfigureAwait(false);
                return;
            }
            case "alter":
            {
                var alter = new TableWrite.Alter(TableOf(write), Integer(write["version"]), EditOf(write["edit"]!.AsObject()));
                CheckAlter(RecordOf(step, alter.Table, what), alter, what);
                await ApplyAlterAsync(step, alter).ConfigureAwait(false);
                return;
            }
            case "put":
            {
                var table = TableOf(write);
                var record = RecordOf(step, table, what);
                var (physical, sql) = RecordedPut(table, record.Columns);
                await step.RunAsync(sql, PutParams(record, write, physical, what)).ConfigureAwait(false);
                return;
            }
            case "update" or "delete" or "updateWhere" or "deleteWhere":
                await ApplyCountedAsync(step, write, what).ConfigureAwait(false);
                return;
            default:
                await ApplyDocumentAsync(step, write, what).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Applies a document write: a creation, a change or a retirement on its recorded revision, an archive's revision.</summary>
    private static async Task ApplyDocumentAsync(Step step, JsonObject write, string what)
    {
        switch (ChordJson.AsString(write["kind"]))
        {
            case "document.create":
            {
                var text = write.TryGetPropertyValue("base", out var @base) ? Json(@base!["value"], what) : null;
                await DocumentEngine.InsertDocumentAsync(step, RecordOfJson(write["record"]!.AsObject()), text).ConfigureAwait(false);
                return;
            }
            case "document.revision":
            {
                var content = write["content"]!.AsObject();
                await InsertRevisionAsync(step, Integer(write["id"]), Integer(write["seq"]), (string)content["kind"]!, Integer(content["version"]),
                    ContentText(content, what)).ConfigureAwait(false);
                return;
            }
            case "document.change":
            {
                var (id, revision) = (Integer(write["id"]), Integer(write["revision"]));
                var record = await LiveAtAsync(step, id, revision, what).ConfigureAwait(false);
                var content = write["content"]!.AsObject();
                var text = ContentText(content, what);
                DocumentContent decoded = (string)content["kind"]! == "base"
                    ? new DocumentContent.Base(Integer(content["version"]), (string)content["fingerprint"]!, content["value"])
                    : new DocumentContent.Delta(Integer(content["version"]), content["ops"]!.AsArray());
                await DocumentEngine.WriteContentAsync(step, id, record.History, revision, decoded, text).ConfigureAwait(false);
                return;
            }
            case "document.retire":
            {
                var record = await LiveAtAsync(step, Integer(write["id"]), Integer(write["revision"]), what).ConfigureAwait(false);
                await DocumentEngine.RetireRecordAsync(step, record).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>A content's stored text: a base's value or a delta's ops, checked as JSON.</summary>
    private static string ContentText(JsonObject content, string what) =>
        Json((string?)content["kind"] == "base" ? content["value"] : content["ops"], what);

    /// <summary>The live record <paramref name="id"/> at <paramref name="revision"/>; another revision, or none, is a divergence.</summary>
    private static async Task<DocumentRecord> LiveAtAsync(Step step, long id, long revision, string what)
    {
        var record = await DocumentEngine.ReadDocumentAsync(step, id).ConfigureAwait(false);
        if (record is null || record.RetiredAt is not null || record.Revision != revision)
            throw new Diverged($"{what}: document {id} is not live at revision {revision}");
        return record;
    }

    /// <summary><paramref name="value"/> serialized; one <c>JSON.stringify</c> cannot encode is refused.</summary>
    private static string Json(JsonNode? value, string what)
    {
        try
        {
            return ChordJson.Stringify(value);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw DataInternal.Invalid("args", $"{what}: not JSON ({error.Message})");
        }
    }

    /// <summary>Applies a counted table write: its statement rendered from the record's columns and the recorded key, where and set
    /// (checked: only recorded columns, well-formed nodes), and its count compared with the recorded one.</summary>
    private static async Task ApplyCountedAsync(Step step, JsonObject write, string what)
    {
        var record = RecordOf(step, TableOf(write), what);
        var sql = new TableSql(record.Table, record.Columns.Select(entry => (entry.Key, entry.Value.Type)), [], record.Key);
        Statement statement;
        switch ((string)write["kind"]!)
        {
            case "update":
            {
                var set = SetOf(record, write, what);
                statement = sql.Update(set, KeyParams(record, write["key"]!.AsObject(), what), VersionsOf(record, write, what));
                break;
            }
            case "delete":
                statement = new Statement(sql.Delete, KeyParams(record, write["key"]!.AsObject(), what));
                break;
            case "updateWhere":
            {
                var where = CheckWhere(record, write["where"], what);
                statement = sql.UpdateWhere(where, SetOf(record, write, what), VersionsOf(record, write, what));
                break;
            }
            default:
                statement = sql.DeleteWhere(CheckWhere(record, write["where"], what));
                break;
        }
        var count = await step.RunCountedAsync(statement.Sql, [.. statement.Params]).ConfigureAwait(false);
        var recorded = Integer(write["count"]);
        if (count != recorded) throw new Diverged($"{what}: changed {count} rows, recorded {recorded}");
    }

    /// <summary>The recorded key as parameters in key order: exactly the key columns, each of its type.</summary>
    private static List<object?> KeyParams(TableRecord record, JsonObject key, string what)
    {
        if (key.Count != record.Key.Count) throw DataInternal.Invalid("args", $"{what}: not a key of {record.Table}");
        return [.. record.Key.Select(name =>
        {
            if (!key.TryGetPropertyValue(name, out var value)) throw DataInternal.Invalid("args", $"{what}: key column {ChordJson.Quote(name)} is missing");
            return (object?)ToSqlite(CheckScalar(ScalarType(record.Columns[name]), value, $"{what}: key"));
        })];
    }

    /// <summary>A recorded set with the migration stamp <c>$v</c> when it carries one; key columns never change (rule 13).</summary>
    private static SetNode SetOf(TableRecord record, JsonObject write, string what)
    {
        var raw = write["set"];
        if (raw is JsonArray { Count: > 0 })
            // Object.entries of an array: its first index is the first column it writes.
            throw DataInternal.Invalid("args", $"{what}: set writes \"0\", no non-key column");
        if (raw is not JsonObject set || set.Count == 0) throw DataInternal.Invalid("args", $"{what}: an empty or malformed set");
        var node = new SetNode();
        foreach (var column in ChordJson.KeysInJavaScriptOrder(set))
        {
            if (!record.Columns.ContainsKey(column) || record.Key.Contains(column))
                throw DataInternal.Invalid("args", $"{what}: set writes {ChordJson.Quote(column)}, no non-key column");
            node[column] = CheckExpr(record, set[column], what);
        }
        if (write["version"] is { } version) node[RowVersion] = new ExprNode.Literal((double)Integer(version));
        return node;
    }

    /// <summary>The <c>json(...)</c> versions a set stamps: the recorded ones, of <c>json(...)</c> columns it sets, up to theirs.</summary>
    private static OrderedDictionary<string, long> VersionsOf(TableRecord record, JsonObject write, string what)
    {
        var versions = new OrderedDictionary<string, long>(StringComparer.Ordinal);
        var set = write["set"]!.AsObject();
        var recorded = write["columns"] as JsonObject;
        foreach (var (name, column) in record.Columns)
        {
            if (column.Type != ColumnType.Json || !set.ContainsKey(name)) continue;
            // A clear stamps NULL whatever the version; a value stamps its recorded version.
            var given = recorded is not null && recorded.TryGetPropertyValue(name, out var stamped) && stamped is not null ? ChordJson.AsNumber(stamped) : null;
            var version = given ?? column.Version!.Value;
            if (!IsSafeInteger(version) || version < 1 || version > column.Version!.Value)
                throw DataInternal.Invalid("args", $"{what}: no readable version for {ChordJson.Quote(name)}");
            versions[name] = (long)version;
        }
        return versions;
    }

    /// <summary>A scalar column's type; <c>json(...)</c> columns are not scalars.</summary>
    private static ColumnType ScalarType(ColumnRecord column) => column.Type == ColumnType.Json ? ColumnType.Str : column.Type;

    /// <summary>A recorded condition: well-formed nodes over recorded scalar columns (and the row's <c>$v</c>), as the vocabulary's
    /// node.</summary>
    private static WhereNode CheckWhere(TableRecord record, JsonNode? node, string what)
    {
        Exception Refuse() => DataInternal.Invalid("args", $"{what}: a malformed condition");
        if (node is not JsonObject { Count: 1 } single) throw Refuse();
        var (op, operand) = single.First();
        string Column(JsonNode? name)
        {
            var column = ChordJson.AsString(name);
            if (column == RowVersion) return column;
            if (column is null || !record.Columns.TryGetValue(column, out var recorded) || recorded.Type == ColumnType.Json) throw Refuse();
            return column;
        }
        switch (op)
        {
            case "and" or "or":
            {
                if (operand is not JsonArray each) throw Refuse();
                List<WhereNode> nodes = [.. each.Select(item => CheckWhere(record, item, what))];
                return op == "and" ? new WhereNode.And(nodes) : new WhereNode.Or(nodes);
            }
            case "not":
                return new WhereNode.Not(CheckWhere(record, operand, what));
            case "isNull":
                return new WhereNode.IsNull(Column(operand));
        }
        if (op != "inArray" && op != "prefix" && !Comparisons.ContainsKey(op)) throw Refuse();
        if (operand is not JsonArray { Count: 2 } pair) throw Refuse();
        var name = Column(pair[0]);
        var values = op == "inArray" ? pair[1] as JsonArray : new JsonArray(pair[1]?.DeepClone());
        if (values is null) throw Refuse();
        var scalars = new List<Scalar>();
        foreach (var value in values)
        {
            if (ScalarOf(value) is not { } scalar || (op == "prefix" && scalar.Value is not string)) throw Refuse();
            scalars.Add(scalar);
        }
        return op switch
        {
            "inArray" => new WhereNode.InArray(name, scalars),
            "prefix" => new WhereNode.Prefix(name, (string)scalars[0].Value),
            _ => new WhereNode.Compare(Comparisons[op], name, scalars[0]),
        };
    }

    /// <summary>A recorded expression: scalars, <c>null</c>, JSON literals, recorded columns and the vocabulary's operations.</summary>
    private static ExprNode? CheckExpr(TableRecord record, JsonNode? node, string what)
    {
        if (node is null) return null;
        if (ScalarOf(node) is { } scalar) return new ExprNode.Literal(scalar);
        Exception Refuse() => DataInternal.Invalid("args", $"{what}: a malformed expression");
        if (node is not JsonObject { Count: 1 } single) throw Refuse();
        var (op, operand) = single.First();
        if (op == "col")
        {
            if (ChordJson.AsString(operand) is not { } column || !record.Columns.ContainsKey(column)) throw Refuse();
            return new ExprNode.Col(column);
        }
        if (op == "json") return new ExprNode.Json(operand?.DeepClone());
        if (operand is not JsonArray each) throw Refuse();
        if (OperationOf(op) is { } operation && each.Count == 2) return new ExprNode.Binary(operation, CheckExpr(record, each[0], what), CheckExpr(record, each[1], what));
        if (op == "replacePrefix" && each.Count == 3)
        {
            var subject = CheckExpr(record, each[0], what);
            if (ChordJson.AsString(each[1]) is not { } from || ChordJson.AsString(each[2]) is not { } to) throw Refuse();
            return new ExprNode.ReplacePrefix(subject, from, to);
        }
        if (op == "when" && each.Count == 3)
            return new ExprNode.When(CheckWhere(record, each[0], what), CheckExpr(record, each[1], what), CheckExpr(record, each[2], what));
        throw Refuse();
    }

    /// <summary>A binary operation by name.</summary>
    private static Operation? OperationOf(string name) => name switch
    {
        "add" => Operation.Add,
        "sub" => Operation.Sub,
        "max" => Operation.Max,
        "min" => Operation.Min,
        "concat" => Operation.Concat,
        "coalesce" => Operation.Coalesce,
        _ => null,
    };

    /// <summary>A recorded table's SQL name (two names joined by <c>:</c>, R2), columns, key and json versions, by the name rules.</summary>
    private static void CheckTable(TableWrite.Definition write, string what)
    {
        var parts = write.Table.Split(':');
        if (parts.Length != 2) throw DataInternal.Invalid("args", $"{what}: table {ChordJson.Quote(write.Table)}: not owner:table");
        try
        {
            foreach (var part in parts) ContractsInternal.CheckName(part, $"{what}: table name part");
        }
        catch (ArgumentException error)
        {
            // CheckName throws plain errors.
            throw DataInternal.Invalid("args", error.Message);
        }
        var folded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, column) in write.Columns)
        {
            var where = $"{what}: column {ChordJson.Quote(name)}";
            if (!ColumnName.IsMatch(name) || !folded.Add(name.ToLowerInvariant())) throw DataInternal.Invalid("args", $"{where}: not a column name");
            CheckColumn(column, where);
        }
        if (write.Columns.Values.Count(column => column.Host is not null) > 1)
            throw DataInternal.Invalid("args", $"{what}: more than one host(...) column");
        if (write.Key.Count == 0) throw DataInternal.Invalid("args", $"{what}: the key has no column");
        CheckOrderColumns(write.Columns, write.Key, $"{what}: the key");
    }

    private static void CheckColumn(ColumnRecord column, string what)
    {
        if (column.Host is { } host)
        {
            if (column.Type != ColumnType.Str || column.Optional || column.Default is not null)
                throw DataInternal.Invalid("args", $"{what}: a host(...) column is a required str without a default");
            try { ContractsInternal.CheckName(host, $"{what}: host kind"); }
            catch (ArgumentException error) { throw DataInternal.Invalid("args", error.Message); }
        }
        var json = column.Type == ColumnType.Json;
        if (json != (column.Version is not null) || json != (column.Fingerprint is not null))
            throw DataInternal.Invalid("args", $"{what}: a json(...) column has a version and a fingerprint, no other column has");
        if (column.Default is not { } value) return;
        if (json || column.Optional) throw DataInternal.Invalid("args", $"{what}: only a non-opt scalar column has a default");
        CheckScalar(column.Type, value.ToNode(), $"{what}: the default");
    }

    /// <summary>A recorded column edit of the record's next version or its own (the edits after a data step).</summary>
    private static void CheckAlter(TableRecord record, TableWrite.Alter write, string what)
    {
        if (write.Version != record.Version && write.Version != record.Version + 1)
            throw DataInternal.Invalid("args", $"{what}: an edit of version {write.Version} on version {record.Version}");
        bool Has(string name) => record.Columns.ContainsKey(name);
        bool Fresh(string name) => ColumnName.IsMatch(name) && !record.Columns.Keys.Any(each => string.Equals(each, name, StringComparison.OrdinalIgnoreCase));
        bool Indexed(string name) => record.Key.Contains(name) || record.Indexes.Values.Any(columns => columns.Contains(name));
        var valid = write.Edit switch
        {
            AlterEdit.Rename rename => Has(rename.From) && Fresh(rename.To),
            AlterEdit.Add add => Fresh(add.Name) && (add.Column.Optional || add.Column.Default is not null),
            AlterEdit.Drop drop => Has(drop.Name) && !Indexed(drop.Name),
            AlterEdit.Json edit => Has(edit.Name) && record.Columns[edit.Name].Type == ColumnType.Json && edit.Column.Type == ColumnType.Json,
            _ => false,
        };
        if (!valid) throw DataInternal.Invalid("args", $"{what}: an {write.Edit.Kind} edit the table cannot take");
        if (write.Edit is AlterEdit.Add added) CheckColumn(added.Column, $"{what}: column {ChordJson.Quote(added.Name)}");
        if (write.Edit is AlterEdit.Json changed) CheckColumn(changed.Column, $"{what}: column {ChordJson.Quote(changed.Name)}");
    }

    /// <summary><paramref name="names"/> are distinct, recorded, non-<c>opt</c> <c>str</c> or <c>int</c> columns.</summary>
    private static void CheckOrderColumns(IReadOnlyDictionary<string, ColumnRecord> columns, IReadOnlyList<string> names, string what)
    {
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) throw DataInternal.Invalid("args", $"{what}: a column occurs twice");
        foreach (var name in names)
        {
            if (!columns.TryGetValue(name, out var column) || column.Optional || (column.Type != ColumnType.Str && column.Type != ColumnType.Int))
                throw DataInternal.Invalid("args", $"{what}: {ChordJson.Quote(name)} is not a non-opt str or int column");
        }
    }

    private static void CheckIndex(TableRecord record, TableWrite.Index write, string what)
    {
        if (!IndexName.IsMatch(write.Name) || record.Indexes.ContainsKey(write.Name))
            throw DataInternal.Invalid("args", $"{what}: index {ChordJson.Quote(write.Name)}: not a new index name");
        CheckOrderColumns(record.Columns, write.Columns, $"{what}: index {write.Name}");
    }

    /// <summary>The record of <paramref name="table"/> in <paramref name="step"/>; a write for a table not recorded is refused.</summary>
    private static TableRecord RecordOf(Step step, string table, string what) =>
        step.Table(table) ?? throw DataInternal.Invalid("args", $"{what}: table {ChordJson.Quote(table)} is not recorded");

    /// <summary>The parameters of a put in <paramref name="physical"/> order, checked against the recorded columns: every non-<c>opt</c>
    /// column, no other field, values of their types, <c>json(...)</c> cells as JSON (no plugin code runs) with a version up to the
    /// column's, and the row's version up to the table's.</summary>
    private static object?[] PutParams(TableRecord record, JsonObject write, IReadOnlyList<string> physical, string what)
    {
        var row = write["row"]!.AsObject();
        var versions = write["columns"]!.AsObject();
        var version = Integer(write["version"]);
        foreach (var field in ChordJson.KeysInJavaScriptOrder(row))
        {
            if (!record.Columns.ContainsKey(field)) throw DataInternal.Invalid("args", $"{what}: unknown column {ChordJson.Quote(field)}");
        }
        foreach (var field in ChordJson.KeysInJavaScriptOrder(versions))
        {
            if (!record.Columns.TryGetValue(field, out var column) || column.Type != ColumnType.Json || !row.ContainsKey(field))
                throw DataInternal.Invalid("args", $"{what}: a version for {ChordJson.Quote(field)}, which is no json(...) cell of the row");
        }
        if (version > record.Version)
            throw DataInternal.Invalid("args", $"{what}: version {version} is newer than the table's version {record.Version}");
        var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, column) in record.Columns)
        {
            var where = $"{what}: column {ChordJson.Quote(name)}";
            // A row has no explicit undefined: an absent field is NULL; a json(...) cell's null is the JSON value.
            if (!row.TryGetPropertyValue(name, out var value))
            {
                if (!column.Optional) throw DataInternal.Invalid("args", $"{where} is missing");
                cells[name] = null;
                continue;
            }
            if (column.Type != ColumnType.Json)
            {
                cells[name] = ToSqlite(CheckScalar(column.Type, value, where));
                continue;
            }
            var stamped = versions.TryGetPropertyValue(name, out var given) ? ChordJson.AsNumber(given) : null;
            if (stamped is null || stamped > column.Version) throw DataInternal.Invalid("args", $"{where}: no readable version");
            cells[name] = Json(value, where);
            cells[VersionColumn(name)] = (long)stamped.Value;
        }
        return [.. physical.Select(name => name == RowVersion ? version : cells.GetValueOrDefault(name))];
    }

    /// <summary>A <c>table</c> write of its JSON (the columns in JavaScript's own-key order).</summary>
    private static TableWrite.Definition DefinitionOf(JsonObject write)
    {
        var columns = new OrderedDictionary<string, ColumnRecord>(StringComparer.Ordinal);
        var json = write["columns"]!.AsObject();
        foreach (var name in ChordJson.KeysInJavaScriptOrder(json)) columns[name] = ParseColumnRecord(json[name]!.AsObject());
        return new TableWrite.Definition(TableOf(write), Integer(write["version"]),
            (string)write["state"]! == "ready" ? TableState.Ready : TableState.Migrating, columns, Names(write["key"]), (string)write["fingerprint"]!);
    }

    /// <summary>An <c>alter</c> write's edit of its JSON.</summary>
    private static AlterEdit EditOf(JsonObject edit) => (string)edit["kind"]! switch
    {
        "rename" => new AlterEdit.Rename((string)edit["from"]!, (string)edit["to"]!),
        "add" => new AlterEdit.Add((string)edit["name"]!, ParseColumnRecord(edit["column"]!.AsObject())),
        "drop" => new AlterEdit.Drop((string)edit["name"]!),
        _ => new AlterEdit.Json((string)edit["name"]!, ParseColumnRecord(edit["column"]!.AsObject())),
    };

    /// <summary>A document record of its JSON (upstream's <c>RECORD</c> shape).</summary>
    internal static DocumentRecord RecordOfJson(JsonObject record) => new()
    {
        Id = Integer(record["id"]),
        Owner = (string)record["owner"]!,
        Namespace = (string)record["namespace"]!,
        Kind = (string)record["kind"]!,
        Key = ChordJson.AsString(record["key"]),
        History = ParseHistory((string)record["history"]!),
        CreatedAt = Integer(record["createdAt"]),
        RetiredAt = record["retiredAt"] is { } retired ? Integer(retired) : null,
        Meta = ChordJson.AsString(record["meta"]),
        Host = ChordJson.AsString(record["host"]),
        Revision = Integer(record["revision"]),
        Version = Integer(record["version"]),
        Fingerprint = (string)record["fingerprint"]!,
    };

    /// <summary>A write's table name.</summary>
    private static string TableOf(JsonObject write) => (string)write["table"]!;

    /// <summary>A JSON number of a checked write as an integer.</summary>
    internal static long Integer(JsonNode? node) => (long)(ChordJson.AsNumber(node) ?? throw DataInternal.Invalid("args", "store: a recorded number is missing"));

}
