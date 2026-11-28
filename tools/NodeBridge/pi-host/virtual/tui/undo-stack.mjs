// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/tui/src/undo-stack.ts.
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
