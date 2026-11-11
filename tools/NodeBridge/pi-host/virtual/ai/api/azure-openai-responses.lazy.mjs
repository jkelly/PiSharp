// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/ai/src/api/azure-openai-responses.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { lazyApi } from "./lazy.mjs";
export const azureOpenAIResponsesApi = ()=>lazyApi(()=>import("../../ai-host/api.mjs"));
