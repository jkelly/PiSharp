// Coordinator calls these controls with its admitted ORIGINAL exports. No imports, copies or package acquisition here.
import assert from 'node:assert/strict';
import { createOriginalPluginMapping } from './original-plugin-mapping.mjs';
export function originalExportContractCases(admitted) {
  if (!admitted || typeof admitted.StringEnum !== 'function' || !admitted.truncation) throw new TypeError('Admitted original StringEnum and truncation exports are mandatory.');
  const map = createOriginalPluginMapping(admitted), ai = '@mariozechner/pi-ai', coding = '@mariozechner/pi-coding-agent';
  return [
    ['original-import.shared-alias-and-export-identity', () => {
      assert.strictEqual(map.resolve(ai), map.resolve('@earendil-works/pi-ai'));
      assert.strictEqual(map.resolve(coding), map.resolve('@earendil-works/pi-coding-agent'));
      assert.strictEqual(map.named(ai, 'StringEnum'), admitted.StringEnum);
      for (const name of ['formatSize', 'truncateHead', 'truncateTail', 'truncateLine']) assert.strictEqual(map.named(coding, name), admitted.truncation[name]);
    }],
    ['original-StringEnum.todo-action-schema-and-options', () => {
      const values = ['list', 'add', 'toggle', 'clear'], options = { description: 'Action', default: 'list' };
      const schema = map.named(ai, 'StringEnum')(values, options);
      assert.equal(schema.type, 'string'); assert.deepEqual(schema.enum, values);
      assert.equal(schema.description, 'Action'); assert.equal(schema.default, 'list');
      assert.deepEqual(schema, admitted.StringEnum(values, options));
      assert.deepEqual(options, { description: 'Action', default: 'list' }); assert.deepEqual(values, ['list', 'add', 'toggle', 'clear']);
    }],
    ['original-StringEnum.empty-options-and-values-retain-original-schema', () => {
      const schema = map.named(ai, 'StringEnum')(['', 'same', 'same'], { description: '', default: '' });
      assert.equal(schema.type, 'string'); assert.deepEqual(schema.enum, ['', 'same', 'same']);
      assert.equal(Object.hasOwn(schema, 'description'), false); assert.equal(Object.hasOwn(schema, 'default'), false);
      assert.deepEqual(map.named(ai, 'StringEnum')([]), admitted.StringEnum([]));
    }],
    ['original-truncation.defaults-size-and-public-export-boundary', () => {
      assert.equal(map.named(coding, 'DEFAULT_MAX_BYTES'), 51200); assert.equal(map.named(coding, 'DEFAULT_MAX_LINES'), 2000);
      const size = map.named(coding, 'formatSize'); assert.equal(size(1023), '1023B'); assert.equal(size(1024), '1.0KB'); assert.equal(size(1048576), '1.0MB');
      for (const name of ['GREP_MAX_LINE_LENGTH', 'truncateMiddle']) assert.throws(() => map.named(coding, name), TypeError);
    }],
    ['original-truncation.empty-trailing-newline-and-inclusive-bounds', () => {
      for (const name of ['truncateHead', 'truncateTail']) {
        const fn = map.named(coding, name);
        const empty = fn(''); assert.equal(empty.content, ''); assert.equal(empty.totalLines, 0); assert.equal(empty.truncated, false);
        const exact = fn('a\nb\n', { maxLines: 2, maxBytes: 4 }); assert.equal(exact.content, 'a\nb\n'); assert.equal(exact.truncated, false);
        for (const [input, options] of [['a\nb\nc', { maxLines: 2, maxBytes: 99 }], ['a\nb\nc', { maxLines: 99, maxBytes: 3 }]])
          assert.deepEqual(fn(input, options), admitted.truncation[name](input, options));
      }
    }],
    ['original-truncation.oversize-first-line-and-UTF8-tail', () => {
      const head = map.named(coding, 'truncateHead')('abcdef\nx', { maxBytes: 3 });
      assert.equal(head.content, ''); assert.equal(head.firstLineExceedsLimit, true); assert.equal(head.truncatedBy, 'bytes');
      const tail = map.named(coding, 'truncateTail')('α😀Z', { maxBytes: 5 });
      assert.equal(tail.content, '😀Z'); assert.equal(tail.outputBytes, 5); assert.equal(tail.lastLinePartial, true);
      assert.equal(tail.content.includes('\ufffd'), false);
    }],
    ['original-truncation.single-line-original-character-suffix', () => {
      assert.deepEqual(map.named(coding, 'truncateLine')('abcd', 3), { text: 'abc... [truncated]', wasTruncated: true });
      assert.deepEqual(map.named(coding, 'truncateLine')('abc', 3), { text: 'abc', wasTruncated: false });
    }],
  ].map(([name, run]) => Object.freeze({ name, run }));
}
