// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/telemetry/src/index.ts.
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
export { InMemoryTelemetryContext, telemetryNow } from "./memory.mjs";
