using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

if (args.Length != 1) throw new ArgumentException("Supply exact admitted eight-case input path.");
using var input = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = input.RootElement;
var model = new ModelDescriptor(root.GetProperty("model").GetProperty("id").GetString()!, "anthropic-messages", "anthropic");
var groups = new List<OriginalTaskRecord>();
var held = new List<OriginalTaskRecord>();
var results = new List<object>();
foreach (var item in root.GetProperty("cases").EnumerateArray())
{
    var record = new OriginalTaskRecord(item.GetProperty("caseId").GetString()!); groups.Add(record);
    try { var original = Run(item); record.Original = original; results.Add(await original); }
    catch (Exception error) { record.Direct = error; }
    finally { record.Capture(); }
}
try { Console.WriteLine(JsonSerializer.Serialize(new { sourceDerivedExpected=false, originalExecuted=false,
    nativeCases=results, groups=groups.Select(QualificationReporter.Project), ownedOriginals=held.Select(QualificationReporter.Project) },
    new JsonSerializerOptions { WriteIndented=true })); }
catch (Exception error) { throw new QualificationReportingFailure(error, groups.ToArray(), held.ToArray()); }
return groups.Count == 8 && results.Count == 8 && groups.All(record => record.Direct is null) ? 0 : 1;

async Task<object> Run(JsonElement item)
{
    var response = item.TryGetProperty("response", out var specific) ? specific : root.GetProperty("response");
    var options = item.GetProperty("options"); var metadata = item.GetProperty("modelOverrides");
    var factory = new AnthropicMessagesKeyAuthRequestFactory(new("https://pisharp-anthropic-oracle.invalid"), model,
        new(64, BetaFeatures: ImmutableArray<string>.Empty), new(
            ModelHeaders: metadata.TryGetProperty("headers", out var headers) ? JsonData.FromElement(headers) : null,
            Headers: options.TryGetProperty("headers", out var requestHeaders) ? JsonData.FromElement(requestHeaders) : null));
    var observation = new Observation();
    using var handler = new Handler(observation, response.GetProperty("sseText").GetString()!, held);
    using var client = new HttpClient(handler);
    var transport = new AnthropicMessagesHttpSseTransport(client, (request, token) => factory.Create(request,
        "pisharp-authored-inert-key-noncredential", token));
    var request = new ChatRequest(model, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"hello\",\"timestamp\":1700000000000}"))]);
    var enumerator = transport.StreamAsync(request).GetAsyncEnumerator();
    try
    {
        while (true)
        {
            var pull = new OriginalTaskRecord("native-pull-original"); held.Add(pull);
            bool more;
            try { var original = enumerator.MoveNextAsync().AsTask(); pull.Original=original; more=await original; }
            catch (Exception error) { pull.Direct=error; throw; }
            finally { pull.Capture(); }
            if (!more) break;
            observation.FrameKinds.Add(enumerator.Current.GetType().Name);
            if (enumerator.Current is StreamTerminalEvent terminal) observation.Terminal=terminal;
        }
    }
    finally
    {
        var cleanupFailures = new List<Exception>();
        var dispose = new OriginalTaskRecord("native-enumerator-dispose-original"); held.Add(dispose);
        try { var original=enumerator.DisposeAsync().AsTask(); dispose.Original=original; await original; }
        catch (Exception error) { dispose.Direct=error; cleanupFailures.Add(error); }
        finally { dispose.Capture(); }
        foreach (var send in handler.Sends)
        {
            try { await send.Original!; } catch (Exception error) { send.Direct=error; cleanupFailures.Add(error); }
            finally { send.Capture(); }
        }
        if (cleanupFailures.Count != 0) throw new AggregateException("Owned native cleanup originals failed after all joins.", cleanupFailures);
    }
    return new { caseId=item.GetProperty("caseId").GetString(), observation.Uri, observation.Body,
        observation.Headers, observation.FrameKinds, stop=observation.Terminal?.Reason.ToString(),
        text=observation.Terminal is null ? "" : string.Concat(observation.Terminal.Message.Content.OfType<TextContent>().Select(part => part.Text)),
        error=observation.Terminal?.Message.ExtraProperties is { } extra && extra.TryGet("errorMessage", out var terminalError) ? terminalError?.Value.GetString() : null };
}

sealed class Observation
{
    internal string? Uri, Body;
    internal readonly Dictionary<string,string> Headers = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> FrameKinds=[];
    internal StreamTerminalEvent? Terminal;
}
sealed class Handler(Observation observed, string sse, List<OriginalTaskRecord> held) : HttpMessageHandler
{
    internal readonly List<OriginalTaskRecord> Sends=[];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var record=new OriginalTaskRecord("native-send-original"); Sends.Add(record); held.Add(record);
        var original=Read(request, token); record.Original=original; return original;
    }
    private async Task<HttpResponseMessage> Read(HttpRequestMessage request, CancellationToken token)
    {
        observed.Uri=request.RequestUri!.AbsoluteUri;
        observed.Body=await request.Content!.ReadAsStringAsync(token);
        foreach(var name in new[]{"anthropic-beta","x-probe","content-type"})
        {
            if(request.Headers.TryGetValues(name,out var values)) observed.Headers[name]=string.Join(", ",values);
            else if(request.Content.Headers.TryGetValues(name,out values)) observed.Headers[name]=string.Join(", ",values);
        }
        return new(System.Net.HttpStatusCode.OK){Content=new StringContent(sse,Encoding.UTF8,"text/event-stream")};
    }
}
