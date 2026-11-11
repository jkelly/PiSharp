// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/telemetry/src/metrics.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export function createTypedMetrics(metrics, schemas) {
    const definitions = new Map();
    for (const schema of schemas){
        for (const [name, definition] of Object.entries(schema.metrics ?? {}))definitions.set(name, definition);
    }
    return {
        counter: (name)=>metrics.counter(name, definitions.get(name)),
        upDownCounter: (name)=>metrics.upDownCounter(name, definitions.get(name)),
        histogram: (name)=>metrics.histogram(name, definitions.get(name))
    };
}
