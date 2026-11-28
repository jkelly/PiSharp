// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/ai/src/api/pi-messages.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { lazyApi } from "./lazy.mjs";
export const piMessagesApi = ()=>lazyApi(()=>import("../../ai-host/api.mjs"));
