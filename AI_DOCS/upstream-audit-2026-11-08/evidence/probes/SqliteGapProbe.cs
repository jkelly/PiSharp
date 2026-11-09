using System.Text.Json;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Testing;

var ctx = DurableContext.Background;
var db = await SqliteConnectionDatabase.OpenAsync(":memory:");
var storage = await SqliteStorage.OpenAsync(db);
await storage.CommitWritesAsync([new ConversationWrite(new(ConversationId.Root))], ctx);
var doc = await storage.MintIdAsync<DocumentId>();
await storage.CommitWritesAsync([new DocumentCreateWrite(new DocumentCreate { Id=doc, Kind="corrupt", Scope=DocumentScope.Session }, DocumentContent.Base(1, (JsonObjectValue)JsonValue.Parse("{\"n\":1}")))], ctx);
// Valid JSON with an invalid document-record shape passes the table's json_valid CHECK.
await db.RunAsync("UPDATE documents SET record = ? WHERE id = ?", "{}", doc.Value);
var entry = await storage.MintIdAsync<EntryId>();
var receipts = new List<int>();
bool resolved = false;
int? stored = null;
string? refusal = null;
string? rejection = null;
try
{
    var result = await storage.CommitAsync([[new EntryWrite(new() { Id=entry, ConversationId=ConversationId.Root, Kind="note" })], [new DocumentChangeWrite(doc, DocumentContent.Base(1,(JsonObjectValue)JsonValue.Parse("{\"n\":2}")))]], (index, seq)=>receipts.Add(index), ctx);
    resolved = true; stored = result.Stored; refusal = result.Refused?.GetType().Name;
}
catch(Exception ex) { rejection = ex.GetType().Name + ": " + ex.Message; }
var persisted = await storage.GetEntryAsync(entry,ctx) is not null;
Console.WriteLine(JsonSerializer.Serialize(new { productCommit = "14dd34b369fba6d236684da6dfc8a39a03263321", expected = "reject whole call, zero receipts, earlier entry absent", resolved, stored, refusal, rejection, receipts, firstEntryPersisted=persisted }, new JsonSerializerOptions { WriteIndented=true }));
await storage.CloseAsync(ctx);
