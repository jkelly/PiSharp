// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/ai/src/api/bedrock-converse-stream.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged except for the marked PiSharp adaptations).
import { lazyApi } from "./lazy.mjs";
const importNodeOnlyApi = (specifier)=>{
    const runtimeSpecifier = import.meta.url.endsWith(".js") ? specifier.replace(/\.ts$/, ".js") : specifier;
    return import(new URL(runtimeSpecifier, import.meta.url).href);
};
let bedrockModuleLoader;
export function setBedrockProviderModule(module) {
    bedrockModuleLoader = async ()=>module;
}
export function setBedrockProviderLoader(load) {
    bedrockModuleLoader = load;
}
export const bedrockConverseStreamApi = ()=>lazyApi(async ()=>bedrockModuleLoader ? await bedrockModuleLoader() : await import("../../ai-host/api.mjs"));
