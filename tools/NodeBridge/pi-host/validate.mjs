// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/validation.ts (validateToolArguments, coerceWithJsonSchema,
// normalizeOptionalNulls, formatValidationPath).
//
// Upstream compiles the tool's TypeBox schema (typebox/compile) and converts values with typebox/value. The bridge checks the same JSON
// Schema with the validator below, which reports errors in TypeBox's localized wording (instancePath, keyword, message).

const TYPEBOX_KIND = Symbol.for('TypeBox.Kind');
const typeOf = (value) => value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value;
function matchesJsonType(value, type) {
  switch (type) {
    case 'number': return typeof value === 'number';
    case 'integer': return typeof value === 'number' && Number.isInteger(value);
    case 'boolean': return typeof value === 'boolean';
    case 'string': return typeof value === 'string';
    case 'null': return value === null;
    case 'array': return Array.isArray(value);
    case 'object': return typeof value === 'object' && value !== null && !Array.isArray(value);
    default: return true;
  }
}
const types = (schema) => typeof schema?.type === 'string' ? [schema.type] : Array.isArray(schema?.type) ? schema.type.filter(t => typeof t === 'string') : [];
const equal = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** JSON Schema check producing TypeBox-style errors: [{ instancePath, keyword, message, params }]. */
export function schemaErrors(schema, value, path = '', root = schema, out = []) {
  if (schema === true || schema === undefined || schema === null) return out;
  if (schema === false) { out.push({ instancePath: path, keyword: 'false schema', message: 'boolean schema is false', params: {} }); return out; }
  if (typeof schema.$ref === 'string') {
    const target = resolveRef(root, schema.$ref);
    if (target) return schemaErrors(target, value, path, root, out);
  }
  const ts = types(schema);
  if (ts.length && !ts.some(t => matchesJsonType(value, t))) {
    out.push({ instancePath: path, keyword: 'type', message: `must be ${ts.join(',')}`, params: { type: ts.join(',') } });
    return out;
  }
  if (schema.const !== undefined && !equal(value, schema.const)) out.push({ instancePath: path, keyword: 'const', message: 'must be equal to constant', params: { allowedValue: schema.const } });
  if (Array.isArray(schema.enum) && !schema.enum.some(item => equal(item, value))) out.push({ instancePath: path, keyword: 'enum', message: 'must be equal to one of the allowed values', params: { allowedValues: schema.enum } });
  if (typeof value === 'string') {
    if (typeof schema.minLength === 'number' && [...value].length < schema.minLength) out.push({ instancePath: path, keyword: 'minLength', message: `must not have fewer than ${schema.minLength} characters`, params: {} });
    if (typeof schema.maxLength === 'number' && [...value].length > schema.maxLength) out.push({ instancePath: path, keyword: 'maxLength', message: `must not have more than ${schema.maxLength} characters`, params: {} });
    if (typeof schema.pattern === 'string' && !new RegExp(schema.pattern, 'u').test(value)) out.push({ instancePath: path, keyword: 'pattern', message: `must match pattern "${schema.pattern}"`, params: {} });
  }
  if (typeof value === 'number') {
    if (typeof schema.minimum === 'number' && value < schema.minimum) out.push({ instancePath: path, keyword: 'minimum', message: `must be >= ${schema.minimum}`, params: {} });
    if (typeof schema.maximum === 'number' && value > schema.maximum) out.push({ instancePath: path, keyword: 'maximum', message: `must be <= ${schema.maximum}`, params: {} });
    if (typeof schema.exclusiveMinimum === 'number' && value <= schema.exclusiveMinimum) out.push({ instancePath: path, keyword: 'exclusiveMinimum', message: `must be > ${schema.exclusiveMinimum}`, params: {} });
    if (typeof schema.exclusiveMaximum === 'number' && value >= schema.exclusiveMaximum) out.push({ instancePath: path, keyword: 'exclusiveMaximum', message: `must be < ${schema.exclusiveMaximum}`, params: {} });
    if (typeof schema.multipleOf === 'number' && value % schema.multipleOf !== 0) out.push({ instancePath: path, keyword: 'multipleOf', message: `must be multiple of ${schema.multipleOf}`, params: {} });
  }
  if (Array.isArray(value)) {
    if (typeof schema.minItems === 'number' && value.length < schema.minItems) out.push({ instancePath: path, keyword: 'minItems', message: `must not have fewer than ${schema.minItems} items`, params: {} });
    if (typeof schema.maxItems === 'number' && value.length > schema.maxItems) out.push({ instancePath: path, keyword: 'maxItems', message: `must not have more than ${schema.maxItems} items`, params: {} });
    if (schema.uniqueItems === true && new Set(value.map(v => JSON.stringify(v))).size !== value.length) out.push({ instancePath: path, keyword: 'uniqueItems', message: 'must not have duplicate items', params: {} });
    const prefix = Array.isArray(schema.prefixItems) ? schema.prefixItems : Array.isArray(schema.items) ? schema.items : undefined;
    if (prefix) prefix.forEach((item, index) => { if (index < value.length) schemaErrors(item, value[index], `${path}/${index}`, root, out); });
    const rest = Array.isArray(schema.items) ? schema.additionalItems : prefix ? schema.items === undefined ? undefined : (Array.isArray(schema.items) ? undefined : schema.items) : schema.items;
    if (rest !== undefined && typeof rest === 'object') value.forEach((item, index) => { if (!prefix || index >= prefix.length) schemaErrors(rest, item, `${path}/${index}`, root, out); });
  }
  if (typeof value === 'object' && value !== null && !Array.isArray(value)) {
    const required = Array.isArray(schema.required) ? schema.required.filter(name => !(name in value)) : [];
    if (required.length) out.push({ instancePath: path, keyword: 'required', message: `must have required properties ${required.join(', ')}`, params: { requiredProperties: required } });
    const properties = schema.properties ?? {};
    for (const [name, sub] of Object.entries(properties)) if (name in value && value[name] !== undefined) schemaErrors(sub, value[name], `${path}/${name}`, root, out);
    const patterns = schema.patternProperties ?? {};
    for (const [name, item] of Object.entries(value)) {
      if (name in properties) continue;
      const matched = Object.entries(patterns).filter(([pattern]) => new RegExp(pattern, 'u').test(name));
      for (const [, sub] of matched) schemaErrors(sub, item, `${path}/${name}`, root, out);
      if (matched.length) continue;
      if (schema.additionalProperties === false) out.push({ instancePath: `${path}/${name}`, keyword: 'additionalProperties', message: 'must not have additional properties', params: { additionalProperty: name } });
      else if (typeof schema.additionalProperties === 'object') schemaErrors(schema.additionalProperties, item, `${path}/${name}`, root, out);
    }
    if (typeof schema.minProperties === 'number' && Object.keys(value).length < schema.minProperties) out.push({ instancePath: path, keyword: 'minProperties', message: `must not have fewer than ${schema.minProperties} properties`, params: {} });
  }
  if (Array.isArray(schema.allOf)) for (const sub of schema.allOf) schemaErrors(sub, value, path, root, out);
  if (Array.isArray(schema.anyOf) && !schema.anyOf.some(sub => schemaErrors(sub, value, path, root, []).length === 0))
    out.push({ instancePath: path, keyword: 'anyOf', message: 'must match a schema in anyOf', params: {} });
  if (Array.isArray(schema.oneOf) && schema.oneOf.filter(sub => schemaErrors(sub, value, path, root, []).length === 0).length !== 1)
    out.push({ instancePath: path, keyword: 'oneOf', message: 'must match exactly one schema in oneOf', params: {} });
  if (schema.not !== undefined && schemaErrors(schema.not, value, path, root, []).length === 0) out.push({ instancePath: path, keyword: 'not', message: 'must not be valid', params: {} });
  return out;
}

