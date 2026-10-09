// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/components/h-stack.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { compositeTuiLine } from "../tui.mjs";
import { visibleWidth } from "../utils.mjs";
import { allocateStackSizes, Stack, visibleStackEntries } from "./stack.mjs";
export class HStack extends Stack {
    layoutType = "hstack";
    constructor(children = [], options = {}){
        super(children, options);
    }
    render(width) {
        const safeWidth = Math.max(1, width);
        const viewport = {
            width: safeWidth,
            height: Number.MAX_SAFE_INTEGER
        };
        const entries = visibleStackEntries(this.entries, viewport);
        if (entries.length === 0) return [];
        const intrinsicWidths = entries.map((entry)=>{
            const lines = entry.component.render(safeWidth);
            return lines.reduce((max, line)=>Math.max(max, visibleWidth(line)), 0);
        });
        const widths = allocateStackSizes(entries, intrinsicWidths, safeWidth, this.gap);
        const rendered = entries.map((entry, index)=>widths[index] === 0 ? [] : entry.component.render(widths[index]));
        const height = rendered.reduce((max, lines)=>Math.max(max, lines.length), 0);
        const result = Array.from({
            length: height
        }, ()=>"");
        let x = 0;
        for(let index = 0; index < rendered.length; index++){
            const lines = rendered[index];
            const childWidth = widths[index];
            let offset = 0;
            if (this.align === "center") offset = Math.floor((height - lines.length) / 2);
            else if (this.align === "end") offset = height - lines.length;
            for(let row = 0; row < lines.length; row++){
                const target = row + offset;
                if (target < 0 || target >= result.length) continue;
                result[target] = compositeTuiLine(result[target], lines[row], x, childWidth, safeWidth);
            }
            x += childWidth + this.gap;
        }
        return result;
    }
}
