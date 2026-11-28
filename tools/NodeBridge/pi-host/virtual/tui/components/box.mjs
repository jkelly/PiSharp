// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/tui/src/components/box.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { dispatchMouseToChildLines, renderChildLines } from "../tui.mjs";
import { flattenLines, visibleWidth } from "../utils.mjs";
export class Box {
    children = [];
    paddingX;
    paddingY;
    bgFn;
    cache;
    childLines;
    constructor(paddingX = 1, paddingY = 1, bgFn){
        this.paddingX = paddingX;
        this.paddingY = paddingY;
        this.bgFn = bgFn;
    }
    addChild(component) {
        this.children.push(component);
        this.invalidateCache();
    }
    removeChild(component) {
        const index = this.children.indexOf(component);
        if (index !== -1) {
            this.children.splice(index, 1);
            this.invalidateCache();
        }
    }
    clear() {
        this.children = [];
        this.invalidateCache();
    }
    setBgFn(bgFn) {
        this.bgFn = bgFn;
    }
    setPaddingX(paddingX) {
        this.paddingX = paddingX;
        this.invalidateCache();
    }
    invalidateCache() {
        this.cache = undefined;
        this.childLines = undefined;
    }
    invalidate() {
        this.invalidateCache();
        for (const child of this.children){
            child.invalidate?.();
        }
    }
    handleMouse(event) {
        const contentWidth = Math.max(1, event.width - this.paddingX * 2);
        const contentY = event.y - this.paddingY;
        const contentX = event.x - this.paddingX;
        if (contentY < 0 || contentX < 0 || contentX >= contentWidth) return undefined;
        const layout = this.childLines?.width === contentWidth ? this.childLines : renderChildLines(this.children, contentWidth, undefined);
        return dispatchMouseToChildLines(layout, {
            ...event,
            x: contentX,
            y: contentY,
            width: contentWidth
        });
    }
    render(width) {
        if (this.children.length === 0) {
            return [];
        }
        const contentWidth = Math.max(1, width - this.paddingX * 2);
        const leftPad = " ".repeat(this.paddingX);
        this.childLines = renderChildLines(this.children, contentWidth, this.childLines);
        const childLines = this.childLines.lines;
        if (childLines.length === 0) {
            return [];
        }
        const bgSample = this.bgFn ? this.bgFn("test") : undefined;
        const cache = this.cache;
        if (cache?.childLines === childLines && cache.width === width && cache.bgSample === bgSample) {
            return cache.lines;
        }
        const result = [];
        for(let i = 0; i < this.paddingY; i++){
            result.push(this.applyBg("", width));
        }
        for (const line of childLines){
            result.push(this.applyBg(leftPad + line, width));
        }
        for(let i = 0; i < this.paddingY; i++){
            result.push(this.applyBg("", width));
        }
        flattenLines(result);
        this.cache = {
            childLines,
            width,
            bgSample,
            lines: result
        };
        return result;
    }
    applyBg(line, width) {
        const visLen = visibleWidth(line);
        const padNeeded = Math.max(0, width - visLen);
        const padded = line + " ".repeat(padNeeded);
        return this.bgFn ? this.bgFn(padded) : padded;
    }
}
