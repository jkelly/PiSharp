// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/images.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import "./providers/images/register-builtins.mjs";
import { getImagesApiProvider } from "./images-api-registry.mjs";
function resolveImagesApiProvider(api) {
    const provider = getImagesApiProvider(api);
    if (!provider) {
        throw new Error(`No API provider registered for api: ${api}`);
    }
    return provider;
}
export async function generateImages(model, context, options) {
    const provider = resolveImagesApiProvider(model.api);
    return provider.generateImages(model, context, options);
}
