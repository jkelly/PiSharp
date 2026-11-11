// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry.ts.
// Pi v1.5.2 (MIT): packages/chord/src/telemetry.ts.
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Telemetry;

namespace PiSharp.Chord;

/// <summary>Chord's schema, bounded span metadata, propagation and cached instruments.</summary>
public static class ChordTelemetry
{
    public const int MaxMessage = 500;
    public static TelemetrySchemaDefinition Schema { get; } = TelemetrySchema.Define(
        JsonSerializer.Deserialize(SchemaJson, TelemetryJsonContext.Default.TelemetrySchemaDefinition)!);
    private static readonly ConditionalWeakTable<ITelemetryMetrics, ChordMetrics> Instruments = new();
    private static readonly ChordMetrics NoopMetrics = new(NoopTelemetry.Metrics);

    public static bool IsUntraced(ITelemetryContext parent) => ReferenceEquals(parent, NoopTelemetry.Context) || parent is CountingSpan;
    public static ITelemetrySpan CountingOnly(ITelemetryMetrics metrics) => new CountingSpan(metrics);
    public static TypedSpanStarter Spans(ITelemetryContext parent) => TelemetrySchema.CreateTypedSpanStarter(parent, Schema);
    public static ChordMetrics Metrics(ITelemetryMetrics? metrics) => metrics is null || ReferenceEquals(metrics, NoopTelemetry.Metrics)
        ? NoopMetrics : Instruments.GetValue(metrics, value => new ChordMetrics(value));
    public static string Capped(string text) => text.Length > 256 ? text[..256] : text;
    public static string HostAttribute(Address? address) => Capped(address?.Key ?? "client");
    public static void SpanFailed(ITelemetrySpan span, string category, Exception error) =>
        span.SetStatus(SpanStatus.Failed(category, error.Message.Length > MaxMessage ? error.Message[..MaxMessage] : error.Message));
    public static string Ended(ITelemetrySpan span, string category, Exception error, CancellationToken? signal = null)
    {
        if (signal?.IsCancellationRequested == true || error is ChordError { Code: "aborted" })
        {
            span.SetStatus(SpanStatus.Ok);
            return "aborted";
        }
        SpanFailed(span, category, error);
        return "error";
    }
    public static IReadOnlyDictionary<string, string>? CarrierOf(ITelemetryContext span) =>
        IsUntraced(span) ? null : span.Propagation?.Inject(span);
    public static ITelemetryContext ParentOf(IReadOnlyDictionary<string, string>? carrier, ITelemetryContext adapter)
    {
        if (IsUntraced(adapter)) return adapter;
        try { return adapter.Propagation?.Extract(carrier) ?? adapter; }
        catch { return adapter; }
    }

    private sealed class CountingSpan(ITelemetryMetrics metrics) : ITelemetrySpan
    {
        public ITelemetryMetrics Metrics => metrics;
        public ITelemetryPropagation? Propagation => NoopTelemetry.Propagation;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
            NoopTelemetry.Context.StartSpanAsync(options, callback);
        public void AddLink(SpanLink link) { }
        public void AddEvent(string name, SpanAttributes? attributes = null) { }
        public void SetAttributes(SpanAttributes attributes) { }
        public void SetStatus(SpanStatus status) { }
    }

