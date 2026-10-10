// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/components/image.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { allocateImageId, getCapabilities, getCellDimensions, getImageDimensions, getPngDimensions, imageFallback, renderImage } from "../terminal-image.mjs";
import { truncateToWidth } from "../utils.mjs";
let imageTranscoder;
const pngCache = new Map();
export function setImageTranscoder(transcoder) {
    imageTranscoder = transcoder;
    pngCache.clear();
}
function toPng(base64Data, mimeType) {
    if (!imageTranscoder) return null;
    const cached = pngCache.get(base64Data);
    const png = cached === undefined ? imageTranscoder(base64Data, mimeType) : cached;
    pngCache.delete(base64Data);
    pngCache.set(base64Data, png);
    if (pngCache.size > 32) pngCache.delete(pngCache.keys().next().value);
    return png;
}
export class Image {
    base64Data;
    mimeType;
    dimensions;
    theme;
    options;
    imageId;
    pngData;
    cachedLines;
    cachedWidth;
    constructor(base64Data, mimeType, theme, options = {}, dimensions){
        this.base64Data = base64Data;
        this.mimeType = mimeType;
        this.theme = theme;
        this.options = options;
        this.dimensions = dimensions || getImageDimensions(base64Data, mimeType) || {
            widthPx: 800,
            heightPx: 600
        };
        this.imageId = options.imageId;
    }
    getImageId() {
        return this.imageId;
    }
    invalidate() {
        this.cachedLines = undefined;
        this.cachedWidth = undefined;
    }
    render(width) {
        if (this.cachedLines && this.cachedWidth === width) {
            return this.cachedLines;
        }
        const maxWidth = Math.max(1, Math.min(width - 2, this.options.maxWidthCells ?? 60));
        const cellDimensions = getCellDimensions();
        const defaultMaxHeight = Math.max(1, Math.ceil(maxWidth * cellDimensions.widthPx / cellDimensions.heightPx));
        const maxHeight = this.options.maxHeightCells ?? defaultMaxHeight;
        const caps = getCapabilities();
        let data = this.base64Data;
        let dimensions = this.dimensions;
        if (caps.images === "kitty" && this.mimeType !== "image/png") {
            this.pngData ??= toPng(this.base64Data, this.mimeType) ?? undefined;
            data = this.pngData ?? null;
            if (data) dimensions = getPngDimensions(data) ?? dimensions;
        }
        let lines;
        if (caps.images && data) {
            if (caps.images === "kitty" && this.imageId === undefined) {
                this.imageId = allocateImageId();
            }
            const result = renderImage(data, dimensions, {
                maxWidthCells: maxWidth,
                maxHeightCells: maxHeight,
                imageId: this.imageId,
                moveCursor: false
            });
            if (result) {
                if (result.imageId) {
                    this.imageId = result.imageId;
                }
                if (caps.images === "kitty") {
                    lines = [
                        result.sequence
                    ];
                    for(let i = 0; i < result.rows - 1; i++){
                        lines.push("");
                    }
                } else {
                    lines = [];
                    for(let i = 0; i < result.rows - 1; i++){
                        lines.push("");
                    }
                    const rowOffset = result.rows - 1;
                    const moveUp = rowOffset > 0 ? `\x1b[${rowOffset}A` : "";
                    lines.push(moveUp + result.sequence);
                }
            } else {
                const fallback = imageFallback(this.mimeType, this.dimensions, this.options.filename);
                lines = [
                    truncateToWidth(this.theme.fallbackColor(fallback), width)
                ];
            }
        } else {
            const fallback = imageFallback(this.mimeType, this.dimensions, this.options.filename);
            lines = [
                truncateToWidth(this.theme.fallbackColor(fallback), width)
            ];
        }
        this.cachedLines = lines;
        this.cachedWidth = width;
        return lines;
    }
}
