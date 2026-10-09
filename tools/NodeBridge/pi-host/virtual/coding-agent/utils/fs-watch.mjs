// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/fs-watch.ts.
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
