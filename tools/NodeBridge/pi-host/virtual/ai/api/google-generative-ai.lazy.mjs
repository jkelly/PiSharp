// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/ai/src/api/google-generative-ai.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { lazyApi } from "./lazy.mjs";
export const googleGenerativeAIApi = ()=>lazyApi(()=>import("../../ai-host/api.mjs"));
