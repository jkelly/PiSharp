// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/tui/src/components/mouse-region.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { dispatchMouseEvent } from "../tui.mjs";
export class MouseRegion {
    child;
    onMouse;
    constructor(child, onMouse){
        this.child = child;
        this.onMouse = onMouse;
    }
    render(width) {
        return this.child.render(width);
    }
    handleMouse(event) {
        const childResult = dispatchMouseEvent(this.child, event);
        return childResult ?? this.onMouse(event);
    }
    invalidate() {
        this.child.invalidate();
    }
}
