// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/tui/src/undo-stack.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export class UndoStack {
    stack = [];
    push(state) {
        this.stack.push(structuredClone(state));
    }
    pop() {
        return this.stack.pop();
    }
    clear() {
        this.stack.length = 0;
    }
    get length() {
        return this.stack.length;
    }
}
