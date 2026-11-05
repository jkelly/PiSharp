// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/api/anthropic-messages.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { lazyApi } from "./lazy.mjs";
export const anthropicMessagesApi = ()=>lazyApi(()=>import("../../ai-host/api.mjs"));
