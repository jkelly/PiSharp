// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/modes/interactive/components/visual-truncate.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { Text, truncateToWidth } from "../../../../pi-tui.mjs";
export function truncateToVisualLines(text, maxVisualLines, width, paddingX = 0, keep = "end") {
    if (!text) {
        return {
            visualLines: [],
            skippedCount: 0
        };
    }
    const tempText = new Text(text, paddingX, 0);
    const allVisualLines = tempText.render(width);
    if (allVisualLines.length <= maxVisualLines) {
        return {
            visualLines: allVisualLines,
            skippedCount: 0
        };
    }
    const truncatedLines = keep === "start" ? allVisualLines.slice(0, maxVisualLines) : allVisualLines.slice(-maxVisualLines);
    const skippedCount = allVisualLines.length - maxVisualLines;
    return {
        visualLines: truncatedLines,
        skippedCount
    };
}
export class VisualLinePreview {
    options;
    cachedWidth;
    cachedLines;
    constructor(options){
        this.options = options;
    }
    render(width) {
        if (this.cachedLines === undefined || this.cachedWidth !== width) {
            const { text, maxVisualLines, keep, formatHint } = this.options;
            const preview = truncateToVisualLines(text, maxVisualLines, width, 0, keep);
            const lines = preview.visualLines;
            if (preview.skippedCount > 0) {
                const hint = truncateToWidth(formatHint(preview.skippedCount), width, "...");
                this.cachedLines = keep === "start" ? [
                    ...lines,
                    hint
                ] : [
                    hint,
                    ...lines
                ];
            } else {
                this.cachedLines = lines;
            }
            this.cachedWidth = width;
        }
        return this.cachedLines;
    }
    invalidate() {
        this.cachedWidth = undefined;
        this.cachedLines = undefined;
    }
}
