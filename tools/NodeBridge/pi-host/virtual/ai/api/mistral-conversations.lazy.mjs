// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/mistral-conversations.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { lazyApi } from "./lazy.mjs";
export const mistralConversationsApi = ()=>lazyApi(()=>import("../../ai-host/api.mjs"));
