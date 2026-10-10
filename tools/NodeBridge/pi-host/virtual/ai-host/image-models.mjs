// PiSharp host-backed replacement for packages/ai/src/image-models.ts (static image-model catalog reads).
// The catalog lives in the PiSharp host; each function forwards to the bridge hook of the same name.
import { hostFunction } from "../bridge.mjs";

export const getImageModel = hostFunction("getImageModel");
export const getImageProviders = hostFunction("getImageProviders");
export const getImageModels = hostFunction("getImageModels");
