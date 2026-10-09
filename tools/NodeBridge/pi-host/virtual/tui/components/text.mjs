// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/components/text.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { applyBackgroundToLine, flattenLines, visibleWidth, wrapTextWithAnsi } from "../utils.mjs";
export class Text {
    text;
    paddingX;
    paddingY;
    customBgFn;
    cachedText;
    cachedWidth;
    cachedLines;
    constructor(text = "", paddingX = 1, paddingY = 1, customBgFn){
        this.text = text;
        this.paddingX = paddingX;
        this.paddingY = paddingY;
        this.customBgFn = customBgFn;
    }
    setText(text) {
        this.text = text;
        this.cachedText = undefined;
        this.cachedWidth = undefined;
        this.cachedLines = undefined;
    }
    setCustomBgFn(customBgFn) {
        this.customBgFn = customBgFn;
        this.cachedText = undefined;
        this.cachedWidth = undefined;
        this.cachedLines = undefined;
    }
    setPaddingX(paddingX) {
        this.paddingX = paddingX;
        this.invalidate();
    }
    invalidate() {
        this.cachedText = undefined;
        this.cachedWidth = undefined;
        this.cachedLines = undefined;
    }
    render(width) {
        if (this.cachedLines && this.cachedText === this.text && this.cachedWidth === width) {
            return this.cachedLines;
        }
        if (!this.text || this.text.trim() === "") {
            const result = [];
            this.cachedText = this.text;
            this.cachedWidth = width;
            this.cachedLines = result;
            return result;
        }
        const normalizedText = this.text.replace(/\t/g, "   ");
        const paddingX = Math.min(this.paddingX, Math.max(0, Math.floor((width - 1) / 2)));
        const contentWidth = Math.max(1, width - paddingX * 2);
        const wrappedLines = wrapTextWithAnsi(normalizedText, contentWidth);
        const leftMargin = " ".repeat(paddingX);
        const rightMargin = " ".repeat(paddingX);
        const contentLines = [];
        for (const line of wrappedLines){
            const lineWithMargins = leftMargin + line + rightMargin;
            if (this.customBgFn) {
                contentLines.push(applyBackgroundToLine(lineWithMargins, width, this.customBgFn));
            } else {
                const visibleLen = visibleWidth(lineWithMargins);
                const paddingNeeded = Math.max(0, width - visibleLen);
                contentLines.push(lineWithMargins + " ".repeat(paddingNeeded));
            }
        }
        const emptyLine = " ".repeat(width);
        const emptyLines = [];
        for(let i = 0; i < this.paddingY; i++){
            const line = this.customBgFn ? applyBackgroundToLine(emptyLine, width, this.customBgFn) : emptyLine;
            emptyLines.push(line);
        }
        const result = [
            ...emptyLines,
            ...contentLines,
            ...emptyLines
        ];
        flattenLines(result);
        this.cachedText = this.text;
        this.cachedWidth = width;
        this.cachedLines = result;
        return result.length > 0 ? result : [
            ""
        ];
    }
}
