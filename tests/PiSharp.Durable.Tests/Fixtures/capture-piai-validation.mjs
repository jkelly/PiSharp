// Prints Fixtures/piai-validation.upstream.json: what the pinned pi-ai's validateToolArguments (the Pi commit given with --commit, one of tools/DurableFixtures/pin.mjs;
// that release's published pi-ai) returns or throws for PiAiValidationCases.cs's schemas and arguments. Each schema is validated twice: as the TypeBox object
// (origin "typebox") and as its plain JSON copy (origin "json", which drops TypeBox's hidden "~kind"). A row is {schema, origin, arguments,
// result} or {schema, origin, arguments, error}; `result` is JSON.stringify of the returned arguments.
// Usage, with a Pi checkout at a pinned commit and its node_modules (pi-ai is that release's published build; see
// tools/DurableFixtures/capture-all.mjs, which runs every capture into the commit's fixture set):
//   node --no-warnings capture-piai-validation.mjs <pi checkout> --commit <sha> > piai-validation.upstream.json
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

import { pinnedArguments } from '../../../tools/DurableFixtures/pin.mjs';

const pin = pinnedArguments(process.argv.slice(2), 'capture-piai-validation.mjs <pi checkout> --commit <sha>');
const { pi } = pin;
const at = (path) => pathToFileURL(join(pi, path)).href;
const { Type, validateToolArguments } = await import(at('node_modules/@earendil-works/pi-ai/dist/index.js'));

const cases = [
  [Type.Object({ text: Type.String() }), [{ text: 'x' }, { text: 1 }, { text: { not: 'a string' } }, {}, { text: null }, { text: true }]],
  [Type.Object({ text: Type.Optional(Type.String()) }), [{}, { text: 7 }, { text: null }, { text: [1] }]],
  [Type.Object({ n: Type.Number(), flag: Type.Optional(Type.Boolean()) }), [{ n: '5' }, { n: 'abc' }, { n: 5, flag: 'true' }, { n: 1, flag: 'yes' }]],
  [Type.Object({ items: Type.Array(Type.String()) }), [{ items: ['a'] }, { items: ['a', 2] }, { items: 'a' }, { items: '["a","b"]' }]],
  [Type.Object({ mode: Type.Union([Type.Literal('a'), Type.Literal('b')]) }), [{ mode: 'a' }, { mode: 'c' }, { mode: 1 }]],
  [Type.Object({}), [{}, { extra: 1 }]],
  [Type.Object({ path: Type.String(), limit: Type.Optional(Type.Integer({ minimum: 1 })) }),
    [{ path: 'x', limit: '3' }, { path: 'x', limit: 0 }, { path: 'x', limit: 2.5 }, { limit: 2 }]],
  [Type.Object({ opts: Type.Object({ deep: Type.Boolean() }) }), [{ opts: { deep: 'false' } }, { opts: 'x' }, { opts: {} }]],
];

const rows = [];
for (const [schema, inputs] of cases) {
  for (const [origin, parameters] of [['typebox', schema], ['json', JSON.parse(JSON.stringify(schema))]]) {
    for (const args of inputs) {
      const row = { schema: JSON.parse(JSON.stringify(schema)), origin, arguments: args };
      try {
        row.result = JSON.stringify(validateToolArguments({ name: 'probe', parameters }, { type: 'toolCall', id: 'c', name: 'probe', arguments: args }));
      } catch (error) {
        row.error = error.message;
      }
      rows.push(row);
    }
  }
}
console.log(JSON.stringify(rows));
