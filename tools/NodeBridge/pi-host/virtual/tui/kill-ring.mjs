// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/tui/src/kill-ring.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export class KillRing {
    ring = [];
    push(text, opts) {
        if (!text) return;
        if (opts.accumulate && this.ring.length > 0) {
            const last = this.ring.pop();
            this.ring.push(opts.prepend ? text + last : last + text);
        } else {
            this.ring.push(text);
        }
    }
    peek() {
        return this.ring.length > 0 ? this.ring[this.ring.length - 1] : undefined;
    }
    rotate() {
        if (this.ring.length > 1) {
            const last = this.ring.pop();
            this.ring.unshift(last);
        }
    }
    get length() {
        return this.ring.length;
    }
}
