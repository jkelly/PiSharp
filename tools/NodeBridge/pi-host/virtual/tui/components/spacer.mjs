// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/tui/src/components/spacer.ts.
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
