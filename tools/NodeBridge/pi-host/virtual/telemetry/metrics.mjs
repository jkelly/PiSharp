// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/telemetry/src/metrics.ts.
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
