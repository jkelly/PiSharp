using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Context;

public enum NativeSessionDiagnosticProvenance { DeclaredNativeRecord, UnverifiedLegacyObservation }
public enum NativeSessionDiagnosticReadFailure { InvalidRecord, UnresolvedAssistant, ResourceLimit }
public sealed record NativeSessionDiagnosticObservation(string? RecordEntryId, string AssistantEntryId,
    long? OperationGeneration, NativeChatDiagnostic? Diagnostic, NativeChatDiagnostic? CleanupDiagnostic,
    NativeSessionDiagnosticProvenance Provenance);
public sealed record NativeSessionDiagnosticReadIssue(string EntryId, NativeSessionDiagnosticReadFailure Failure);
public sealed record NativeSessionDiagnosticView(ImmutableArray<NativeSessionDiagnosticObservation> Entries,
    ImmutableArray<NativeSessionDiagnosticReadIssue> Issues);

/// <summary>Reads selected raw ancestry, including omitted assistants. Declared records and legacy
/// observations are file data; neither grants execution, retry or authenticated-origin authority.</summary>
public static class NativeSessionDiagnosticProjector
{
    public const string CustomType = "pisharp.native-chat-diagnostic.v1";
    public const int SchemaVersion = 1;
    public const int MaximumDataCharacters = 4096;
    public const int MaximumObservations = 1024;
    public const int MaximumIdentityCharacters = 256;
    public const int MaximumViewBytes = 4_194_304;

