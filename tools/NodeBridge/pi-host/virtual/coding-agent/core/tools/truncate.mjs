// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/core/tools/truncate.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export const DEFAULT_MAX_LINES = 2000;
export const DEFAULT_MAX_BYTES = 50 * 1024;
export const GREP_MAX_LINE_LENGTH = 500;
function splitLinesForCounting(content) {
    if (content.length === 0) {
        return [];
    }
    const lines = content.split("\n");
    if (content.endsWith("\n")) {
        lines.pop();
    }
    return lines;
}
export function formatSize(bytes) {
    if (bytes < 1024) {
        return `${bytes}B`;
    } else if (bytes < 1024 * 1024) {
        return `${(bytes / 1024).toFixed(1)}KB`;
    } else {
        return `${(bytes / (1024 * 1024)).toFixed(1)}MB`;
    }
}
export function truncateHead(content, options = {}) {
    const maxLines = options.maxLines ?? DEFAULT_MAX_LINES;
    const maxBytes = options.maxBytes ?? DEFAULT_MAX_BYTES;
    const totalBytes = Buffer.byteLength(content, "utf-8");
    const lines = splitLinesForCounting(content);
    const totalLines = lines.length;
    if (totalLines <= maxLines && totalBytes <= maxBytes) {
        return {
            content,
            truncated: false,
            truncatedBy: null,
            totalLines,
            totalBytes,
            outputLines: totalLines,
            outputBytes: totalBytes,
            lastLinePartial: false,
            firstLineExceedsLimit: false,
            maxLines,
            maxBytes
        };
    }
    const firstLineBytes = Buffer.byteLength(lines[0], "utf-8");
    if (firstLineBytes > maxBytes) {
        return {
            content: "",
            truncated: true,
            truncatedBy: "bytes",
            totalLines,
            totalBytes,
            outputLines: 0,
            outputBytes: 0,
            lastLinePartial: false,
            firstLineExceedsLimit: true,
            maxLines,
            maxBytes
        };
    }
    const outputLinesArr = [];
    let outputBytesCount = 0;
    let truncatedBy = "lines";
    for(let i = 0; i < lines.length && i < maxLines; i++){
        const line = lines[i];
        const lineBytes = Buffer.byteLength(line, "utf-8") + (i > 0 ? 1 : 0);
        if (outputBytesCount + lineBytes > maxBytes) {
            truncatedBy = "bytes";
            break;
        }
        outputLinesArr.push(line);
        outputBytesCount += lineBytes;
    }
    if (outputLinesArr.length >= maxLines && outputBytesCount <= maxBytes) {
        truncatedBy = "lines";
    }
    const outputContent = outputLinesArr.join("\n");
    const finalOutputBytes = Buffer.byteLength(outputContent, "utf-8");
    return {
        content: outputContent,
        truncated: true,
        truncatedBy,
        totalLines,
        totalBytes,
        outputLines: outputLinesArr.length,
        outputBytes: finalOutputBytes,
        lastLinePartial: false,
        firstLineExceedsLimit: false,
        maxLines,
        maxBytes
    };
}
export function truncateTail(content, options = {}) {
    const maxLines = options.maxLines ?? DEFAULT_MAX_LINES;
    const maxBytes = options.maxBytes ?? DEFAULT_MAX_BYTES;
    const totalBytes = Buffer.byteLength(content, "utf-8");
    const lines = splitLinesForCounting(content);
    const totalLines = lines.length;
    if (totalLines <= maxLines && totalBytes <= maxBytes) {
        return {
            content,
            truncated: false,
            truncatedBy: null,
            totalLines,
            totalBytes,
            outputLines: totalLines,
            outputBytes: totalBytes,
            lastLinePartial: false,
            firstLineExceedsLimit: false,
            maxLines,
            maxBytes
        };
    }
    const outputLinesArr = [];
    let outputBytesCount = 0;
    let truncatedBy = "lines";
    let lastLinePartial = false;
    for(let i = lines.length - 1; i >= 0 && outputLinesArr.length < maxLines; i--){
        const line = lines[i];
        const lineBytes = Buffer.byteLength(line, "utf-8") + (outputLinesArr.length > 0 ? 1 : 0);
        if (outputBytesCount + lineBytes > maxBytes) {
            truncatedBy = "bytes";
            if (outputLinesArr.length === 0) {
                const truncatedLine = truncateStringToBytesFromEnd(line, maxBytes);
                outputLinesArr.unshift(truncatedLine);
                outputBytesCount = Buffer.byteLength(truncatedLine, "utf-8");
                lastLinePartial = true;
            }
            break;
        }
        outputLinesArr.unshift(line);
        outputBytesCount += lineBytes;
    }
    if (outputLinesArr.length >= maxLines && outputBytesCount <= maxBytes) {
        truncatedBy = "lines";
    }
    const outputContent = outputLinesArr.join("\n");
    const finalOutputBytes = Buffer.byteLength(outputContent, "utf-8");
    return {
        content: outputContent,
        truncated: true,
        truncatedBy,
        totalLines,
        totalBytes,
        outputLines: outputLinesArr.length,
        outputBytes: finalOutputBytes,
        lastLinePartial,
        firstLineExceedsLimit: false,
        maxLines,
        maxBytes
    };
}
function truncateStringToBytesFromEnd(str, maxBytes) {
    const buf = Buffer.from(str, "utf-8");
    if (buf.length <= maxBytes) {
        return str;
    }
    let start = buf.length - maxBytes;
    while(start < buf.length && (buf[start] & 0xc0) === 0x80){
        start++;
    }
    return buf.slice(start).toString("utf-8");
}
export function truncateLine(line, maxChars = GREP_MAX_LINE_LENGTH) {
    if (line.length <= maxChars) {
        return {
            text: line,
            wasTruncated: false
        };
    }
    return {
        text: `${sliceHead(line, maxChars)}... [truncated]`,
        wasTruncated: true
    };
}
export function sliceHead(text, maxUnits) {
    const end = Math.max(0, maxUnits);
    return isHighSurrogate(text.charCodeAt(end - 1)) && isLowSurrogate(text.charCodeAt(end)) ? text.slice(0, end - 1) : text.slice(0, end);
}
export function sliceTail(text, maxUnits) {
    if (maxUnits <= 0) return "";
    const start = Math.max(0, text.length - maxUnits);
    return isLowSurrogate(text.charCodeAt(start)) && isHighSurrogate(text.charCodeAt(start - 1)) ? text.slice(start + 1) : text.slice(start);
}
function isHighSurrogate(code) {
    return code >= 0xd800 && code <= 0xdbff;
}
function isLowSurrogate(code) {
    return code >= 0xdc00 && code <= 0xdfff;
}
