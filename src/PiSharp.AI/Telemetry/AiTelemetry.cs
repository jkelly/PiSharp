// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/telemetry.ts.
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Telemetry;

namespace PiSharp.AI;

/// <summary>telemetry.ts: Pi-owned request metadata, bounded error status, events and low-cardinality metrics. Nothing is recorded
/// unless a host passes a span: <see cref="ChatRequest.Telemetry"/> for a <see cref="ChatRun"/>, or <see cref="ObserveModelRequest"/>
/// directly. Spans and metrics never carry prompt or response content, credentials, headers or bodies: the only provider text recorded
/// is the error message of an error stop and the response ID, each with credential-shaped text redacted and capped at 500 UTF-16
/// characters, and the tool name of a tool-call block.</summary>
[Experimental("PISHARP_TELEMETRY001")]
public static partial class AiTelemetry
{
    public const string RequestSpanName = "pi.ai.request";
    public const string PollSpanName = "pi.ai.poll";
    private const int MaximumErrorCharacters = 500;
    private const string Redacted = "[redacted]";

    /// <summary><c>AI_TELEMETRY_SCHEMA</c>.</summary>
    public static TelemetrySchemaDefinition Schema { get; } = TelemetrySchema.Define(BuildSchema());

    private static readonly ConditionalWeakTable<ITelemetryMetrics, Instruments> InstrumentCache = [];

    /// <summary>telemetry.ts <c>observeModelRequest</c>: starts a <c>pi.ai.request</c> span (<c>pi.ai.poll</c> for a deferred-response
    /// poll) under <paramref name="telemetry"/>, or under the no-op context when it is null. Feed the observer each delivered
    /// normalized event, the terminal event included, then the final message (or null) once. <paramref name="reasoning"/> is the
    /// requested scalar reasoning level. Instruments are created once per metrics object.</summary>
    public static AiRequestObserver ObserveModelRequest(ModelDescriptor model, ITelemetrySpan? telemetry, string? reasoning = null,
        bool poll = false, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var time = timeProvider ?? TimeProvider.System;
        var started = time.GetTimestamp();
        var adapter = telemetry?.Metrics ?? NoopTelemetry.Metrics;
        var instruments = InstrumentCache.GetValue(adapter, CreateInstruments);
        var observer = new AiRequestObserver(model, instruments, poll, time, started);
        var attributes = new SpanAttributes { ["provider"] = model.Provider, ["api"] = model.Api, ["model"] = model.Id };
        if (reasoning is not null) attributes["reasoning"] = reasoning;
        var start = TelemetrySchema.CreateTypedSpanStarter(telemetry ?? NoopTelemetry.Context, Schema);
        var settled = start.StartSpanAsync(poll ? PollSpanName : RequestSpanName, attributes, observer.Run);
        // The span settles when the result is observed; an adapter's own failure never surfaces as an unobserved task.
        if (!settled.IsCompleted) _ = settled.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else _ = settled.Exception;
        return observer;
    }

    internal sealed record Instruments(SchemaTelemetryHistogram RequestDuration, SchemaTelemetryHistogram FirstChunkDuration,
        SchemaTelemetryCounter Tokens, SchemaTelemetryCounter Cost);

    private static Instruments CreateInstruments(ITelemetryMetrics adapter)
    {
        var metrics = TelemetrySchema.CreateTypedMetrics(adapter, Schema);
        return new(metrics.Histogram("pi.ai.request.duration"), metrics.Histogram("pi.ai.first_chunk.duration"),
            metrics.Counter("pi.ai.tokens"), metrics.Counter("pi.ai.cost"));
    }

