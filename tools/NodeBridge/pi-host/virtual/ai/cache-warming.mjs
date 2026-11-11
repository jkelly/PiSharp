// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/ai/src/cache-warming.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { calculateCost } from "./models.mjs";
import { getProviderEnvValue } from "./utils/provider-env.mjs";
export const CACHE_WARMING_MAX_AGE_MS = 60 * 60_000;
export const CACHE_WARMING_MAX_IDLE_AGE_MS = 30 * 60_000;
export const CACHE_WARMING_MINIMUM_EXPECTED_SAVINGS = 0.05;
export const CACHE_WARMING_IDLE_CONTINUATION_PROBABILITY = 0.15;
export function getCacheWarmingDelayMs(ttlMs) {
    if (ttlMs <= 10_000) return undefined;
    return Math.max(1, Math.floor(Math.min(ttlMs * 0.9, ttlMs - 10_000)));
}
export function getPromptCacheTtlMs(model, options) {
    const retention = options?.cacheRetention ?? (getProviderEnvValue("PI_CACHE_RETENTION", options?.env) === "long" ? "long" : "short");
    if (retention === "none") return undefined;
    const seconds = model.promptCache?.[retention];
    return seconds === undefined ? undefined : seconds * 1000;
}
export function isCacheWarmingReplayable(model, options) {
    if (!options?.reasoning || model.api !== "anthropic-messages") return true;
    return model.compat?.forceAdaptiveThinking === true;
}
export function getCacheWarmingRequestOptions(options, signal) {
    return {
        ...options,
        maxTokens: 1,
        disableContinuation: true,
        maxRetries: 0,
        signal
    };
}
export function evaluateCacheWarming(model, promptTokens, phase) {
    const cacheHitCost = price(model, {
        cacheRead: promptTokens
    });
    const cacheMissCost = price(model, model.cost.cacheWrite > 0 ? {
        cacheWrite: promptTokens
    } : {
        input: promptTokens
    });
    const warmCost = price(model, {
        cacheRead: promptTokens,
        output: 1
    });
    const missCost = Math.max(0, cacheMissCost - cacheHitCost);
    const continuationProbability = phase === "idle" ? CACHE_WARMING_IDLE_CONTINUATION_PROBABILITY : 1;
    const economicsAvailable = promptTokens > 0 && (cacheHitCost > 0 || cacheMissCost > 0);
    const expectedSavings = continuationProbability * missCost - warmCost;
    return {
        phase,
        warmCost,
        missCost,
        continuationProbability,
        expectedSavings,
        economicsAvailable,
        action: expectedSavings >= CACHE_WARMING_MINIMUM_EXPECTED_SAVINGS ? "warm" : "stop"
    };
}
function price(model, tokens) {
    const usage = {
        input: 0,
        output: 0,
        cacheRead: 0,
        cacheWrite: 0,
        totalTokens: 0,
        cost: {
            input: 0,
            output: 0,
            cacheRead: 0,
            cacheWrite: 0,
            total: 0
        },
        ...tokens
    };
    return calculateCost(model, usage).total;
}
