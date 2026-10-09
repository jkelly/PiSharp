// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/components/v-stack.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { allocateStackSizes, Stack, visibleStackEntries } from "./stack.mjs";
export class VStack extends Stack {
    layoutType = "vstack";
    constructor(children = [], options = {}){
        super(children, options);
    }
    render(width) {
        const viewport = {
            width: Math.max(1, width),
            height: Number.MAX_SAFE_INTEGER
        };
        const entries = visibleStackEntries(this.entries, viewport);
        const rendered = entries.map((entry)=>entry.component.render(viewport.width));
        const sizes = allocateStackSizes(entries, rendered.map((lines)=>lines.length), undefined, this.gap);
        const lines = [];
        for(let index = 0; index < entries.length; index++){
            if (index > 0) {
                for(let gap = 0; gap < this.gap; gap++)lines.push("");
            }
            const childLines = rendered[index].slice(0, sizes[index]);
            lines.push(...childLines);
            for(let padding = childLines.length; padding < sizes[index]; padding++)lines.push("");
        }
        return lines;
    }
}
