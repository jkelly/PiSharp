// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/dynamic-border.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { theme } from "../../../../theme.mjs";
export class DynamicBorder {
    color;
    constructor(color = (str)=>theme.fg("border", str)){
        this.color = color;
    }
    invalidate() {}
    render(width) {
        return [
            this.color("─".repeat(Math.max(1, width)))
        ];
    }
}
