// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/api/openai-responses.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { lazyApi } from "./lazy.mjs";
export const openAIResponsesApi = ()=>lazyApi(()=>import("../../ai-host/api.mjs"));
