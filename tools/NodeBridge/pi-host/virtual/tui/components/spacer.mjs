// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/tui/src/components/spacer.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export class Spacer {
    lines;
    constructor(lines = 1){
        this.lines = lines;
    }
    setLines(lines) {
        this.lines = lines;
    }
    invalidate() {}
    render(_width) {
        const result = [];
        for(let i = 0; i < this.lines; i++){
            result.push("");
        }
        return result;
    }
}