    private const string SchemaJson = """
{
  "version": 1,
  "spans": {
    "chord.call": {
      "description": "A consumer's call of a remote object's method, from the call until its reply.",
      "parents": {
        "kind": "any"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "type": {
          "type": "string",
          "required": true,
          "description": "The object type's name."
        },
        "member": {
          "type": "string",
          "required": true,
          "description": "The method's name."
        },
        "object": {
          "type": "string",
          "required": true,
          "description": "The object: its type, and its params key when it has params.",
          "cardinality": "high"
        },
        "address.kind": {
          "type": "string",
          "required": true,
          "description": "The kind of the provider's host."
        },
        "chord.principal": {
          "type": "string",
          "required": false,
          "description": "Whom the call was for: its principal key, unless the host's `principalAttributes` hook records other attributes.",
          "cardinality": "high"
        }
      },
      "endAttributes": {
        "outcome": {
          "type": "string",
          "values": [
            "ok",
            "error",
            "aborted"
          ],
          "description": "How it ended; a cancel is no error (status ok)."
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.method": {
      "description": "A provider running a method: under the call's carrier (over a link) or the caller's span (same host).",
      "parents": {
        "kind": "any"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "type": {
          "type": "string",
          "required": true,
          "description": "The object type's name."
        },
        "member": {
          "type": "string",
          "required": true,
          "description": "The method's name."
        },
        "object": {
          "type": "string",
          "required": true,
          "description": "The object: its type, and its params key when it has params.",
          "cardinality": "high"
        },
        "principal.kind": {
          "type": "string",
          "required": true,
          "description": "The ctx's principal kind, or `none`."
        },
        "chord.principal": {
          "type": "string",
          "required": false,
          "description": "Whom the call was for: its principal key, unless the host's `principalAttributes` hook records other attributes.",
          "cardinality": "high"
        }
      },
      "endAttributes": {
        "outcome": {
          "type": "string",
          "values": [
            "ok",
            "error",
            "aborted"
          ],
          "description": "How it ended; a cancel is no error (status ok)."
        }
      },
      "events": {
        "chord.commit": {
          "description": "A commit of the span's object (documents and tables) inside the span's work.",
          "attributes": {
            "documents": {
              "type": "number",
              "required": true,
              "description": "Documents the commit adopted."
            },
            "ops": {
              "type": "number",
              "required": true,
              "description": "Ops of the documents' prepared changes."
            },
            "rows": {
              "type": "number",
              "required": true,
              "description": "Table rows its writes changed: a `put` one, an update or delete the rows its statement changed."
            },
            "duration": {
              "type": "number",
              "required": true,
              "description": "Milliseconds from the commit's start to its adoption."
            }
          }
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.relay": {
      "description": "A relay forwarding a hold (until it is held upstream) or a call (until its reply came back), under the arriving carrier.",
      "parents": {
        "kind": "root_or_external"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "type": {
          "type": "string",
          "required": true,
          "description": "The object type's name."
        },
        "member": {
          "type": "string",
          "required": true,
          "description": "`hold`, or a call's method as `#<index>` (a relay never knows names)."
        },
        "upstream.kind": {
          "type": "string",
          "required": true,
          "description": "The kind of the upstream host."
        }
      },
      "endAttributes": {
        "outcome": {
          "type": "string",
          "values": [
            "ok",
            "error",
            "aborted"
          ],
          "description": "How it ended; a cancel is no error (status ok)."
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.hold": {
      "description": "A consumer's hold of a remote object, until its first `bound` or failure.",
      "parents": {
        "kind": "any"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "type": {
          "type": "string",
          "required": true,
          "description": "The object type's name."
        },
        "object": {
          "type": "string",
          "required": true,
          "description": "The object: its type, and its params key when it has params.",
          "cardinality": "high"
        },
        "address.kind": {
          "type": "string",
          "required": true,
          "description": "The kind of the provider's host."
        }
      },
      "endAttributes": {
        "outcome": {
          "type": "string",
          "values": [
            "bound",
            "failed",
            "ended"
          ],
          "description": "How it ended; a cancel is no error (status ok)."
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.activate": {
      "description": "One activation attempt until it is live, failed, parked or ended (a parked one resumes in a new span); linked to its object's first trigger.",
      "parents": {
        "kind": "root_or_external"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "type": {
          "type": "string",
          "required": true,
          "description": "The object type's name."
        },
        "object": {
          "type": "string",
          "required": true,
          "description": "The object: its type, and its params key when it has params.",
          "cardinality": "high"
        },
        "attempt": {
          "type": "number",
          "required": true,
          "description": "The object's attempt, from 1."
        }
      },
      "endAttributes": {
        "outcome": {
          "type": "string",
          "values": [
            "live",
            "failed",
            "parked",
            "ended"
          ],
          "description": "How it ended; a cancel is no error (status ok)."
        }
      },
      "events": {
        "chord.park": {
          "description": "The attempt parked on a dependency that is not live.",
          "attributes": {
            "cause": {
              "type": "string",
              "required": true,
              "description": "`loading`, or the waiting cause's message."
            }
          }
        },
        "chord.retry": {
          "description": "A transient failure: the next attempt waits for its backoff.",
          "attributes": {
            "backoff": {
              "type": "number",
              "required": true,
              "description": "Milliseconds until the next attempt."
            }
          }
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.open": {
      "description": "One native's open, under its attempt's span.",
      "parents": {
        "kind": "spans",
        "spans": [
          "chord.activate"
        ]
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "type": {
          "type": "string",
          "required": true,
          "description": "The object type's name."
        },
        "native": {
          "type": "number",
          "required": true,
          "description": "The native's position, from 1."
        }
      },
      "endAttributes": {},
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.close": {
      "description": "One native's close (a root: it runs at its attempt's teardown).",
      "parents": {
        "kind": "root_or_external"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "type": {
          "type": "string",
          "required": true,
          "description": "The object type's name."
        },
        "native": {
          "type": "number",
          "required": true,
          "description": "The native's position, from 1."
        },
        "reason": {
          "type": "string",
          "required": true,
          "values": [
            "end",
            "close",
            "reload"
          ],
          "description": "Why."
        }
      },
      "endAttributes": {},
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.load": {
      "description": "A host's `load`: the setups, the publication and the start barrier.",
      "parents": {
        "kind": "any"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "plugins": {
          "type": "number",
          "required": true,
          "description": "Plugins given."
        }
      },
      "endAttributes": {
        "failed": {
          "type": "number",
          "description": "Plugins left out."
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.reload": {
      "description": "A host's `reload`: staging, the commit and every object's fate.",
      "parents": {
        "kind": "any"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "plugins": {
          "type": "number",
          "required": true,
          "description": "Plugins given."
        }
      },
      "endAttributes": {
        "added": {
          "type": "number",
          "description": "Plugins added."
        },
        "changed": {
          "type": "number",
          "description": "Plugins changed."
        },
        "removed": {
          "type": "number",
          "description": "Plugins removed."
        },
        "failed": {
          "type": "number",
          "description": "Plugins whose new code failed."
        },
        "fates": {
          "type": "number",
          "description": "Objects that took a fate."
        }
      },
      "events": {
        "chord.reload.fate": {
          "description": "One object's fate in a reload.",
          "attributes": {
            "object": {
              "type": "string",
              "required": true,
              "description": "The object: its type, and its params key when it has params.",
              "cardinality": "high"
            },
            "fate": {
              "type": "string",
              "required": true,
              "values": [
                "restarted",
                "ended",
                "parked",
                "retried"
              ],
              "description": "What the object did."
            }
          }
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.resolve": {
      "description": "A route resolving an address (a worker start included), linked to its first trigger.",
      "parents": {
        "kind": "root_or_external"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "kind": {
          "type": "string",
          "required": true,
          "description": "The host kind."
        },
        "address": {
          "type": "string",
          "required": true,
          "description": "The address key.",
          "cardinality": "high"
        }
      },
      "endAttributes": {
        "outcome": {
          "type": "string",
          "values": [
            "linked",
            "failed"
          ],
          "description": "How it ended; a cancel is no error (status ok)."
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.revoke": {
      "description": "A revocation on this host.",
      "parents": {
        "kind": "any"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        }
      },
      "endAttributes": {
        "count": {
          "type": "number",
          "description": "Objects it ended and child links it closed."
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.routine": {
      "description": "One routine call (`self.on`, `self.onLive`), a root linked to the span of the commit that changed its source (when known); a throw's backoff a `chord.retry` event.",
      "parents": {
        "kind": "root_or_external"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "type": {
          "type": "string",
          "required": true,
          "description": "The object type's name."
        },
        "object": {
          "type": "string",
          "required": true,
          "description": "The object: its type, and its params key when it has params.",
          "cardinality": "high"
        },
        "routine": {
          "type": "string",
          "required": true,
          "values": [
            "on",
            "onLive"
          ],
          "description": "Its kind."
        },
        "position": {
          "type": "number",
          "required": true,
          "description": "Its position among its kind, from 1."
        }
      },
      "endAttributes": {
        "outcome": {
          "type": "string",
          "values": [
            "ok",
            "error",
            "aborted"
          ],
          "description": "How it ended; a cancel is no error (status ok)."
        }
      },
      "events": {
        "chord.commit": {
          "description": "A commit of the span's object (documents and tables) inside the span's work.",
          "attributes": {
            "documents": {
              "type": "number",
              "required": true,
              "description": "Documents the commit adopted."
            },
            "ops": {
              "type": "number",
              "required": true,
              "description": "Ops of the documents' prepared changes."
            },
            "rows": {
              "type": "number",
              "required": true,
              "description": "Table rows its writes changed: a `put` one, an update or delete the rows its statement changed."
            },
            "duration": {
              "type": "number",
              "required": true,
              "description": "Milliseconds from the commit's start to its adoption."
            }
          }
        },
        "chord.retry": {
          "description": "A transient failure: the next attempt waits for its backoff.",
          "attributes": {
            "backoff": {
              "type": "number",
              "required": true,
              "description": "Milliseconds until the next attempt."
            }
          }
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.job": {
      "description": "One phase of a job run: the intent (the start's transaction), the run, the finish; under the stored carrier (persisted) or the starter's span.",
      "parents": {
        "kind": "any"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "job": {
          "type": "string",
          "required": true,
          "description": "The job's name."
        },
        "phase": {
          "type": "string",
          "required": true,
          "values": [
            "intent",
            "run",
            "finish"
          ],
          "description": "The phase."
        },
        "object": {
          "type": "string",
          "required": true,
          "description": "The object: its type, and its params key when it has params.",
          "cardinality": "high"
        },
        "job.id": {
          "type": "string",
          "required": false,
          "description": "The run's id.",
          "cardinality": "high"
        }
      },
      "endAttributes": {
        "outcome": {
          "type": "string",
          "values": [
            "ok",
            "error",
            "aborted"
          ],
          "description": "How it ended; a cancel is no error (status ok)."
        },
        "job.id": {
          "type": "string",
          "description": "The run's id, once the intent made it.",
          "cardinality": "high"
        }
      },
      "events": {
        "chord.commit": {
          "description": "A commit of the span's object (documents and tables) inside the span's work.",
          "attributes": {
            "documents": {
              "type": "number",
              "required": true,
              "description": "Documents the commit adopted."
            },
            "ops": {
              "type": "number",
              "required": true,
              "description": "Ops of the documents' prepared changes."
            },
            "rows": {
              "type": "number",
              "required": true,
              "description": "Table rows its writes changed: a `put` one, an update or delete the rows its statement changed."
            },
            "duration": {
              "type": "number",
              "required": true,
              "description": "Milliseconds from the commit's start to its adoption."
            }
          }
        },
        "chord.retry": {
          "description": "A transient failure: the next attempt waits for its backoff.",
          "attributes": {
            "backoff": {
              "type": "number",
              "required": true,
              "description": "Milliseconds until the next attempt."
            }
          }
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    },
    "chord.http": {
      "description": "An HTTP request routed to a contributed route (`chord/http`); its path is never recorded.",
      "parents": {
        "kind": "root_or_external"
      },
      "startAttributes": {
        "chord.host": {
          "type": "string",
          "required": true,
          "description": "The host that recorded the span: its address key (`list|groceries`), `client` without an address.",
          "cardinality": "high"
        },
        "method": {
          "type": "string",
          "required": true,
          "description": "The request's method."
        },
        "route": {
          "type": "string",
          "required": true,
          "description": "The route's pattern."
        }
      },
      "endAttributes": {
        "status": {
          "type": "number",
          "description": "The response's status."
        }
      },
      "events": {
        "chord.commit": {
          "description": "A commit of the span's object (documents and tables) inside the span's work.",
          "attributes": {
            "documents": {
              "type": "number",
              "required": true,
              "description": "Documents the commit adopted."
            },
            "ops": {
              "type": "number",
              "required": true,
              "description": "Ops of the documents' prepared changes."
            },
            "rows": {
              "type": "number",
              "required": true,
              "description": "Table rows its writes changed: a `put` one, an update or delete the rows its statement changed."
            },
            "duration": {
              "type": "number",
              "required": true,
              "description": "Milliseconds from the commit's start to its adoption."
            }
          }
        }
      },
      "status": {
        "default": "ok",
        "errorWhen": "The error's category (`call_failed`, `method_failed`, `relay_failed`, `hold_failed`, `activation_failed`, `open_failed`, `close_failed`, `load_failed`, `reload_failed`, `resolve_failed`, `routine_failed`, `job_failed`, `http_failed`, `trace_failed` for `ctx.trace`) and its message, at most 500 characters; a cancel keeps status ok."
      }
    }
  },
  "metrics": {
    "chord.data.compilations": {
      "kind": "counter",
      "description": "Schemas compiled.",
      "attributes": {}
    },
    "chord.data.validations": {
      "kind": "counter",
      "description": "Values validated against a schema.",
      "attributes": {}
    },
    "chord.data.walks": {
      "kind": "counter",
      "description": "Full-value walks.",
      "attributes": {}
    },
    "chord.link.checks": {
      "kind": "counter",
      "description": "Messages checked against the message schema.",
      "attributes": {}
    },
    "chord.link.deflates": {
      "kind": "counter",
      "description": "Parts deflated.",
      "attributes": {}
    },
    "chord.link.inflates": {
      "kind": "counter",
      "description": "Parts inflated.",
      "attributes": {}
    },
    "chord.link.encodes": {
      "kind": "counter",
      "description": "Member values and ops encoded for a hold.",
      "attributes": {}
    },
    "chord.store.loads": {
      "kind": "counter",
      "description": "Persisted documents loaded.",
      "attributes": {}
    },
    "chord.store.commits": {
      "kind": "counter",
      "description": "Store commits.",
      "attributes": {}
    },
    "chord.store.statements": {
      "kind": "counter",
      "description": "SQL statements of store commits.",
      "attributes": {}
    },
    "chord.query.runs": {
      "kind": "counter",
      "description": "Live query runs.",
      "attributes": {}
    },
    "chord.cycle.visits": {
      "kind": "counter",
      "description": "Objects a cycle check visited.",
      "attributes": {}
    },
    "chord.derive.computes": {
      "kind": "counter",
      "description": "Derive computations.",
      "attributes": {}
    },
    "chord.derive.diffs": {
      "kind": "counter",
      "description": "Derive diffs.",
      "attributes": {}
    },
    "chord.registry.composes": {
      "kind": "counter",
      "description": "Registry compositions.",
      "attributes": {}
    },
    "chord.registry.adapts": {
      "kind": "counter",
      "description": "Registry values mapped for an older reader.",
      "attributes": {}
    },
    "chord.contract.adapts": {
      "kind": "counter",
      "description": "State mapped for an older consumer.",
      "attributes": {}
    },
    "chord.link.state_messages": {
      "kind": "counter",
      "description": "`state` messages this host sent of its own.",
      "attributes": {}
    },
    "chord.link.bytes": {
      "kind": "counter",
      "unit": "By",
      "description": "Bytes of encoded messages on this host's links (WebSocket frames, IPC messages).",
      "attributes": {
        "direction": {
          "type": "string",
          "required": true,
          "values": [
            "sent",
            "received"
          ],
          "description": "Which way."
        }
      }
    },
    "chord.commit.duration": {
      "kind": "histogram",
      "unit": "ms",
      "description": "Commits of an object's documents and tables, from their start to their adoption.",
      "attributes": {}
    },
    "chord.calls.in_flight": {
      "kind": "upDownCounter",
      "description": "Method calls running on this host.",
      "attributes": {}
    },
    "chord.holds.active": {
      "kind": "upDownCounter",
      "description": "Holds arriving over links and placed on this host.",
      "attributes": {}
    },
    "chord.links.open": {
      "kind": "upDownCounter",
      "description": "Open links of this host.",
      "attributes": {}
    },
    "chord.park.duration": {
      "kind": "histogram",
      "unit": "ms",
      "description": "How long an activation attempt stayed parked on its dependencies.",
      "attributes": {}
    },
    "chord.backpressure.wait": {
      "kind": "histogram",
      "unit": "ms",
      "description": "How long a link origin's state waited for a slot of its window.",
      "attributes": {}
    }
  }
}
""";
}

