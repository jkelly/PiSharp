// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/wheel-scroll.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
const BURST_GAP_MS = 5;
const GESTURE_GAP_MS = 200;
const REFERENCE_GAP_MS = 100;
const MAX_AUTO_LINES = 6;
function terminalAcceleratesWheel() {
    const env = process.env;
    return process.platform === "darwin" && env.SSH_CONNECTION === undefined && env.SSH_CLIENT === undefined && env.SSH_TTY === undefined;
}
export class WheelScrollAccelerator {
    lines;
    accelerate;
    lastTime = Number.NEGATIVE_INFINITY;
    lastDirection = 0;
    averageGap;
    carry = 0;
    constructor(lines = "auto", accelerate = !terminalAcceleratesWheel()){
        this.lines = lines;
        this.accelerate = accelerate;
    }
    setLines(lines) {
        this.lines = lines;
        this.reset();
    }
    next(direction, now) {
        if (this.lines !== "auto") return Number.isFinite(this.lines) ? Math.max(1, Math.floor(this.lines)) : 1;
        if (!this.accelerate) return 1;
        const gap = now - this.lastTime;
        const sameGesture = direction === this.lastDirection && gap <= GESTURE_GAP_MS;
        this.lastTime = now;
        this.lastDirection = direction;
        if (!sameGesture) {
            this.averageGap = undefined;
            this.carry = 0;
            return 1;
        }
        if (gap < BURST_GAP_MS) return 1;
        this.averageGap = this.averageGap === undefined ? gap : (this.averageGap + gap) / 2;
        const lines = Math.min(MAX_AUTO_LINES, Math.max(1, REFERENCE_GAP_MS / this.averageGap)) + this.carry;
        const whole = Math.floor(lines);
        this.carry = lines - whole;
        return whole;
    }
    reset() {
        this.lastTime = Number.NEGATIVE_INFINITY;
        this.lastDirection = 0;
        this.averageGap = undefined;
        this.carry = 0;
    }
}
