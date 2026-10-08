using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

internal static class NativeSessionDiagnosticViewTests
{
    private static readonly SessionEntryCodec Codec=new();
    public static (string Name,Func<Task> Run)[] Cases()=>
    [
        ("native-diagnostic selected ancestry keeps separate semantic cleanup codes without sibling authority",SelectedBranch),
        ("native-diagnostic omitted assistant remains raw billable and correlated without model metadata",OmittedBilling),
        ("native-diagnostic legacy marker stays unverified and all opaque source bytes survive reading",Legacy),
        ("native-diagnostic malformed numeric schema adapter code and unresolved reference become bounded issues",Malformed),
        ("native-diagnostic enum and bounded newest view preserve current correlation after truncation",Bounds)
    ];
    private static SessionEntry Entry(string type,string id,string? parent,object fields)
    {
        var extra=JsonSerializer.Serialize(fields)[1..^1];
        return Codec.Parse("{\"type\":"+JsonSerializer.Serialize(type)+",\"id\":"+JsonSerializer.Serialize(id)+
            ",\"parentId\":"+JsonSerializer.Serialize(parent)+",\"timestamp\":\"1970-01-01T00:00:00.001Z\","+extra+"}");
    }
    private static SessionEntry Assistant(string id,string? parent,string? legacy=null)
    {
        var value=new AssistantMessage("openai-completions","fixture","model",1,[new TextContent("owned test assistant")],
            new(100,1,0,0,101,new(.1m,.001m,0,0,.101m)),StopReason.Error);
        if(legacy is not null)value=value with{ExtraProperties=JsonFields.Empty.Set("openAICompletionsFailure",JsonData.Parse(JsonSerializer.Serialize(legacy))).Set("opaque",JsonData.Parse("{\"nullValue\":null,\"tail\":[1,\"unchanged\"]}"))};
        return Entry("message",id,parent,new{message=PiWireJson.WriteMessage(value).Value});
    }
    private static SessionEntry Diagnostic(string id,string parent,string assistant,NativeChatFailureCode primary,NativeChatFailureCode? cleanup=null)=>
        Entry("custom",id,parent,new{customType=NativeSessionDiagnosticProjector.CustomType,
            data=NativeSessionDiagnosticProjector.RecordData(assistant,1,new(NativeChatAdapter.OpenAICompletions,primary),
                cleanup is { } code?new(NativeChatAdapter.OpenAICompletions,code):null).Value});
    private static Task SelectedBranch()
    {
        var a=Assistant("a",null);var left=Diagnostic("left","a","a",NativeChatFailureCode.ProviderError,NativeChatFailureCode.CleanupFailed);
        var b=Assistant("b","a");var right=Diagnostic("right","b","b",NativeChatFailureCode.UnexpectedEof);
        ImmutableArray<SessionEntry> entries=[a,left,b,right];var projector=new SessionContextProjector();
        var selected=NativeSessionDiagnosticProjector.Project(projector.Project(entries,"left"));Equal(1,selected.Entries.Length);Equal(0,selected.Issues.Length);
        var row=selected.Entries.Single();Equal("a",row.AssistantEntryId);Equal("left",row.RecordEntryId);Equal(1L,row.OperationGeneration);
        Equal(NativeChatFailureCode.ProviderError,row.Diagnostic!.Code);Equal(NativeChatFailureCode.CleanupFailed,row.CleanupDiagnostic!.Code);
        Equal(NativeSessionDiagnosticProvenance.DeclaredNativeRecord,row.Provenance);
        Equal("b",NativeSessionDiagnosticProjector.Project(projector.Project(entries,"right")).Entries.Single().AssistantEntryId);
        Check(NativeSessionDiagnosticProjector.Project(projector.Project(entries,null)).Entries.IsEmpty,"Explicit root acquired sibling metadata.");
        var wire=NativeSessionDiagnosticProjector.ToWire(selected).Value;Equal(1,wire.GetProperty("schemaVersion").GetInt32());
        Equal("declared-native-record",wire.GetProperty("entries")[0].GetProperty("provenance").GetString());return Task.CompletedTask;
    }
    private static Task OmittedBilling()
    {
        var a=Assistant("a",null);var diagnostic=Diagnostic("diagnostic","a","a",NativeChatFailureCode.SourceFailed);
        var omission=Entry("context_edit","omit","diagnostic",new{targetId="a",replacement=(object?)null});
        var history=new SessionHistoryProjector().Project([a,diagnostic,omission],"omit");
        Check(history.Context.LlmMessages.IsEmpty,"Omitted failed assistant or native metadata leaked into model context.");
        Equal(101d,history.BranchStatistics.Totals!.Total);Equal("a",NativeSessionDiagnosticProjector.Project(history.Context).Entries.Single().AssistantEntryId);
        Equal(3,history.BranchHistory.Length);return Task.CompletedTask;
    }
    private static Task Legacy()
    {
        var legacy=Assistant("legacy",null,"MalformedStream");var unknown=Entry("custom","unknown","legacy",new{customType="opaque.extension",data=new{errorMessage="opaque imported bytes",schemaVersion=99}});
        var before=new[]{legacy.WireBody.ToString(),unknown.WireBody.ToString()};var context=new SessionContextProjector().Project([legacy,unknown],"unknown");
        var row=NativeSessionDiagnosticProjector.Project(context).Entries.Single();Equal(NativeSessionDiagnosticProvenance.UnverifiedLegacyObservation,row.Provenance);
        Check(row.RecordEntryId is null&&row.OperationGeneration is null,"Legacy field acquired internally originated authority.");Equal(NativeChatFailureCode.MalformedStream,row.Diagnostic!.Code);
        Check(before.SequenceEqual(new[]{legacy.WireBody.ToString(),unknown.WireBody.ToString()}),"Diagnostic reading rewrote imported source fields.");
        Check(context.LlmMessages.Single().WireBody.Value.GetProperty("opaque").GetProperty("nullValue").ValueKind==JsonValueKind.Null,"Opaque legacy fields were stripped.");return Task.CompletedTask;
    }
    private static Task Malformed()
    {
        var assistant=Assistant("a",null);var rows=ImmutableArray.CreateBuilder<SessionEntry>();rows.Add(assistant);var parent="a";
        foreach(var data in new object[]{
            new{schemaVersion="1",assistantEntryId="a",operationGeneration=1,diagnostic=new{adapter="openai-completions",code="SourceFailed"}},
            new{schemaVersion=1,assistantEntryId="a",operationGeneration="1",diagnostic=new{adapter="openai-completions",code="SourceFailed"}},
            new{schemaVersion=1,assistantEntryId="a",operationGeneration=1,diagnostic=new{adapter="openai-completions",code="1"}},
            new{schemaVersion=1,assistantEntryId="a",operationGeneration=1,diagnostic=new{adapter="private-exception-text",code="SourceFailed"}},
            new{schemaVersion=1,assistantEntryId="a",operationGeneration=1,diagnostic=new{adapter="openai-completions",code="private exception text"}},
            new{schemaVersion=1,assistantEntryId="missing",operationGeneration=1,diagnostic=new{adapter="openai-completions",code="SourceFailed"}}})
        {var id="bad-"+rows.Count;rows.Add(Entry("custom",id,parent,new{customType=NativeSessionDiagnosticProjector.CustomType,data}));parent=id;}
        var raw=rows.Select(row=>row.WireBody.ToString()).ToArray();var view=NativeSessionDiagnosticProjector.Project(new SessionContextProjector().Project(rows.ToImmutable(),parent));
        Equal(0,view.Entries.Length);Equal(5,view.Issues.Count(row=>row.Failure==NativeSessionDiagnosticReadFailure.InvalidRecord));Equal(1,view.Issues.Count(row=>row.Failure==NativeSessionDiagnosticReadFailure.UnresolvedAssistant));
        Check(raw.SequenceEqual(rows.Select(row=>row.WireBody.ToString())),"Malformed observation reading rewrote data.");return Task.CompletedTask;
    }
    private static Task Bounds()
    {
        Throws<ArgumentException>(()=>NativeSessionDiagnosticProjector.RecordData("a",1,new((NativeChatAdapter)999,NativeChatFailureCode.SourceFailed),null));
        Throws<ArgumentException>(()=>NativeSessionDiagnosticProjector.RecordData("a",1,new(NativeChatAdapter.OpenAICompletions,(NativeChatFailureCode)999),null));
        Throws<ArgumentException>(()=>NativeSessionDiagnosticProjector.RecordData("a",0,new(NativeChatAdapter.OpenAICompletions,NativeChatFailureCode.SourceFailed),null));
        var entries=ImmutableArray.CreateBuilder<SessionEntry>();entries.Add(Assistant("a",null));var parent="a";
        for(var i=0;i<=NativeSessionDiagnosticProjector.MaximumObservations;i++){var id="diagnostic-"+i;entries.Add(Diagnostic(id,parent,"a",NativeChatFailureCode.SourceFailed));parent=id;}
        var context=new SessionContextProjector().Project(entries.ToImmutable(),parent);
        var bounded=NativeSessionDiagnosticProjector.Project(context);Equal(NativeSessionDiagnosticProjector.MaximumObservations-1,bounded.Entries.Length);
        Equal(1,bounded.Issues.Length);Equal(NativeSessionDiagnosticReadFailure.ResourceLimit,bounded.Issues.Single().Failure);
        Equal("diagnostic-"+NativeSessionDiagnosticProjector.MaximumObservations,bounded.Entries[^1].RecordEntryId);
        Equal(1,context.LlmMessages.Length);Check(System.Text.Encoding.UTF8.GetByteCount(NativeSessionDiagnosticProjector.ToWire(bounded).ToString())<=NativeSessionDiagnosticProjector.MaximumViewBytes,"Bounded view exceeded its byte limit.");
        var valid=bounded.Entries[^1];
        foreach(var invalid in new[]{valid with{AssistantEntryId=new string('x',257)},valid with{RecordEntryId=null},valid with{OperationGeneration=0},
            valid with{Diagnostic=null,CleanupDiagnostic=null},valid with{Provenance=NativeSessionDiagnosticProvenance.UnverifiedLegacyObservation}})
            Throws<ArgumentException>(()=>NativeSessionDiagnosticProjector.ToWire(new([invalid],[])));
        Throws<ArgumentException>(()=>NativeSessionDiagnosticProjector.ToWire(new([],[new("x",(NativeSessionDiagnosticReadFailure)999)])));
        var oversized=Entry("custom",new string('z',257),"a",new{customType=NativeSessionDiagnosticProjector.CustomType,data=NativeSessionDiagnosticProjector.RecordData("a",1,new(NativeChatAdapter.OpenAICompletions,NativeChatFailureCode.SourceFailed),null).Value});
        var oversizedView=NativeSessionDiagnosticProjector.Project(new SessionContextProjector().Project([Assistant("a",null),oversized],oversized.Id));
        Equal("",oversizedView.Issues.Single().EntryId);Equal(NativeSessionDiagnosticReadFailure.InvalidRecord,oversizedView.Issues.Single().Failure);return Task.CompletedTask;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Equal<T>(T expected,T actual)=>Check(EqualityComparer<T>.Default.Equals(expected,actual),$"Expected {expected}; actual {actual}.");
    private static void Throws<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
}