/// <summary>One lazily created instrument per Chord metric name, retained only while its adapter is retained.</summary>
public sealed class ChordMetrics
{
    private readonly TypedTelemetryMetrics typed;
    private readonly Dictionary<string, object> made = new(StringComparer.Ordinal);
    private readonly object gate = new();
    internal ChordMetrics(ITelemetryMetrics metrics) => typed = TelemetrySchema.CreateTypedMetrics(metrics, ChordTelemetry.Schema);
    private T Once<T>(string name, Func<T> make) where T : class
    {
        lock (gate)
        {
            if (made.TryGetValue(name, out var value)) return value as T ?? throw new ArgumentException("Wrong metric kind", nameof(name));
            var result = make(); made.Add(name, result); return result;
        }
    }
    private void Kind(string name, TelemetryMetricKind expected)
    {
        if (!ChordTelemetry.Schema.Metrics!.TryGetValue(name, out var definition) || definition.Kind != expected)
            throw new ArgumentException($"Unknown {expected} metric '{name}'.", nameof(name));
    }
    public SchemaTelemetryCounter Counter(string name) { Kind(name, TelemetryMetricKind.Counter); return Once(name, () => typed.Counter(name)); }
    public SchemaTelemetryCounter UpDownCounter(string name) { Kind(name, TelemetryMetricKind.UpDownCounter); return Once(name, () => typed.UpDownCounter(name)); }
    public SchemaTelemetryHistogram Histogram(string name) { Kind(name, TelemetryMetricKind.Histogram); return Once(name, () => typed.Histogram(name)); }
}

