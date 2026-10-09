// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/virtual-models.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { isModelType, lazyStream } from "../../pi-ai.mjs";
export const VIRTUAL_MODEL_API = "pi-virtual";
export const VIRTUAL_MODEL_STATE_ENTRY = "pi.virtual-model-state";
const THINKING_LEVELS = [
    "off",
    "minimal",
    "low",
    "medium",
    "high",
    "xhigh",
    "max"
];
export function isVirtualModel(model) {
    return model.api === VIRTUAL_MODEL_API;
}
export function findLatestResponse(messages) {
    for(let i = messages.length - 1; i >= 0; i--){
        const message = messages[i];
        if (message.role === "assistant" && message.stopReason !== "error" && message.stopReason !== "aborted") {
            return message;
        }
    }
    return undefined;
}
export function getBranchSelection(branch, getModel) {
    for(let i = branch.length - 1; i >= 0; i--){
        const entry = branch[i];
        if (entry.type === "model_change") {
            return {
                provider: entry.provider,
                modelId: entry.modelId
            };
        }
        if (entry.type === "message" && entry.message.role === "assistant" && !isVirtualModel(entry.message)) {
            const response = {
                provider: entry.message.provider,
                modelId: entry.message.model
            };
            const change = findLastModelChange(branch, i);
            const model = change && getModel(change.provider, change.modelId);
            return change && model && isVirtualModel(model) ? change : response;
        }
    }
    return undefined;
}
function findLastModelChange(branch, before) {
    for(let i = before - 1; i >= 0; i--){
        const entry = branch[i];
        if (entry.type === "model_change") return {
            provider: entry.provider,
            modelId: entry.modelId
        };
    }
    return undefined;
}
export function getVirtualModelState(branch, provider, modelId) {
    for(let i = branch.length - 1; i >= 0; i--){
        const entry = branch[i];
        if (entry.type !== "custom" || entry.customType !== VIRTUAL_MODEL_STATE_ENTRY) continue;
        const data = entry.data;
        if (data?.provider === provider && data.modelId === modelId) return data.state;
    }
    return undefined;
}
export function createVirtualModel(definition) {
    const levels = definition.thinkingLevels ?? [
        "off"
    ];
    const thinkingLevelMap = {};
    for (const level of THINKING_LEVELS)thinkingLevelMap[level] = levels.includes(level) ? level : null;
    return {
        id: definition.id,
        name: definition.name,
        api: VIRTUAL_MODEL_API,
        provider: definition.provider,
        baseUrl: "",
        reasoning: levels.some((level)=>level !== "off"),
        thinkingLevelMap,
        input: definition.input ?? [
            "text",
            "image"
        ],
        cost: {
            input: 0,
            output: 0,
            cacheRead: 0,
            cacheWrite: 0
        },
        contextWindow: definition.contextWindow ?? 0,
        maxTokens: definition.maxTokens ?? 0
    };
}
function unroutedStream(model) {
    return lazyStream(model, async ()=>{
        throw new Error(`Virtual model ${model.provider}/${model.id} must be routed before streaming`);
    });
}
export function withVirtualModels(providerId, provider, virtualModels) {
    if (!provider) {
        return {
            id: providerId,
            name: providerId,
            auth: {
                apiKey: {
                    name: "Virtual model",
                    resolve: async ()=>({
                            auth: {},
                            source: "virtual"
                        })
                }
            },
            getModels: ()=>virtualModels,
            stream: unroutedStream,
            streamSimple: unroutedStream
        };
    }
    const ids = new Set(virtualModels.map((model)=>model.id));
    const physical = (models)=>models.filter((model)=>!isVirtualModel(model) && !(isModelType(model, "chat") && ids.has(model.id)));
    const virtual = (models)=>models.filter((model)=>isVirtualModel(model));
    const { filterModels, filterAllModels } = provider;
    return {
        ...provider,
        getModels: ()=>[
                ...physical(provider.getModels()),
                ...virtualModels
            ],
        getAllModels: ()=>[
                ...physical(provider.getAllModels?.() ?? provider.getModels()),
                ...virtualModels
            ],
        filterModels: (models, credential)=>{
            const real = physical(models);
            return [
                ...filterModels?.(real, credential) ?? real,
                ...virtual(models)
            ];
        },
        filterAllModels: filterAllModels && ((models, credential)=>[
                ...filterAllModels(physical(models), credential),
                ...virtual(models)
            ]),
        stream: (model, context, options)=>isVirtualModel(model) ? unroutedStream(model) : provider.stream(model, context, options),
        streamSimple: (model, context, options)=>isVirtualModel(model) ? unroutedStream(model) : provider.streamSimple(model, context, options)
    };
}
