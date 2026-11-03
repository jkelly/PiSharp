// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/session-resources.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
const sessionResourceCleanups = new Set();
export function registerSessionResourceCleanup(cleanup) {
    sessionResourceCleanups.add(cleanup);
    return ()=>{
        sessionResourceCleanups.delete(cleanup);
    };
}
export function cleanupSessionResources(sessionId) {
    const errors = [];
    for (const cleanup of sessionResourceCleanups){
        try {
            cleanup(sessionId);
        } catch (error) {
            errors.push(error);
        }
    }
    if (errors.length > 0) {
        throw new AggregateError(errors, "Failed to cleanup session resources");
    }
}
