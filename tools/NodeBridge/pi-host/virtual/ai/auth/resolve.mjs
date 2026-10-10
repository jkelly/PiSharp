// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/resolve.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { operationSignal, raceWithAbortSignal } from "../utils/abort.mjs";
import { ModelsError } from "../utils/models-error.mjs";
export { ModelsError } from "../utils/models-error.mjs";
export function resolveProviderAuth(provider, credentials, authContext, overrides) {
    const signal = operationSignal(overrides?.signal);
    return raceWithAbortSignal(resolveProviderAuthWithSignal(provider, credentials, authContext, overrides, signal), signal);
}
async function resolveProviderAuthWithSignal(provider, credentials, authContext, overrides, signal) {
    signal.throwIfAborted();
    const requestAuthContext = overrides?.env ? overlayEnvAuthContext(authContext, overrides.env) : authContext;
    if (overrides?.apiKey !== undefined && provider.auth.apiKey) {
        return resolveApiKey(requestAuthContext, provider.auth.apiKey, provider.id, {
            type: "api_key",
            key: overrides.apiKey,
            env: overrides.env
        }, signal);
    }
    const stored = await readCredential(credentials, provider.id, signal);
    if (stored) {
        if (stored.type === "oauth" && provider.auth.oauth) {
            return resolveStoredOAuth(credentials, provider.id, provider.auth.oauth, stored, signal, overrides?.minOAuthValidityMs);
        }
        if (stored.type === "api_key" && provider.auth.apiKey) {
            const credential = overrides?.env ? {
                ...stored,
                env: {
                    ...stored.env,
                    ...overrides.env
                }
            } : stored;
            return resolveApiKey(requestAuthContext, provider.auth.apiKey, provider.id, credential, signal);
        }
        return undefined;
    }
    return provider.auth.apiKey ? resolveApiKey(requestAuthContext, provider.auth.apiKey, provider.id, undefined, signal) : undefined;
}
function overlayEnvAuthContext(base, env) {
    return {
        env: async (name)=>env[name] || await base.env(name),
        fileExists: (path)=>base.fileExists(path)
    };
}
const DEFAULT_OAUTH_MINIMUM_VALIDITY_MS = 5 * 60 * 1000;
const DEFAULT_OAUTH_REFRESH_TIMEOUT_MS = 15_000;
export async function refreshStoredOAuthCredential(credentials, providerId, oauth, needsRefresh, signal) {
    let post;
    const lockWait = new AbortController();
    const cancelLockWait = ()=>lockWait.abort(signal.reason);
    signal.addEventListener("abort", cancelLockWait, {
        once: true
    });
    if (signal.aborted) cancelLockWait();
    try {
        post = await credentials.modify(providerId, async (current)=>{
            signal.removeEventListener("abort", cancelLockWait);
            signal.throwIfAborted();
            if (current?.type !== "oauth") return undefined;
            if (!needsRefresh(current)) return undefined;
            try {
                return await oauth.refresh(current, AbortSignal.timeout(DEFAULT_OAUTH_REFRESH_TIMEOUT_MS));
            } catch (error) {
                throw new ModelsError("oauth", `OAuth refresh failed for ${providerId}`, {
                    cause: error
                });
            }
        }, {
            signal: lockWait.signal
        });
    } catch (error) {
        if (error instanceof ModelsError) throw error;
        signal.throwIfAborted();
        throw new ModelsError("auth", `Credential store modify failed for ${providerId}`, {
            cause: error
        });
    } finally{
        signal.removeEventListener("abort", cancelLockWait);
    }
    return post?.type === "oauth" ? post : undefined;
}
async function resolveStoredOAuth(credentials, providerId, oauth, stored, signal, minOAuthValidityMs) {
    const minimumValidityMs = Math.max(DEFAULT_OAUTH_MINIMUM_VALIDITY_MS, minOAuthValidityMs ?? 0);
    const expiresSoon = (credential)=>Date.now() + minimumValidityMs >= credential.expires;
    let credential = stored;
    if (expiresSoon(credential)) {
        const post = await refreshStoredOAuthCredential(credentials, providerId, oauth, expiresSoon, signal);
        if (!post) return undefined;
        credential = post;
        if (minOAuthValidityMs !== undefined && expiresSoon(credential)) {
            throw new ModelsError("oauth", `OAuth refresh returned a token that expires too soon for ${providerId}`);
        }
    }
    try {
        return {
            auth: await oauth.toAuth(credential),
            source: "OAuth"
        };
    } catch (error) {
        throw new ModelsError("oauth", `OAuth auth derivation failed for ${providerId}`, {
            cause: error
        });
    }
}
async function resolveApiKey(authContext, apiKey, providerId, credential, signal) {
    try {
        return await apiKey.resolve({
            ctx: authContext,
            credential,
            signal
        });
    } catch (error) {
        throw new ModelsError("auth", `API key auth failed for provider ${providerId}`, {
            cause: error
        });
    }
}
async function readCredential(credentials, providerId, signal) {
    try {
        return await credentials.read(providerId, {
            signal
        });
    } catch (error) {
        throw new ModelsError("auth", `Credential store read failed for ${providerId}`, {
            cause: error
        });
    }
}
