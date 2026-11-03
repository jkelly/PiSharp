// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/telemetry/src/index.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export { createTypedMetrics } from "./metrics.mjs";
export { NOOP_TELEMETRY_CONTEXT, NOOP_TELEMETRY_METRICS, NOOP_TELEMETRY_PROPAGATION } from "./noop.mjs";
export function defineTelemetrySchema(schema) {
    return schema;
}
function bindTypedSpanStarter(telemetryContext) {
    const startSpan = (name, attributes, callback)=>telemetryContext.startSpan({
            name,
            attributes
        }, (span)=>callback(span, bindTypedSpanStarter(span)));
    return startSpan;
}
export function createTypedSpanStarter(telemetryContext, _schemas) {
    return bindTypedSpanStarter(telemetryContext);
}
export { InMemoryTelemetryContext } from "./memory.mjs";
