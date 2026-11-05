// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/api/google-generative-ai.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { lazyApi } from "./lazy.mjs";
export const googleGenerativeAIApi = ()=>lazyApi(()=>import("../../ai-host/api.mjs"));
