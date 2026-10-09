// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/images-api-registry.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
const imagesApiProviderRegistry = new Map();
function wrapGenerateImages(api, generateImages) {
    return (model, context, options)=>{
        if (model.api !== api) {
            throw new Error(`Mismatched api: ${model.api} expected ${api}`);
        }
        return generateImages(model, context, options);
    };
}
export function registerImagesApiProvider(provider, sourceId) {
    imagesApiProviderRegistry.set(provider.api, {
        provider: {
            api: provider.api,
            generateImages: wrapGenerateImages(provider.api, provider.generateImages)
        },
        sourceId
    });
}
export function getImagesApiProvider(api) {
    return imagesApiProviderRegistry.get(api)?.provider;
}
