---
title: Telemetry
description: Trace model requests and durable runs with PiSharp.Telemetry, the experimental port of Pi's pi-telemetry. New in PiSharp 1.5.0.
sidebar:
  badge:
    text: Experimental
    variant: caution
---

:::caution[Experimental]
`PiSharp.Telemetry` is new in PiSharp 1.5.0. Every public type in it, and the telemetry members of `PiSharp.AI`, are marked `[Experimental("PISHARP_TELEMETRY001")]`, so suppress `PISHARP_TELEMETRY001` in your project to compile against them. The API may change or be removed in any release, without notice or a deprecation period.
:::

`PiSharp.Telemetry` ports Pi's `pi-telemetry` v1.5.0 package: the contract for spans, span links, metrics and trace propagation. Like upstream it has no package dependencies and ships only two adapters, a no-op one and an in-memory one. It has no exporter, so nothing is sent anywhere. Telemetry is recorded only when your host passes in a context or a span. The CLI and the coding agent pass none, so they record nothing.

To send spans to a tracing backend, implement the contract yourself and check your adapter with the conformance kit described below.

## Install

`PiSharp.AI` references `PiSharp.Telemetry`, so any project that uses the chat transports already has it. To use it on its own:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <NoWarn>$(NoWarn);PISHARP_TELEMETRY001</NoWarn>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="PiSharp.Telemetry" Version="1.5.0" />
</ItemGroup>
```

Everything is in the `PiSharp.Telemetry` namespace. The conformance kit is in `PiSharp.Telemetry.Testing`.

## The contract

| Type | Upstream | What it does |
| --- | --- | --- |
| `ITelemetryContext` | `TelemetryContext` | `StartSpanAsync(options, callback)` starts a span, runs the callback once, synchronously, and settles the span when the callback's task does. The returned task has the callback's result, or rethrows the same exception instance. `Metrics` and `Propagation` are the adapter's optional capabilities. |
| `ITelemetrySpan` | `TelemetrySpan` | A context with `AddLink`, `AddEvent`, `SetAttributes` and `SetStatus`. Once the span has settled, these calls have no effect. |
| `SpanOptions` | `SpanOptions` | A name, with optional attributes and links. A plain string converts to options. |
| `SpanStatus` | `SpanStatus` | `SpanStatus.Ok`, or `SpanStatus.Failed(name, message)`. |
| `ITelemetryPropagation` | `TelemetryPropagation` | `Inject(context)` returns a carrier, an opaque string map, and `Extract(carrier)` continues a parent from one. An absent or unknown carrier gives the root. |
| `ITelemetryMetrics` | `TelemetryMetrics` | `Counter`, `UpDownCounter` and `Histogram` instruments. |
| `ITelemetryStore` | `TelemetryStore` | Reads settled traces and metrics: `FindTracesAsync`, `GetTraceAsync`, `FindMetricsAsync`. |
| `TraceRenderer.RenderTrace` | `renderTrace` | Prints stored spans as a chronological forest of text lines, with their links across traces. |

`NoopTelemetry.Context`, `NoopTelemetry.Metrics` and `NoopTelemetry.Propagation` are the no-op adapter. `InMemoryTelemetryContext` is the in-memory adapter. It is a context and also implements propagation, metrics and the store. Its carriers hold only its own `pi-memory-scope`, `pi-memory-span` and `pi-memory-trace` identities, and nothing leaves the process. It is thread-safe, and a `TimeProvider` can drive its clocks. Use a fresh instance for each test or recording scope; `GetSpans()` and `GetMetrics()` return what it recorded.

### Typed schemas

Upstream checks span names and attributes with TypeScript types. C# has no literal or mapped types, so in PiSharp a schema is a serializable record, `TelemetrySchemaDefinition`, that serializes to upstream's JSON shape. Three methods enforce it at run time:

- `TelemetrySchema.Define` checks the definition itself.
- `TelemetrySchema.CreateTypedSpanStarter` starts only the spans the schemas declare.
- `TelemetrySchema.CreateTypedMetrics` creates only the declared instruments, of the declared kind.

Where TypeScript would fail to compile, PiSharp throws `ArgumentException` before anything is recorded:

- an undeclared span or instrument name, or the wrong instrument kind;
- a missing required attribute, or an undeclared one;
- a value of the wrong type, or outside a closed set;
- a high-cardinality metric attribute.

The `pi.ai.*` schema is `AiTelemetry.Schema`, and the durable one is `DurableTelemetry.Schema`.

## Tracing model requests

Set `ChatRequest.Telemetry` to the parent span. The `ChatRun` that `ChatClient` starts then records one `pi.ai.request` span under it:

```csharp
using PiSharp.AI;
using PiSharp.Telemetry;

