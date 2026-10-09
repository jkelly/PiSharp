// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/compat.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged except for the marked PiSharp adaptations).
export * from "./ai/api/anthropic-messages.lazy.mjs";
export * from "./ai/api/azure-openai-responses.lazy.mjs";
export * from "./ai/api/bedrock-converse-stream.lazy.mjs";
export * from "./ai/api/google-generative-ai.lazy.mjs";
export * from "./ai/api/google-vertex.lazy.mjs";
export * from "./ai/api/mistral-conversations.lazy.mjs";
export * from "./ai/api/openai-codex-responses.lazy.mjs";
export * from "./ai/api/openai-completions.lazy.mjs";
export * from "./ai/api/openai-responses.lazy.mjs";
export * from "./ai/api/pi-messages.lazy.mjs";
export * from "./ai/env-api-keys.mjs";
export * from "./ai-host/image-models.mjs";
export * from "./ai/images.mjs";
export * from "./ai/images-api-registry.mjs";
export * from "./ai/index.mjs";
export * from "./ai/legacy-api-aliases.mjs";
export * from "./ai/providers/images/register-builtins.mjs";
import { anthropicMessagesApi } from "./ai/api/anthropic-messages.lazy.mjs";
import { azureOpenAIResponsesApi } from "./ai/api/azure-openai-responses.lazy.mjs";
import { bedrockConverseStreamApi } from "./ai/api/bedrock-converse-stream.lazy.mjs";
import { googleGenerativeAIApi } from "./ai/api/google-generative-ai.lazy.mjs";
import { googleVertexApi } from "./ai/api/google-vertex.lazy.mjs";
import { mistralConversationsApi } from "./ai/api/mistral-conversations.lazy.mjs";
import { openAICodexResponsesApi } from "./ai/api/openai-codex-responses.lazy.mjs";
import { openAICompletionsApi } from "./ai/api/openai-completions.lazy.mjs";
import { openAIResponsesApi } from "./ai/api/openai-responses.lazy.mjs";
import { piMessagesApi } from "./ai/api/pi-messages.lazy.mjs";
import { getEnvApiKey } from "./ai/env-api-keys.mjs";
import { builtinModels, getBuiltinModel, getBuiltinModels, getBuiltinProviders } from "./pi-ai-providers.mjs";
import { createFauxCore } from "./ai/providers/faux.mjs";
import { normalizeContext } from "./ai/utils/transcript.mjs";
export const getModel = __pisharpHostFunction("getModel");
export const getModels = __pisharpHostFunction("getModels");
export const getProviders = __pisharpHostFunction("getProviders");
const apiProviderRegistry = new Map();
function wrapStream(api, stream) {
    return (model, context, options)=>{
        if (model.api !== api) {
            throw new Error(`Mismatched api: ${model.api} expected ${api}`);
        }
        return stream(model, context, options);
    };
}
function wrapStreamSimple(api, streamSimple) {
    return (model, context, options)=>{
        if (model.api !== api) {
            throw new Error(`Mismatched api: ${model.api} expected ${api}`);
        }
        return streamSimple(model, context, options);
    };
}
export function registerApiProvider(provider, sourceId) {
    __pisharpNotifyHost("registerApiProvider", provider, sourceId);
    apiProviderRegistry.set(provider.api, {
        provider: {
            api: provider.api,
            stream: wrapStream(provider.api, provider.stream),
            streamSimple: wrapStreamSimple(provider.api, provider.streamSimple)
        },
        sourceId
    });
}
export function getApiProvider(api) {
    return apiProviderRegistry.get(api)?.provider;
}
export function getApiProviders() {
    return Array.from(apiProviderRegistry.values(), (entry)=>entry.provider);
}
export function unregisterApiProviders(sourceId) {
    __pisharpNotifyHost("unregisterApiProviders", sourceId);
    for (const [api, entry] of apiProviderRegistry.entries()){
        if (entry.sourceId === sourceId) {
            apiProviderRegistry.delete(api);
        }
    }
}
function clearApiProviders() {
    apiProviderRegistry.clear();
}
export function registerFauxProvider(options = {}) {
    const core = createFauxCore(options);
    const sourceId = `faux-provider-${Math.random().toString(36).slice(2, 10)}`;
    registerApiProvider({
        api: core.api,
        stream: core.stream,
        streamSimple: core.streamSimple
    }, sourceId);
    return {
        api: core.api,
        models: core.models,
        getModel: core.getModel,
        state: core.state,
        setResponses: core.setResponses,
        appendResponses: core.appendResponses,
        getPendingResponseCount: core.getPendingResponseCount,
        unregister () {
            unregisterApiProviders(sourceId);
        }
    };
}
const BUILTIN_APIS = [
    [
        "anthropic-messages",
        anthropicMessagesApi()
    ],
    [
        "openai-completions",
        openAICompletionsApi()
    ],
    [
        "openai-responses",
        openAIResponsesApi()
    ],
    [
        "openai-codex-responses",
        openAICodexResponsesApi()
    ],
    [
        "azure-openai-responses",
        azureOpenAIResponsesApi()
    ],
    [
        "google-generative-ai",
        googleGenerativeAIApi()
    ],
    [
        "google-vertex",
        googleVertexApi()
    ],
    [
        "mistral-conversations",
        mistralConversationsApi()
    ],
    [
        "bedrock-converse-stream",
        bedrockConverseStreamApi()
    ],
    [
        "pi-messages",
        piMessagesApi()
    ]
];
const builtinApiProviderInstances = new Map();
export function registerBuiltInApiProviders() {
    for (const [api, streams] of BUILTIN_APIS){
        if (!getApiProvider(api)) {
            registerApiProvider({
                api,
                stream: streams.stream,
                streamSimple: streams.streamSimple
            });
        }
        builtinApiProviderInstances.set(api, getApiProvider(api));
    }
}
export function resetApiProviders() {
    clearApiProviders();
    builtinApiProviderInstances.clear();
    registerBuiltInApiProviders();
}
registerBuiltInApiProviders();
const AMBIENT_AUTH_MARKER = "<authenticated>";
function hasExplicitApiKey(apiKey) {
    return typeof apiKey === "string" && apiKey.trim().length > 0;
}
function withEnvApiKey(model, options) {
    if (hasExplicitApiKey(options?.apiKey)) return options;
    const apiKey = getEnvApiKey(model.provider, options?.env);
    if (!apiKey || apiKey === AMBIENT_AUTH_MARKER) return options;
    return {
        ...options,
        apiKey
    };
}
function hasResolvedCloudflareAuth(options) {
    return hasExplicitApiKey(options?.apiKey) || typeof options?.headers?.["cf-aig-authorization"] === "string";
}
function getBuiltinProviderForModel(_model) {
    return undefined;
}
function resolveApiProvider(api) {
    const provider = getApiProvider(api);
    if (!provider) {
        throw new Error(`No API provider registered for api: ${api}`);
    }
    return provider;
}
export function stream(model, context, options) {
    const transcript = normalizeContext(context);
    const builtinProvider = getBuiltinProviderForModel(model);
    if (builtinProvider) {
        if (model.provider.startsWith("cloudflare-") && !hasResolvedCloudflareAuth(options)) {
            return compatModels.stream(model, transcript, options);
        }
        return builtinProvider.stream(model, transcript, withEnvApiKey(model, options));
    }
    const provider = resolveApiProvider(model.api);
    __pisharpRequireHost("stream", model.api);
    return provider.stream(model, transcript, withEnvApiKey(model, options));
}
export async function complete(model, context, options) {
    __pisharpRequireHost("complete", model.api);
    const s = stream(model, context, options);
    return s.result();
}
export function streamSimple(model, context, options) {
    const transcript = normalizeContext(context);
    const builtinProvider = getBuiltinProviderForModel(model);
    if (builtinProvider) {
        if (model.provider.startsWith("cloudflare-") && !hasResolvedCloudflareAuth(options)) {
            return compatModels.streamSimple(model, transcript, options);
        }
        return builtinProvider.streamSimple(model, transcript, withEnvApiKey(model, options));
    }
    const provider = resolveApiProvider(model.api);
    __pisharpRequireHost("streamSimple", model.api);
    return provider.streamSimple(model, transcript, withEnvApiKey(model, options));
}
export async function completeSimple(model, context, options) {
    __pisharpRequireHost("completeSimple", model.api);
    const s = streamSimple(model, context, options);
    return s.result();
}

// ---------------------------------------------------------------------------------------------
// PiSharp adaptations: builtin API providers are host-backed. Calling stream/complete for them without
// the host bridge hook throws "<name> is not available in the PiSharp Node bridge".
import { getBridge as __pisharpGetBridge, hostFunction as __pisharpHostFunction, unavailable as __pisharpUnavailable } from "./bridge.mjs";
function __pisharpRequireHost(name, api) {
	const provider = getApiProvider(api);
	if (provider && provider === builtinApiProviderInstances.get(api) && !__pisharpGetBridge()) throw __pisharpUnavailable(name);
}
// Upstream's provider composer falls back to this registry for the agent's own model streams, so
// extension registrations are mirrored to the PiSharp host (the builtin host-backed APIs are not).
function __pisharpNotifyHost(name, ...args) {
	const bridge = __pisharpGetBridge();
	if (!bridge) return;
	if (name === "registerApiProvider" && BUILTIN_APIS.some(([, streams]) => streams.stream === args[0].stream)) return;
	bridge.call(name, ...args);
}
