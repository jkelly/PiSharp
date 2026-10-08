// Root-parameter adaptation of reviewed inspect-ast-archives.mjs inspectArchive.
// Original SHA ac6c53f6dd692b2c69bd8ecfcd1003370f6d389a776a4a269f6d14be370db763.
// Exact publisher prefixes and observed Node USTAR time-header layout differ.
// No tar/header/member bytes are rewritten. Original strict field rules remain.
import assert from 'node:assert/strict';
import { gunzipSync } from 'node:zlib';

const utf8 = bytes => new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
function safeMember(name, directory, caps, root) {
  assert(!/[\\\0]/.test(name) && !name.startsWith('/') && !/^[A-Za-z]:/.test(name), 'Unsafe tar path');
  assert(Buffer.byteLength(name) <= caps.pathUtf8Bytes, 'Tar path byte limit');
  const value = directory && name.endsWith('/') ? name.slice(0, -1) : name;
  const parts = value.split('/'); assert(parts.length <= caps.depth, 'Tar path depth limit');
  for (const part of parts) {
    assert(part && part !== '.' && part !== '..' && !/[<>:"|?*\x00-\x1f\x7f]/.test(part) && !/[. ]$/.test(part), 'Unsafe tar component');
    assert(Buffer.byteLength(part) <= caps.componentUtf8Bytes, 'Tar component byte limit');
    assert(!/^(?:con|prn|aux|nul|com[1-9\u00b9\u00b2\u00b3]|lpt[1-9\u00b9\u00b2\u00b3])(?:\.|$)/i.test(part), 'Reserved Windows tar component');
  }
  assert(value === root ? directory : value.startsWith(root + '/'), 'Tar outside exact publisher root');
  return value === root ? '' : value.slice(root.length + 1);
}
export function inspectArchive(compressed, caps, root) {
  assert(root === 'package' || root === 'node v22.19', 'Unadmitted publisher root');
  assert(compressed.length > 0 && compressed.length <= caps.compressedBytes, 'Compressed archive byte limit');
  const tar = gunzipSync(compressed, { maxOutputLength: caps.expandedBytes });
  assert(tar.length <= caps.expandedBytes && tar.length % 512 === 0, 'Expanded tar byte/alignment limit');
  const files = new Map(), seen = new Map(); let offset = 0, memberCount = 0, directories = 0, metadataHeaders = 0, localPax = null, publisherTimeHeaders = 0;
  const field = (header, start, count) => { const data = header.subarray(start, start + count), end = data.indexOf(0); if (end >= 0) { assert(data.subarray(end).every(b => b === 0), 'Tar field bytes after NUL'); return utf8(data.subarray(0, end)); } return utf8(data); };
  const octal = (header, start, count) => {
    const data = header.subarray(start, start + count); assert(data.every(b => b === 0 || b === 32 || (b >= 48 && b <= 55)), 'Unsupported tar numeric encoding');
    const raw = data.toString('ascii').replace(/^[\0 ]+|[\0 ]+$/g, ''); assert(/^[0-7]*$/.test(raw), 'Embedded tar numeric separator');
    const result = raw ? Number.parseInt(raw, 8) : 0; assert(Number.isSafeInteger(result), 'Unsafe tar number'); return result;
  };
  const pax = data => {
    assert(data.length <= caps.paxBytes, 'PAX byte limit'); const result = Object.create(null); let cursor = 0;
    while (cursor < data.length) {
      const space = data.indexOf(32, cursor); assert(space > cursor, 'Invalid PAX record');
      const sizeText = data.subarray(cursor, space).toString('ascii'); assert(/^[1-9][0-9]*$/.test(sizeText), 'Invalid PAX length');
      const end = cursor + Number(sizeText); assert(Number.isSafeInteger(end) && end > space + 1 && end <= data.length && data[end - 1] === 10, 'PAX bounds');
      const record = utf8(data.subarray(space + 1, end - 1)), equals = record.indexOf('='); assert(equals > 0, 'Invalid PAX attribute');
      const key = record.slice(0, equals); assert(['path', 'mtime', 'atime', 'ctime', 'uid', 'gid', 'uname', 'gname'].includes(key) && !Object.hasOwn(result, key), 'Unsupported/duplicate PAX attribute');
      result[key] = record.slice(equals + 1); cursor = end;
    }
    return result;
  };
  while (offset + 512 <= tar.length) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(b => b === 0)) {
      assert(tar.length - offset >= 1024 && tar.subarray(offset).every(b => b === 0), 'Invalid tar terminal blocks');
      assert(localPax === null && files.size > 0, 'Dangling PAX or empty tar');
      for (const [key] of seen) { const parts = key.split('/'); for (let i = 1; i < parts.length; i++) assert(seen.get(parts.slice(0, i).join('/')) !== 'file', 'File/directory parent conflict'); }
      return { files, publisherRoot: root, memberCount, directories, metadataHeaders, publisherTimeHeaders, expandedBytes: tar.length, regularBytes: [...files.values()].reduce((sum, data) => sum + data.length, 0) };
    }
    assert(++memberCount <= caps.members, 'Tar member count limit');
    const sum = header.reduce((total, byte, index) => total + (index >= 148 && index < 156 ? 32 : byte), 0); assert.equal(sum, octal(header, 148, 8), 'Tar checksum');
    const size = octal(header, 124, 12); assert(size <= caps.memberBytes, 'Tar member byte limit');
    const start = offset + 512, end = start + size, next = start + Math.ceil(size / 512) * 512;
    assert(end <= tar.length && next <= tar.length, 'Truncated tar member');
    assert(tar.subarray(end, next).every(b => b === 0), 'Nonzero tar member padding');
    const type = field(header, 156, 1) || '0'; offset = next;
    if (type === 'x') { assert(localPax === null, 'Consecutive PAX headers'); safeMember(field(header, 0, 100), false, caps, root); localPax = pax(tar.subarray(start, end)); metadataHeaders++; continue; }
    assert(type === '0' || type === '5', 'Tar link/device/global-PAX/extension rejected');
    assert(field(header, 157, 100) === '', 'Tar link target on regular member');
    let prefix;
    if (root === 'node v22.19') {
      assert(header.subarray(257, 265).equals(Buffer.from('ustar\0' + '00')), 'Unexpected Node publisher tar magic/version');
      assert(header[475] === 0 && header.subarray(500).every(byte => byte === 0), 'Unexpected Node publisher reserved header bytes');
      prefix = field(header, 345, 130); octal(header, 476, 12); octal(header, 488, 12);
      if (header.subarray(476, 500).some(byte => byte !== 0)) publisherTimeHeaders++;
    } else prefix = field(header, 345, 155);
    const leaf = field(header, 0, 100);
    const originalName = prefix ? prefix + '/' + leaf : leaf; safeMember(originalName, type === '5', caps, root);
    const name = safeMember(localPax?.path ?? originalName, type === '5', caps, root); localPax = null;
    const key = name.toLowerCase(); assert(!seen.has(key), 'Duplicate or Windows case-colliding tar path'); seen.set(key, type === '0' ? 'file' : 'directory');
    if (type === '5') { assert.equal(size, 0, 'Directory member data'); directories++; }
    else { assert(name, 'Empty regular member path'); files.set(name, tar.subarray(start, end)); }
  }
  throw new Error('Missing tar terminal blocks');
}
