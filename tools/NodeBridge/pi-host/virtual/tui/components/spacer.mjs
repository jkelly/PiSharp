// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/tui/src/components/spacer.ts.
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
