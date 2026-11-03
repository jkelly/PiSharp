// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/retry.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
function buildProviderErrorPattern(patterns) {
    return new RegExp(patterns.join("|"), "i");
}
const NON_RETRYABLE_PROVIDER_LIMIT_ERROR_PATTERN = buildProviderErrorPattern([
    "GoUsageLimitError",
    "FreeUsageLimitError",
    "Monthly usage limit reached",
    "available balance",
    "insufficient_quota",
    "out of budget",
    "quota exceeded",
    "billing",
    "subscription_sharing_usage_limit_exceeded",
    "subscription_sharing_workplace_individual_usage_limit_exceeded",
    '"code"\\s*:\\s*"?1308(?![0-9])'
]);
const RETRYABLE_PROVIDER_ERROR_PATTERN = buildProviderErrorPattern([
    "overloaded",
    "server_busy",
    "servers are currently busy",
    "currently experiencing high demand",
    "model is at capacity",
    "rate.?limit",
    "too many requests",
    "(?<![a-z0-9])(?:429|500|502|503|504|520|524)(?![a-z0-9])",
    "service.?(?:is.?)?(?:temporarily.?)?unavailable",
    "server.?error",
    "internal.?error",
    "provider.?returned.?(?:an.?)?error",
    "exceeded request buffer limit while retrying upstream",
    "unable to forward upstream",
    "upstream.?request.?failed",
    "upstream_stream_read_error",
    "stream was interrupted",
    "http/?2 stream failed",
    "network.?error",
    "connection.?error",
    "connection.?refused",
    "connection.?lost",
    "other side closed",
    "fetch failed",
    "request to \\S+ failed, reason:",
    "getaddrinfo",
    "ENOTFOUND",
    "EAI_AGAIN",
    "upstream.?connect",
    "reset before headers",
    "socket hang up",
    "socket connection was closed",
    "timed? out",
    "timeout",
    "terminated",
    "websocket.?closed",
    "websocket.?error",
    "ended without",
    "stream ended before message_stop",
    "stream ended before a terminal response event",
    "http2 request did not get a response",
    "stream disconnected before completion",
    "pending stream has been canceled",
    "retry delay",
    "you can retry your request",
    "try your request again",
    "please retry your request",
    "ResourceExhausted",
    "subscription_sharing_usage_unavailable",
    "subscription_sharing_user_unavailable"
]);
export const DEFAULT_MAX_AGENT_RETRY_DELAY_MS = 60_000;
export function retryDelayMs(policy, attempt) {
    const delay = policy.baseDelayMs * 2 ** Math.max(0, attempt - 1);
    const safeDelay = Number.isSafeInteger(delay) ? delay : Number.MAX_SAFE_INTEGER;
    return Math.min(safeDelay, policy.maxAgentDelayMs ?? DEFAULT_MAX_AGENT_RETRY_DELAY_MS);
}
class RetrySleepAbortError extends Error {
    constructor(){
        super("Aborted");
    }
}
function sleep(ms, signal) {
    return new Promise((resolve, reject)=>{
        if (signal?.aborted) {
            reject(new RetrySleepAbortError());
            return;
        }
        const timeout = setTimeout(resolve, ms);
        signal?.addEventListener("abort", ()=>{
            clearTimeout(timeout);
            reject(new RetrySleepAbortError());
        }, {
            once: true
        });
    });
}
export async function retryAssistantCall(produce, policy, signal, callbacks) {
    const maxAttempts = policy?.enabled ? policy.maxRetries : 0;
    let attempt = 0;
    let lastRetry;
    for(;;){
        const response = await produce();
        if (response.stopReason === "aborted") {
            if (lastRetry) await callbacks?.onRetryFinished?.(false, lastRetry.attempt);
            return response;
        }
        if (response.stopReason !== "error") {
            if (lastRetry) await callbacks?.onRetryFinished?.(true, lastRetry.attempt);
            return response;
        }
        if (attempt >= maxAttempts || !isRetryableAssistantError(response)) {
            if (lastRetry) await callbacks?.onRetryFinished?.(false, lastRetry.attempt, response.errorMessage);
            return response;
        }
        attempt++;
        lastRetry = {
            attempt,
            errorMessage: response.errorMessage || "Unknown error"
        };
        const delayMs = retryDelayMs(policy, attempt);
        await callbacks?.onRetryScheduled?.(attempt, maxAttempts, delayMs, lastRetry.errorMessage);
        try {
            await sleep(delayMs, signal);
        } catch (error) {
            await callbacks?.onRetryFinished?.(false, attempt, lastRetry.errorMessage);
            if (error instanceof RetrySleepAbortError) {
                const { errorMessage: _errorMessage, ...rest } = response;
                return {
                    ...rest,
                    stopReason: "aborted"
                };
            }
            throw error;
        }
        await callbacks?.onRetryAttemptStart?.();
    }
}
export function isRetryableAssistantError(message) {
    if (message.stopReason !== "error" || !message.errorMessage) return false;
    const errorMessage = message.errorMessage;
    if (NON_RETRYABLE_PROVIDER_LIMIT_ERROR_PATTERN.test(errorMessage)) return false;
    return RETRYABLE_PROVIDER_ERROR_PATTERN.test(errorMessage);
}
