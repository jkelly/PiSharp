// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/coding-agent/src/utils/fs-watch.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { watch } from "node:fs";
export const FS_WATCH_RETRY_DELAY_MS = 5000;
export function closeWatcher(watcher) {
    if (!watcher) {
        return;
    }
    try {
        watcher.close();
    } catch  {}
}
export function watchWithErrorHandler(path, listener, onError) {
    try {
        const watcher = watch(path, listener);
        watcher.on("error", onError);
        return watcher;
    } catch  {
        onError();
        return null;
    }
}