    public static NativeSessionDiagnosticView Project(SessionContextProjection context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if(context.Ancestry.IsDefault)throw new ArgumentException("Initialized selected ancestry is required.",nameof(context));
        var rows=new Queue<(NativeSessionDiagnosticObservation? Observation,NativeSessionDiagnosticReadIssue? Issue)>();
        string? firstDiscarded=null;
        var assistants=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in context.Ancestry)
        {
            cancellationToken.ThrowIfCancellationRequested();var body=entry.WireBody.Value;
            if(entry.Kind==SessionEntryKind.Message&&body.GetProperty("message") is var message&&
                message.GetProperty("role").GetString()=="assistant")
            {
                assistants.Add(entry.Id);
                if(message.TryGetProperty("openAICompletionsFailure",out var legacy)&&
                    legacy.ValueKind==JsonValueKind.String&&TryCode(legacy.GetString(),out var code))
                {
                    if(ValidIdentity(entry.Id))Add(new(null,entry.Id,null,new(NativeChatAdapter.OpenAICompletions,code),null,
                        NativeSessionDiagnosticProvenance.UnverifiedLegacyObservation),null);
                    else Add(null,new("",NativeSessionDiagnosticReadFailure.InvalidRecord));
                }
            }
            else if(entry.Kind==SessionEntryKind.Custom&&body.GetProperty("customType").GetString()==CustomType)
            {
                if(!ValidIdentity(entry.Id)||!body.TryGetProperty("data",out var data)||!TryRecord(data,out var observation))
                    Add(null,new(IssueIdentity(entry.Id),NativeSessionDiagnosticReadFailure.InvalidRecord));
                else if(!assistants.Contains(observation!.AssistantEntryId))
                    Add(null,new(entry.Id,NativeSessionDiagnosticReadFailure.UnresolvedAssistant));
                else Add(observation! with {RecordEntryId=entry.Id},null);
            }
        }
        var entries=rows.Where(row=>row.Observation is not null).Select(row=>row.Observation!).ToImmutableArray();
        var issues=rows.Where(row=>row.Issue is not null).Select(row=>row.Issue!).ToImmutableArray();
        if(firstDiscarded is not null)issues=issues.Insert(0,new(firstDiscarded,NativeSessionDiagnosticReadFailure.ResourceLimit));
        return new(entries,issues);
        void Add(NativeSessionDiagnosticObservation? observation,NativeSessionDiagnosticReadIssue? issue)
        {
            // Keep the most recent association available to the live event sink. Once truncated,
            // reserve one row for an explicit limit issue rather than failing a later operation.
            var capacity=firstDiscarded is null?MaximumObservations:MaximumObservations-1;
            if(rows.Count>=capacity)
            {
                var discarded=rows.Dequeue();
                firstDiscarded??=discarded.Observation?.RecordEntryId??discarded.Observation?.AssistantEntryId??discarded.Issue!.EntryId;
                if(rows.Count>=MaximumObservations-1)rows.Dequeue();
            }
            rows.Enqueue((observation,issue));
        }
    }

    // Pi records every adapter's failure diagnostics on the assistant message; any declared native adapter is recorded here.
    public static bool IsValid(NativeChatDiagnostic? diagnostic)=>diagnostic is null||
        AdapterName(diagnostic.Adapter) is not null&&Enum.IsDefined(diagnostic.Code);
    /// <summary>Stable wire names of the declared native adapters; null for an undeclared value.</summary>
    public static string? AdapterName(NativeChatAdapter adapter)=>adapter switch
    {
        NativeChatAdapter.OpenAICompletions=>"openai-completions",NativeChatAdapter.PiMessages=>"pi-messages",
        NativeChatAdapter.GoogleGenerativeAI=>"google-generative-ai",NativeChatAdapter.MistralConversations=>"mistral-conversations",_=>null
    };
    private static bool TryAdapter(string? name,out NativeChatAdapter adapter)
    {
        foreach(var value in Enum.GetValues<NativeChatAdapter>())if(AdapterName(value)==name){adapter=value;return true;}
        adapter=default;return false;
    }

    public static JsonData RecordData(string assistantEntryId,long operationGeneration,
        NativeChatDiagnostic? diagnostic,NativeChatDiagnostic? cleanupDiagnostic)
    {
        if(!ValidIdentity(assistantEntryId)||operationGeneration<=0||
            operationGeneration>9_007_199_254_740_991L||diagnostic is null&&cleanupDiagnostic is null||
            !IsValid(diagnostic)||!IsValid(cleanupDiagnostic))throw new ArgumentException("Invalid native diagnostic record.");
        var data=Encode(writer=>
        {
            writer.WriteStartObject();writer.WriteNumber("schemaVersion",SchemaVersion);
            writer.WriteString("assistantEntryId",assistantEntryId);writer.WriteNumber("operationGeneration",operationGeneration);
            WriteCarrier(writer,"diagnostic",diagnostic);WriteCarrier(writer,"cleanupDiagnostic",cleanupDiagnostic);writer.WriteEndObject();
        });
        if(data.Value.GetRawText().Length>MaximumDataCharacters)throw new ArgumentException("Native diagnostic data exceeds its bound.");
        return data;
    }

    public static JsonData ToWire(NativeSessionDiagnosticView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if(view.Entries.IsDefault||view.Issues.IsDefault||view.Entries.Length+view.Issues.Length>MaximumObservations)
            throw new ArgumentException("Invalid native diagnostic view.",nameof(view));
        return Encode(writer=>
        {
            writer.WriteStartObject();writer.WriteNumber("schemaVersion",SchemaVersion);writer.WritePropertyName("entries");writer.WriteStartArray();
            foreach(var row in view.Entries)WriteObservation(writer,row);
            writer.WriteEndArray();writer.WritePropertyName("issues");writer.WriteStartArray();
            foreach(var row in view.Issues)
            {
                if(row is null||row.EntryId is null||row.EntryId.Length>MaximumIdentityCharacters||!Enum.IsDefined(row.Failure))
                    throw new ArgumentException("Invalid native diagnostic read issue.",nameof(view));
                writer.WriteStartObject();writer.WriteString("entryId",row.EntryId);writer.WriteString("failure",row.Failure.ToString());writer.WriteEndObject();
            }
            writer.WriteEndArray();writer.WriteEndObject();
        });
    }

    public static void WriteObservation(Utf8JsonWriter writer,NativeSessionDiagnosticObservation row)
    {
        ArgumentNullException.ThrowIfNull(writer);ArgumentNullException.ThrowIfNull(row);
        if(!ValidIdentity(row.AssistantEntryId)||row.Diagnostic is null&&row.CleanupDiagnostic is null||
            !IsValid(row.Diagnostic)||!IsValid(row.CleanupDiagnostic)||!Enum.IsDefined(row.Provenance)||
            row.Provenance==NativeSessionDiagnosticProvenance.DeclaredNativeRecord&&
                (!ValidIdentity(row.RecordEntryId)||row.OperationGeneration is not (>0 and <=9_007_199_254_740_991L))||
            row.Provenance==NativeSessionDiagnosticProvenance.UnverifiedLegacyObservation&&
                (row.RecordEntryId is not null||row.OperationGeneration is not null))
            throw new ArgumentException("Invalid native diagnostic observation.",nameof(row));
        writer.WriteStartObject();writer.WriteString("recordEntryId",row.RecordEntryId);writer.WriteString("assistantEntryId",row.AssistantEntryId);
        if(row.OperationGeneration is { } generation)writer.WriteNumber("operationGeneration",generation);
        WriteCarrier(writer,"diagnostic",row.Diagnostic);WriteCarrier(writer,"cleanupDiagnostic",row.CleanupDiagnostic);
        writer.WriteString("provenance",row.Provenance==NativeSessionDiagnosticProvenance.DeclaredNativeRecord?"declared-native-record":"unverified-legacy-observation");writer.WriteEndObject();
    }

    private static bool TryRecord(JsonElement data,out NativeSessionDiagnosticObservation? observation)
    {
        observation=null;
        if(data.ValueKind!=JsonValueKind.Object||data.GetRawText().Length>MaximumDataCharacters||
            !data.TryGetProperty("schemaVersion",out var schema)||schema.ValueKind!=JsonValueKind.Number||!schema.TryGetInt32(out var version)||version!=SchemaVersion||
            !data.TryGetProperty("assistantEntryId",out var id)||id.ValueKind!=JsonValueKind.String||
            !ValidIdentity(id.GetString())||
            !data.TryGetProperty("operationGeneration",out var generation)||generation.ValueKind!=JsonValueKind.Number||!generation.TryGetInt64(out var value)||value<=0||value>9_007_199_254_740_991L||
            !TryCarrier(data,"diagnostic",out var diagnostic)||!TryCarrier(data,"cleanupDiagnostic",out var cleanup)||
            diagnostic is null&&cleanup is null)return false;
        observation=new(null,id.GetString()!,value,diagnostic,cleanup,NativeSessionDiagnosticProvenance.DeclaredNativeRecord);return true;
    }
    private static bool TryCarrier(JsonElement data,string name,out NativeChatDiagnostic? diagnostic)
    {
        diagnostic=null;if(!data.TryGetProperty(name,out var carrier))return true;
        if(carrier.ValueKind!=JsonValueKind.Object||!carrier.TryGetProperty("adapter",out var adapter)||
            adapter.ValueKind!=JsonValueKind.String||!TryAdapter(adapter.GetString(),out var declared)||
            !carrier.TryGetProperty("code",out var code)||code.ValueKind!=JsonValueKind.String||!TryCode(code.GetString(),out var parsed))return false;
        diagnostic=new(declared,parsed);return true;
    }
    private static bool TryCode(string? text,out NativeChatFailureCode code)=>
        Enum.TryParse(text,false,out code)&&Enum.IsDefined(code)&&Enum.GetName(code)==text;
    private static bool ValidIdentity(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=MaximumIdentityCharacters;
    private static string IssueIdentity(string value)=>ValidIdentity(value)?value:"";
    private static void WriteCarrier(Utf8JsonWriter writer,string name,NativeChatDiagnostic? diagnostic)
    {
        if(diagnostic is null)return;writer.WritePropertyName(name);writer.WriteStartObject();
        writer.WriteString("adapter",AdapterName(diagnostic.Adapter));writer.WriteString("code",diagnostic.Code.ToString());writer.WriteEndObject();
    }
    private static JsonData Encode(Action<Utf8JsonWriter> write)
    {
        using var bytes=new MemoryStream();using(var writer=new Utf8JsonWriter(bytes))write(writer);
        if(bytes.Length>MaximumViewBytes)throw new ArgumentException("Native diagnostic envelope exceeds its byte bound.");
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }
}
