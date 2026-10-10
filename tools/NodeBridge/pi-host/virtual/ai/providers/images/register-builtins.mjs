// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/images/register-builtins.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { registerImagesApiProvider } from "../../images-api-registry.mjs";
let openRouterImagesProviderModulePromise;
function createLazyLoadErrorImages(model, error) {
    return {
        api: model.api,
        provider: model.provider,
        model: model.id,
        output: [],
        stopReason: "error",
        errorMessage: error instanceof Error ? error.message : String(error),
        timestamp: Date.now()
    };
}
function loadOpenRouterImagesProviderModule() {
    openRouterImagesProviderModulePromise ||= import("../../../ai-host/api.mjs").then((module)=>module);
    return openRouterImagesProviderModulePromise;
}
export const generateImagesOpenRouter = async (model, context, options)=>{
    try {
        const module = await loadOpenRouterImagesProviderModule();
        return await module.generateImages(model, context, options);
    } catch (error) {
        return createLazyLoadErrorImages(model, error);
    }
};
export function registerBuiltInImagesApiProviders() {
    registerImagesApiProvider({
        api: "openrouter-images",
        generateImages: generateImagesOpenRouter
    });
}
registerBuiltInImagesApiProviders();
