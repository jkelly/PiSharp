import { createOriginalPluginMapping } from './original-plugin-mapping.mjs';

// Pure composition only. The loader owner must supply and authenticate genuine
// admitted namespaces; this function imports no upstream source or packages.
export function createOriginalVirtualModuleInjection({ typebox, typeboxCompile, typeboxValue, originalAi, originalTypes, originalTruncation, originalTui, withFileMutationQueue }) {
  for (const [name, namespace] of Object.entries({ typebox, typeboxCompile, typeboxValue, originalAi, originalTypes })) {
    if (namespace === null || typeof namespace !== 'object' || Array.isArray(namespace))
      throw new TypeError('An explicitly supplied original namespace is required: ' + name);
  }
  if (!Object.hasOwn(typebox, 'Type') || !Object.hasOwn(originalAi, 'Type') || originalAi.Type !== typebox.Type)
    throw new TypeError('Original AI and Typebox must share the same Type reference.');
  if (!Object.hasOwn(originalAi, 'StringEnum') || typeof originalAi.StringEnum !== 'function')
    throw new TypeError('An explicitly supplied original StringEnum is required.');
  if (!Object.hasOwn(originalTypes, 'defineTool') || originalTruncation === undefined)
    throw new TypeError('Original defineTool and public truncation suppliers are required.');
  const slices = createOriginalPluginMapping({ Type: typebox.Type, defineTool: originalTypes.defineTool,
    StringEnum: originalAi.StringEnum, truncation: originalTruncation, tui: originalTui, withFileMutationQueue });
  // Preserve entire existing Typebox namespaces, including compile/value aliases.
  // Replacing the live map wholesale with slices.modules would remove these.
  const modules = Object.freeze({ ...slices.modules,
    typebox,
    '@sinclair/typebox': typebox,
    'typebox/compile': typeboxCompile,
    '@sinclair/typebox/compile': typeboxCompile,
    'typebox/value': typeboxValue,
    '@sinclair/typebox/value': typeboxValue,
  });
  return modules;
}
