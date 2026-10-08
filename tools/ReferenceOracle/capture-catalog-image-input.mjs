// Additive offline Source capture; no original fixture, source module or package data is replaced.
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { registerHooks } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';

const script = fileURLToPath(import.meta.url);
const [oracleArgument, outputArgument] = process.argv.slice(2);
assert(oracleArgument && outputArgument && process.argv.length === 4, 'Supply pinned oracle root and fresh output file');
const oracleRoot = path.resolve(oracleArgument), output = path.resolve(outputArgument);
assert(!fs.existsSync(output), 'Capture output must be fresh');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const pin = filename => { const bytes = fs.readFileSync(filename); return { bytes: bytes.length, sha256: hash(bytes) }; };
const lockPath = path.join(path.dirname(script), 'catalog-image-input.lock.json');
const lock = JSON.parse(fs.readFileSync(lockPath, 'utf8'));
assert.equal(lock.sourceSha, 'd86654abb8862e201933517d6f1fce9f88dd117f');
assert.deepEqual(pin(process.execPath), lock.node);
const archivePath = path.resolve(path.dirname(script), '../../artifacts/released-baseline/pi-0.99.1-source.tar.gz');
assert.deepEqual(pin(archivePath), lock.sourceArchive);
assert.deepEqual(pin(path.join(oracleRoot, 'PREPARED_SOURCE_AND_PACKAGE_MANIFEST.json')), lock.preparedSourceManifest);
const locked = new Map();
for (const entry of lock.files) {
    const filename = path.resolve(oracleRoot, entry.relative);
    assert(filename.startsWith(oracleRoot + path.sep), 'Locked path escaped oracle');
    assert(!locked.has(filename), 'Duplicate locked path');
    assert.deepEqual(pin(filename), { bytes: entry.bytes, sha256: entry.sha256 });
    locked.set(filename, entry);
}

const loaded = new Set();
registerHooks({ load(url, context, next) {
    if (url.startsWith('file:')) {
        const filename = path.resolve(fileURLToPath(url));
        assert(locked.has(filename), 'An unpinned Source module was requested');
        const entry = locked.get(filename);
        assert.deepEqual(pin(filename), { bytes: entry.bytes, sha256: entry.sha256 });
        loaded.add(filename);
    }
    return next(url, context);
} });
let prohibitedNetworkCalls = 0;
globalThis.fetch = () => { prohibitedNetworkCalls++; throw new Error('This metadata/transform capture permits no network'); };
const catalogModule = await import(pathToFileURL(path.join(oracleRoot, 'upstream/packages/ai/src/model-catalog.ts')));
const { transformMessages } = await import(pathToFileURL(path.join(oracleRoot, 'upstream/packages/ai/src/api/transform-messages.ts')));
const flatten = { chat: catalogModule.flattenChatModelCatalog, image: catalogModule.flattenImageModelCatalog,
    classifier: catalogModule.flattenClassifierModelCatalog };
const dataPrefix = 'upstream/packages/ai/src/providers/data/';
const dataManifestPath = path.join(oracleRoot, dataPrefix, '.manifest.json');
const dataManifest = JSON.parse(fs.readFileSync(dataManifestPath, 'utf8'));
const shardPins = lock.files.filter(entry => entry.relative.startsWith(dataPrefix) && !entry.relative.endsWith('/.manifest.json'));
assert.equal(shardPins.length, 42);
assert.deepEqual(Object.keys(dataManifest.files).sort(), shardPins.map(entry => path.basename(entry.relative)).sort());
const observations = [], controls = new Map();
const counts = { chat: 0, image: 0, classifier: 0 }, imageCounts = { chat: 0, image: 0, classifier: 0 };
for (const entry of shardPins) {
    const name = path.basename(entry.relative), provider = name.slice(0, -5);
    assert.equal(dataManifest.files[name], entry.sha256);
    const groups = JSON.parse(fs.readFileSync(path.join(oracleRoot, entry.relative), 'utf8'));
    const original = JSON.stringify(groups);
    const flattened = Object.fromEntries(Object.entries(flatten).map(([type, helper]) => [type, helper(provider, groups)]));
    const models = [];
    for (const [api, group] of Object.entries(groups)) {
        for (const [key, model] of Object.entries(group)) {
            assert(Object.hasOwn(flatten, model.type));
            assert.equal(model.provider, provider); assert.equal(model.api, api); assert.equal(key, model.type + ':' + model.id);
            assert.strictEqual(flattened[model.type][model.id], model, 'Whole Source helper changed model ownership');
            assert(Array.isArray(model.input) && model.input.length > 0);
            assert(model.input.every(modality => modality === 'text' || modality === 'image'));
            const declaresImageInput = model.input.includes('image');
            counts[model.type]++; if (declaresImageInput) imageCounts[model.type]++;
            models.push({ type: model.type, id: model.id, provider, api, input: [...model.input], declaresImageInput });
            if (model.type === 'chat' && !controls.has(declaresImageInput)) controls.set(declaresImageInput, model);
        }
    }
    assert.equal(JSON.stringify(groups), original, 'Source flatten helper mutated canonical metadata');
    observations.push({ provider, source: { relative: entry.relative, bytes: entry.bytes, sha256: entry.sha256 }, models });
}
assert.deepEqual(counts, { chat: 1523, image: 57, classifier: 12 });
assert.deepEqual(imageCounts, { chat: 1054, image: 55, classifier: 0 });
assert.equal(controls.size, 2);
const imageTransformObservations = [];
for (const [declaresImageInput, model] of controls) {
    const messages = [{ role: 'user', timestamp: 123, content: [{ type: 'text', text: 'before' },
        { type: 'image', mimeType: 'image/png', data: 'AA==' }, { type: 'text', text: 'after' }] }];
    const original = JSON.stringify(messages), transformed = transformMessages(messages, model);
    assert.equal(JSON.stringify(messages), original, 'Source transform changed canonical input');
    imageTransformObservations.push({ model: { provider: model.provider, id: model.id, api: model.api, input: [...model.input] },
        declaresImageInput, messages, transformed, canonicalContextUnchanged: true });
}
assert.equal(prohibitedNetworkCalls, 0);
assert.deepEqual([...loaded].map(filename => locked.get(filename).relative).sort(),
    ['upstream/packages/ai/src/model-catalog.ts', 'upstream/packages/ai/src/api/transform-messages.ts'].sort());
const report = { schemaVersion: 1, kind: 'additive-whole-source-catalog-image-input-capture', sourceSha: lock.sourceSha,
    sourceLock: pin(lockPath), captureProgram: pin(script), node: pin(process.execPath), sourceFilesVerified: locked.size,
    sourceArchive: pin(archivePath), preparedSourceManifest: lock.preparedSourceManifest,
    actualLoadedSourceFiles: [...loaded].sort().map(filename => locked.get(filename)), sourceOrSdkEdits: 0,
    originalFixtureEdits: 0, prohibitedNetworkCalls, sourceDataManifest: pin(dataManifestPath), counts, imageCounts,
    observations, imageTransformObservations };
fs.writeFileSync(output, JSON.stringify(report, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ models: Object.values(counts).reduce((sum, count) => sum + count, 0), providers: observations.length,
    sourceFilesVerified: locked.size, loaded: loaded.size, networkCalls: prohibitedNetworkCalls, report: { path: output, ...pin(output) } }));
