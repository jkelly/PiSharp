// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/bedrock-converse-stream.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged except for the marked PiSharp adaptations).
import { lazyApi } from "./lazy.mjs";
const importNodeOnlyApi = (specifier)=>{
    const runtimeSpecifier = import.meta.url.endsWith(".js") ? specifier.replace(/\.ts$/, ".js") : specifier;
    return import(runtimeSpecifier);
};
let bedrockModuleOverride;
export function setBedrockProviderModule(module) {
    bedrockModuleOverride = module;
}
export const bedrockConverseStreamApi = ()=>lazyApi(async ()=>bedrockModuleOverride ?? await import("../../ai-host/api.mjs"));
