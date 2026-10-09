// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/model-operations.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { ModelsError } from "./models-error.mjs";
export function getModelType(model) {
    return model.type ?? "chat";
}
export function isModelType(model, type) {
    return getModelType(model) === type;
}
export function assertChatModel(model) {
    if (!isModelType(model, "chat")) {
        throw new ModelsError("provider", `Model ${model.provider}/${model.id} is not a chat model`);
    }
}
export function assertImageModel(model) {
    if (!isModelType(model, "image")) {
        throw new ModelsError("provider", `Model ${model.provider}/${model.id} is not an image model`);
    }
}
export function assertClassifierModel(model) {
    if (!isModelType(model, "classifier")) {
        throw new ModelsError("provider", `Model ${model.provider}/${model.id} is not a classifier model`);
    }
}
export function assertClassifierInputSupported(model, context) {
    if (context.images?.length && !model.input.includes("image")) {
        throw new ModelsError("provider", `Model ${model.provider}/${model.id} does not accept image input`);
    }
}
export function imageErrorResult(model, error, aborted = false) {
    return {
        api: model.api,
        provider: model.provider,
        model: model.id,
        output: [],
        stopReason: aborted ? "aborted" : "error",
        errorMessage: error instanceof Error ? error.message : String(error),
        timestamp: Date.now()
    };
}
export function classifierErrorResult(model, error, aborted = false) {
    return {
        api: model.api,
        provider: model.provider,
        model: model.id,
        answers: {},
        stopReason: aborted ? "aborted" : "error",
        errorMessage: error instanceof Error ? error.message : String(error),
        timestamp: Date.now()
    };
}
