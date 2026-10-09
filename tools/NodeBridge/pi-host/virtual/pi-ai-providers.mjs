// PiSharp shim for `@earendil-works/pi-ai/providers/all` (upstream packages/ai/src/providers/all.ts).
// The generated model catalog and provider factories live in the PiSharp host, so every export forwards
// to the host bridge hook (`globalThis.__pisharpBridge.call("<name>", ...args)`) and throws
// "<name> is not available in the PiSharp Node bridge" when the hook is absent.
import { hostFunction } from "./bridge.mjs";

export const builtinModels = hostFunction("builtinModels");
export const builtinProviders = hostFunction("builtinProviders");
export const getAllBuiltinModels = hostFunction("getAllBuiltinModels");
export const getBuiltinClassifierModel = hostFunction("getBuiltinClassifierModel");
export const getBuiltinClassifierModels = hostFunction("getBuiltinClassifierModels");
export const getBuiltinImageModel = hostFunction("getBuiltinImageModel");
export const getBuiltinImageModels = hostFunction("getBuiltinImageModels");
export const getBuiltinModel = hostFunction("getBuiltinModel");
export const getBuiltinModelDataGeneratedAt = hostFunction("getBuiltinModelDataGeneratedAt");
export const getBuiltinModels = hostFunction("getBuiltinModels");
export const getBuiltinProviders = hostFunction("getBuiltinProviders");
export const radiusProvider = hostFunction("radiusProvider");