/// <summary>A span whose lifetime ends on an event rather than when the initiating method returns.</summary>
public sealed class OpenSpan
{
    private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ITelemetrySpan span = NoopTelemetry.Context;
    private int ended;
    public OpenSpan(ITelemetryContext parent, string name, SpanAttributes attributes)
    {
        Completion = parent.StartSpanAsync(new SpanOptions(name) { Attributes = attributes }, value =>
        {
            span = value;
            return AwaitEnd();
        });
    }
    private async Task<bool> AwaitEnd() { await done.Task.ConfigureAwait(false); return true; }
    public ITelemetrySpan Span => span;
    public bool Ended => Volatile.Read(ref ended) != 0;
    public Task Completion { get; }
    public void End() { if (Interlocked.Exchange(ref ended, 1) == 0) done.TrySetResult(); }
    public void EndWhen(Task work, string category, CancellationToken? signal = null) => _ = CloseWhen(work, category, signal);
    private async Task CloseWhen(Task work, string category, CancellationToken? signal)
    {
        string outcome;
        try { await work.ConfigureAwait(false); outcome = "ok"; }
        catch (Exception error) { outcome = ChordTelemetry.Ended(span, category, error, signal); }
        span.SetAttributes(new SpanAttributes { ["outcome"] = outcome });
        End();
    }
}