var telemetry = new InMemoryTelemetryContext();
var client = new ChatClient(provider);

await telemetry.StartSpanAsync("my-request", async parent =>
{
    var result = await client.CompleteAsync(new ChatRequest(model, messages) { Telemetry = parent });
});

foreach (var span in telemetry.GetSpans())
    Console.WriteLine($"{span.Name} {span.Status.Status}");
```

The span starts with the logical request, so it also covers authentication and provider setup.

- **Attributes.** It starts with `provider`, `api`, `model` and, when one is requested, `reasoning`. It ends with `stop` (the normalized stop reason), `usage.input`, `usage.output`, `usage.cache_read`, `usage.cache_write`, `cost.total` and `response.id`.
- **Events.** `pi.ai.first_chunk` marks the first stream event. A `pi.ai.block` event marks each completed content block, with its type, index and character count, plus the tool name for a tool call.
- **Status.** An error stop sets the error status `provider_error`. An aborted request keeps status ok.
- **Metrics.** If the span's context has a `Metrics` capability, the request also records `pi.ai.request.duration` and `pi.ai.first_chunk.duration` in milliseconds, `pi.ai.tokens` by kind, and `pi.ai.cost` in USD. Their attributes are the provider and the model, plus the stop reason on the duration and the token kind on the tokens.

Requests that a host sends straight to a transport, without a `ChatRun`, are not traced. To trace one, call `AiTelemetry.ObserveModelRequest(model, span, reasoning)` and pass the returned observer each event and then the final message. For a deferred-response poll, pass `poll: true` to record a `pi.ai.poll` span instead.

`ModelRequestOptions.Telemetry`, inherited by the classifier and image options, carries a span with the request. As upstream, classifier and image requests record no spans of their own.

A telemetry adapter that throws never changes the chat. The observation stops and the request carries on.

## Durable runs

`HarnessOptions.Telemetry` takes a host-owned `ITelemetryContext`. The harness then records `pi.durable.submit` and `pi.durable.phase` spans, report and entry events, and phase and active-task metrics, and it stores trace carriers on its records, so a phase that runs after a restart joins its original trace. See [Telemetry on the Durable library page](/docs/embed/durable/#telemetry).

Model requests made through `NativeDurableModels` with telemetry record one `pi.ai.request` span around the whole stream. A deferred poll (`StreamDeferred`, `FetchDeferredAsync`) records one `pi.ai.poll` span.

## What is recorded, and redaction

Spans and metrics never carry prompt or response content, credentials, headers or request and response bodies. A request span holds only three pieces of provider text:

- the error message of an error stop;
- the response ID;
- the tool name of a tool-call block.

PiSharp redacts more than upstream does, under its rule that secrets never reach logs:

- **The error message.** Credential-shaped text is replaced with `[redacted]` before the message is cut to 500 characters. That covers URL user information, sensitive query parameters and name/value pairs, `Bearer` and `Basic` credentials, and common key shapes. If the redaction pattern times out, the message is recorded as empty.
- **The response ID.** Upstream records it raw. PiSharp redacts and caps it the same way.
- **Setup failures.** A failure before the stream starts records `ChatRun`'s sanitized message, never the transport's exception text.

## Testing an adapter

`TelemetryAdapterConformance.Create(factory)` returns upstream's adapter conformance cases. Each is a `TelemetryAdapterConformanceCase` with a `Group`, a `Name` and `RunAsync()`, so any test runner can register them.

The factory creates a fresh `TelemetryAdapterFixture` for each case. The fixture holds your `Context`, the optional `Propagation` and `Metrics` capabilities, and readers that return the recorded spans and measurements. A failed expectation throws `TelemetryConformanceException`. A case that needs a capability the fixture lacks returns without checking anything. PiSharp's own suite runs all 14 cases against `InMemoryTelemetryContext`, and the capability cases against the no-op adapter.

## Upgrading from 1.2.0.x

`CompletionsSimpleOptions.TelemetryContext`, which held opaque JSON, is now the typed span `CompletionsSimpleOptions.Telemetry` (`ITelemetrySpan?`). Upstream renamed `telemetryContext` to `telemetry` the same way, and there is no alias, so code that set the old property must change. The option is carried with the request and never serialized. As with other requests, tracing comes from the `ChatRun`, through `ChatRequest.Telemetry`.
