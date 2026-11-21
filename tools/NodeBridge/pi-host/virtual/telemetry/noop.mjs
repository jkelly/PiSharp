// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/telemetry/src/noop.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
async function startNoopSpan(_options, callback) {
    return callback(noopTelemetrySpan);
}
const noopCounter = Object.freeze({
    add: ()=>{}
});
const noopUpDownCounter = Object.freeze({
    add: ()=>{}
});
const noopHistogram = Object.freeze({
    record: ()=>{}
});
export const NOOP_TELEMETRY_METRICS = Object.freeze({
    counter: ()=>noopCounter,
    upDownCounter: ()=>noopUpDownCounter,
    histogram: ()=>noopHistogram
});
export const NOOP_TELEMETRY_PROPAGATION = Object.freeze({
    inject: ()=>undefined,
    extract: ()=>NOOP_TELEMETRY_CONTEXT
});
const noopTelemetrySpan = {
    metrics: NOOP_TELEMETRY_METRICS,
    propagation: NOOP_TELEMETRY_PROPAGATION,
    startSpan: startNoopSpan,
    addLink: ()=>{},
    addEvent: ()=>{},
    setAttributes: ()=>{},
    setStatus: ()=>{}
};
Object.freeze(noopTelemetrySpan);
export const NOOP_TELEMETRY_CONTEXT = noopTelemetrySpan;
