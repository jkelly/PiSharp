// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/models-store.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export class InMemoryModelsStore {
    entries = new Map();
    async read(providerId, options) {
        options?.signal?.throwIfAborted();
        const entry = this.entries.get(providerId);
        return entry ? structuredClone(entry) : undefined;
    }
    async write(providerId, entry, options) {
        options?.signal?.throwIfAborted();
        this.entries.set(providerId, structuredClone(entry));
    }
    async delete(providerId, options) {
        options?.signal?.throwIfAborted();
        this.entries.delete(providerId);
    }
}