    internal static string StopName(StopReason reason) => reason switch
    {
        StopReason.Pending => "pending",
        StopReason.Stop => "stop",
        StopReason.Length => "length",
        StopReason.ToolUse => "toolUse",
        StopReason.Error => "error",
        StopReason.Aborted => "aborted",
        StopReason.Deferred => "deferred",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    /// <summary><c>ownedSlice(text, 0, 500)</c>, after redacting credential-shaped text: URL user information, sensitive query
    /// parameters and name/value pairs (keys, tokens, secrets, passwords, signatures, authorization, cookies), Bearer and Basic
    /// credentials, and well-known API key shapes. Only a bounded prefix is examined, so a large message is never retained or scanned
    /// whole. A redaction that times out records nothing.</summary>
    internal static string Bound(string text)
    {
        var window = text.Length > MaximumErrorCharacters + 1024 ? text[..(MaximumErrorCharacters + 1024)] : text;
        try
        {
            window = UrlUserInfo().Replace(window, "${scheme}" + Redacted + "@");
            window = QueryParameter().Replace(window, match => IsSensitiveName(match.Groups["name"].Value)
                ? match.Groups["lead"].Value + match.Groups["name"].Value + "=" + Redacted : match.Value);
            window = AuthorizationScheme().Replace(window, "${scheme} " + Redacted);
            window = NamedValue().Replace(window, match => IsSensitiveName(match.Groups["name"].Value)
                ? match.Groups["name"].Value + match.Groups["separator"].Value + Redacted : match.Value);
            window = KeyShape().Replace(window, Redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            return "";
        }
        // A copy: the span never keeps a large provider message alive.
        return window.Length > MaximumErrorCharacters ? new string(window.AsSpan(0, MaximumErrorCharacters)) : new string(window.AsSpan());
    }

    /// <summary>A name is sensitive when, after splitting camelCase with <c>_</c>, a delimited word names a credential
    /// (<c>key</c>, <c>api_key</c>, <c>x-goog-api-key</c>, <c>access_token</c>, <c>X-Amz-Signature</c>; not <c>max_tokens</c>).</summary>
    private static bool IsSensitiveName(string name) => SensitiveWord().IsMatch(CamelBoundary().Replace(name, "$1_$2"));

    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.\-]*://)[^\s/?#@""'<>]+@", RegexOptions.CultureInvariant, 250)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"(?<lead>[?&;])(?<name>[^=&#;\s""'<>]+)=(?<value>[^&#;\s""'<>]*)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex QueryParameter();

    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9._~+/\-]+=*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex AuthorizationScheme();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.\-])(?<name>[A-Za-z][A-Za-z0-9_.\-]*)(?<separator>[""']?\s*[:=]\s*[""']?)(?<value>[^\s""',;&<>]+)",
        RegexOptions.CultureInvariant, 250)]
    private static partial Regex NamedValue();

    [GeneratedRegex(@"(?:^|[-_.\s])(?:api[-_]?key|apikey|key|secret|token|password|passwd|pwd|credential|credentials|authorization|auth|cookie|sig|signature)(?:$|[-_.\s])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex SensitiveWord();

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant, 250)]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_\-]{16,}|AIza[0-9A-Za-z_\-]{30,}|(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|(?:AKIA|ASIA)[0-9A-Z]{16}|ya29\.[0-9A-Za-z_\-]{10,}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}|(?:hf|gsk)_[A-Za-z0-9]{20,}|(?:xai|pplx)-[A-Za-z0-9]{20,})",
        RegexOptions.CultureInvariant, 250)]
    private static partial Regex KeyShape();

    private static TelemetrySchemaDefinition BuildSchema()
    {
        var provider = new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Provider identifier.") { Required = true };
        var model = new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Requested model identifier.") { Required = true };
        var stop = new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Normalized stop reason.")
        { Values = ["pending", "stop", "length", "toolUse", "error", "aborted", "deferred"] };
        var request = new TelemetrySpanDefinition(
            "One logical model request, including authentication and provider setup.",
            TelemetryParentDefinition.Any,
            Map(("provider", provider),
                ("api", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "API identifier.") { Required = true }),
                ("model", model),
                ("reasoning", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Scalar requested reasoning level.") { Required = false })),
            Map(("stop", stop),
                ("usage.input", Number("Uncached input tokens.")),
                ("usage.output", Number("Output tokens.")),
                ("usage.cache_read", Number("Cache-read tokens.")),
                ("usage.cache_write", Number("Cache-write tokens.")),
                ("cost.total", Number("Total request cost in USD.")),
                ("response.id", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Provider response identifier.")
                { Cardinality = TelemetryCardinality.High })),
            new("Stop reason is error: provider_error with at most 500 error-message characters. Aborted stays ok."))
        {
            Events = Map(
                ("pi.ai.first_chunk", new TelemetryEventDefinition("First normalized stream event delivered.", Map<TelemetryAttributeDefinition>())),
                ("pi.ai.block", new TelemetryEventDefinition("A completed content block; content is never recorded.", Map(
                    ("type", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Block kind.")
                    { Values = ["text", "thinking", "tool_call"], Required = true }),
                    ("index", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "Content block index.") { Required = true }),
                    ("chars", new TelemetryAttributeDefinition(TelemetryAttributeType.Number, "UTF-16 character count.") { Required = true }),
                    ("tool", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Tool name, for tool-call blocks only.") { Required = false })))))
        };
        return new(1, Map(
            (RequestSpanName, request),
            (PollSpanName, request with { Description = "One deferred-response poll, not the deferred request's lifetime." })))
        {
            Metrics = Map(
                ("pi.ai.request.duration", new TelemetryMetricDefinition(TelemetryMetricKind.Histogram, "Logical request or poll duration.",
                    Map(("provider", provider), ("model", model), ("stop", stop with { Required = true }))) { Unit = "ms" }),
                ("pi.ai.first_chunk.duration", new TelemetryMetricDefinition(TelemetryMetricKind.Histogram,
                    "Time to the first normalized stream event.", Map(("provider", provider), ("model", model))) { Unit = "ms" }),
                ("pi.ai.tokens", new TelemetryMetricDefinition(TelemetryMetricKind.Counter,
                    "Reported model token usage; pending and deferred polls are excluded.", Map(("provider", provider), ("model", model),
                        ("kind", new TelemetryAttributeDefinition(TelemetryAttributeType.String, "Token usage category.")
                        { Values = ["input", "output", "cache_read", "cache_write"], Required = true }))) { Unit = "token" }),
                ("pi.ai.cost", new TelemetryMetricDefinition(TelemetryMetricKind.Counter,
                    "Reported model cost; pending and deferred polls are excluded.", Map(("provider", provider), ("model", model))) { Unit = "USD" }))
        };

        static TelemetryAttributeDefinition Number(string description) => new(TelemetryAttributeType.Number, description);
    }

    private static OrderedDictionary<string, T> Map<T>(params (string Name, T Value)[] items)
    {
        var map = new OrderedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var (name, value) in items) map.Add(name, value);
        return map;
    }

    /// <summary>Observation that must never change a chat: a failure disables it and settles its span.</summary>
    internal static AiRequestObserver? TryObserve(ModelDescriptor model, ITelemetrySpan telemetry, string? reasoning, TimeProvider? time)
    {
        try
        {
            return ObserveModelRequest(model, telemetry, reasoning, poll: false, time);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static double CostTotal(UsageCost cost)
    {
        if (cost.SourceBinary64Cost is { } source && source.Value.ValueKind == JsonValueKind.Object &&
            source.Value.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetDouble(out var exact))
            return exact;
        // The typed decimal is the binary64 value's shortest decimal text.
        return double.Parse(cost.Total.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    internal static string? Text(AssistantMessage message, string name) =>
        message.ExtraProperties is { } extra && extra.TryGet(name, out var value) && value is not null &&
        value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
}

/// <summary>The observer of one <c>pi.ai.request</c> or <c>pi.ai.poll</c> span (lazy.ts <c>LazyStreamObserver</c>): synchronous and
/// read-only. Events after the result are ignored.</summary>
[Experimental("PISHARP_TELEMETRY001")]
public sealed class AiRequestObserver
{
    private readonly SpanAttributes _attributes;
    private readonly AiTelemetry.Instruments _instruments;
    private readonly bool _poll;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly TaskCompletionSource _finished = new();
    private readonly Dictionary<int, int> _toolChars = [];
    private SchemaTelemetrySpan? _span;
    private bool _first = true;
    private bool _done;

    internal AiRequestObserver(ModelDescriptor model, AiTelemetry.Instruments instruments, bool poll, TimeProvider time, long started)
    {
        _attributes = new SpanAttributes { ["provider"] = model.Provider, ["model"] = model.Id };
        _instruments = instruments;
        _poll = poll;
        _time = time;
        _started = started;
    }

    internal Task Run(SchemaTelemetrySpan span, TypedSpanStarter _)
    {
        _span = span;
        return _finished.Task;
    }

    public void OnEvent(StreamEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_done || _span is not { } span) return;
        if (_first)
        {
            _first = false;
            span.AddEvent("pi.ai.first_chunk");
            _instruments.FirstChunkDuration.Record(Elapsed(), With(), span.Span);
        }
        switch (value)
        {
            case ToolCallDelta delta:
                _toolChars[delta.ContentIndex] = _toolChars.GetValueOrDefault(delta.ContentIndex) + delta.Delta.Length;
                break;
            case TextEnded text:
                span.AddEvent("pi.ai.block", new SpanAttributes { ["type"] = "text", ["index"] = text.ContentIndex, ["chars"] = text.Content.Length });
                break;
            case ThinkingEnded thinking:
                span.AddEvent("pi.ai.block", new SpanAttributes { ["type"] = "thinking", ["index"] = thinking.ContentIndex, ["chars"] = thinking.Content.Length });
                break;
            case ToolCallEnded tool:
                _toolChars.Remove(tool.ContentIndex, out var chars);
                span.AddEvent("pi.ai.block", new SpanAttributes
                { ["type"] = "tool_call", ["index"] = tool.ContentIndex, ["chars"] = chars, ["tool"] = tool.ToolCall.Name });
                break;
        }
    }

    public void OnResult(AssistantMessage? message)
    {
        if (_done) return;
        _done = true;
        try
        {
            if (message is not null && _span is { } span)
            {
                var stop = AiTelemetry.StopName(message.StopReason);
                var usage = message.Usage;
                var cost = AiTelemetry.CostTotal(usage.Cost);
                span.SetAttributes(new SpanAttributes
                {
                    ["stop"] = stop,
                    ["usage.input"] = usage.Input,
                    ["usage.output"] = usage.Output,
                    ["usage.cache_read"] = usage.CacheRead,
                    ["usage.cache_write"] = usage.CacheWrite,
                    ["cost.total"] = cost,
                    ["response.id"] = AiTelemetry.Text(message, "responseId") is { } id ? AiTelemetry.Bound(id) : null
                });
                if (message.StopReason == StopReason.Error)
                    span.SetStatus(SpanStatus.Failed("provider_error", AiTelemetry.Bound(AiTelemetry.Text(message, "errorMessage") ?? "")));
                _instruments.RequestDuration.Record(Elapsed(), With(("stop", stop)), span.Span);
                if (!_poll || message.StopReason is not (StopReason.Pending or StopReason.Deferred))
                {
                    _instruments.Tokens.Add(usage.Input, With(("kind", "input")));
                    _instruments.Tokens.Add(usage.Output, With(("kind", "output")));
                    _instruments.Tokens.Add(usage.CacheRead, With(("kind", "cache_read")));
                    _instruments.Tokens.Add(usage.CacheWrite, With(("kind", "cache_write")));
                    _instruments.Cost.Add(cost, With());
                }
            }
        }
        finally
        {
            _finished.TrySetResult();
        }
    }

    /// <summary>Settles the span without a result, after a failure of the observation itself.</summary>
    internal void Abandon()
    {
        _done = true;
        _finished.TrySetResult();
    }

    private double Elapsed() => _time.GetElapsedTime(_started).TotalMilliseconds;

    private SpanAttributes With(params (string Name, string Value)[] extra)
    {
        var attributes = new SpanAttributes(_attributes);
        foreach (var (name, value) in extra) attributes[name] = value;
        return attributes;
    }
}
