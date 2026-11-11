// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/tui/src/components/cancellable-loader.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { getKeybindings } from "../keybindings.mjs";
import { Loader } from "./loader.mjs";
export class CancellableLoader extends Loader {
    abortController = new AbortController();
    onAbort;
    get signal() {
        return this.abortController.signal;
    }
    get aborted() {
        return this.abortController.signal.aborted;
    }
    handleInput(data) {
        const kb = getKeybindings();
        if (kb.matches(data, "tui.select.cancel")) {
            this.abortController.abort();
            this.onAbort?.();
        }
    }
    dispose() {
        this.stop();
    }
}