function resolveRef(root, ref) {
  if (!ref.startsWith('#')) {
    const all = [root, ...Object.values(root.$defs ?? {}), ...Object.values(root.definitions ?? {})];
    return all.find(s => s?.$id === ref);
  }
  let node = root;
  for (const part of ref.slice(1).split('/').filter(Boolean)) node = node?.[decodeURIComponent(part.replace(/~1/g, '/').replace(/~0/g, '~'))];
  return node;
}

const check = (schema, value) => schemaErrors(schema, value).length === 0;

function coercePrimitiveByType(value, type) {
  switch (type) {
    case 'number': if (value === null) return 0; if (typeof value === 'string' && value.trim() !== '') { const n = Number(value); if (Number.isFinite(n)) return n; } if (typeof value === 'boolean') return value ? 1 : 0; return value;
    case 'integer': if (value === null) return 0; if (typeof value === 'string' && value.trim() !== '') { const n = Number(value); if (Number.isInteger(n)) return n; } if (typeof value === 'boolean') return value ? 1 : 0; return value;
    case 'boolean': if (value === null) return false; if (value === 'true') return true; if (value === 'false') return false; if (value === 1) return true; if (value === 0) return false; return value;
    case 'string': if (value === null) return ''; if (typeof value === 'number' || typeof value === 'boolean') return String(value); return value;
    case 'null': if (value === '' || value === 0 || value === false) return null; return value;
    default: return value;
  }
}
function coerceUnion(value, schemas) {
  for (const schema of schemas) if (check(schema, value)) return value;
  for (const schema of schemas) { const coerced = coerceWithJsonSchema(structuredClone(value), schema); if (check(schema, coerced)) return coerced; }
  return value;
}
export function coerceWithJsonSchema(value, schema) {
  if (!schema || typeof schema !== 'object') return value;
  let next = value;
  if (Array.isArray(schema.allOf)) for (const nested of schema.allOf) next = coerceWithJsonSchema(next, nested);
  if (Array.isArray(schema.anyOf)) next = coerceUnion(next, schema.anyOf);
  if (Array.isArray(schema.oneOf)) next = coerceUnion(next, schema.oneOf);
  const ts = types(schema);
  const matchesUnionMember = ts.length > 1 && ts.some(t => matchesJsonType(next, t));
  if (ts.length > 0 && !matchesUnionMember)
    for (const t of ts) { const candidate = coercePrimitiveByType(next, t); if (candidate !== next) { next = candidate; break; } }
  if (ts.includes('object') && typeOf(next) === 'object') {
    const defined = new Set(Object.keys(schema.properties ?? {}));
    for (const [key, sub] of Object.entries(schema.properties ?? {})) if (key in next) next[key] = coerceWithJsonSchema(next[key], sub);
    if (schema.additionalProperties && typeof schema.additionalProperties === 'object')
      for (const [key, item] of Object.entries(next)) if (!defined.has(key)) next[key] = coerceWithJsonSchema(item, schema.additionalProperties);
  }
  if (ts.includes('array') && Array.isArray(next)) {
    if (Array.isArray(schema.items)) schema.items.forEach((sub, index) => { if (index < next.length && sub) next[index] = coerceWithJsonSchema(next[index], sub); });
    else if (schema.items && typeof schema.items === 'object') for (let index = 0; index < next.length; index++) next[index] = coerceWithJsonSchema(next[index], schema.items);
  }
  return next;
}
function normalizeOptionalNulls(value, schema) {
  if (Array.isArray(value)) {
    if (Array.isArray(schema?.items)) value.forEach((item, index) => { if (schema.items[index]) normalizeOptionalNulls(item, schema.items[index]); });
    else if (schema?.items) for (const item of value) normalizeOptionalNulls(item, schema.items);
    return;
  }
  if (typeof value !== 'object' || value === null || !schema?.properties) return;
  const required = new Set(schema.required ?? []);
  for (const [key, sub] of Object.entries(schema.properties)) {
    if (!(key in value)) continue;
    if (value[key] === null && !required.has(key) && typeof sub?.$ref !== 'string' && !check(sub, null)) delete value[key];
    else normalizeOptionalNulls(value[key], sub);
  }
}
function formatValidationPath(error) {
  if (error.keyword === 'required') {
    const property = error.params?.requiredProperties?.[0];
    if (property) { const base = error.instancePath.replace(/^\//, '').replace(/\//g, '.'); return base ? `${base}.${property}` : property; }
  }
  const p = error.instancePath.replace(/^\//, '').replace(/\//g, '.');
  return p || 'root';
}

/** Source validateToolArguments: normalize optional nulls, convert, coerce JSON-schema (non-TypeBox) parameters, then check. */
export function validateToolArguments(tool, toolCall) {
  const args = structuredClone(toolCall.arguments);
  normalizeOptionalNulls(args, tool.parameters);
  let current = coerceWithJsonSchema(args, tool.parameters); // Value.Convert and the JSON-schema coercion share these rules here.
  if (Object.getOwnPropertySymbols(tool.parameters).includes(TYPEBOX_KIND) && current !== args) current = args;
  const errors = schemaErrors(tool.parameters, current);
  if (errors.length === 0) return current;
  const text = errors.map(error => `  - ${formatValidationPath(error)}: ${error.message}`).join('\n') || 'Unknown validation error';
  throw new Error(`Validation failed for tool "${toolCall.name}":\n${text}\n\nReceived arguments:\n${JSON.stringify(toolCall.arguments, null, 2)}`);
}
