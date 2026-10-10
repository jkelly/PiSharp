// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/components/loader.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { Text } from "./text.mjs";
const DEFAULT_FRAMES = [
    "⠋",
    "⠙",
    "⠹",
    "⠸",
    "⠼",
    "⠴",
    "⠦",
    "⠧",
    "⠇",
    "⠏"
];
const DEFAULT_INTERVAL_MS = 80;
export class Loader extends Text {
    frames = [
        ...DEFAULT_FRAMES
    ];
    intervalMs = DEFAULT_INTERVAL_MS;
    currentFrame = 0;
    intervalId = null;
    ui = null;
    renderIndicatorVerbatim = false;
    spinnerColorFn;
    messageColorFn;
    message = "Loading...";
    constructor(ui, spinnerColorFn, messageColorFn, message = "Loading...", indicator){
        super("", 1, 0);
        this.ui = ui;
        this.spinnerColorFn = spinnerColorFn;
        this.messageColorFn = messageColorFn;
        this.message = message;
        this.setIndicator(indicator);
    }
    render(width) {
        return [
            "",
            ...super.render(width)
        ];
    }
    start() {
        this.updateDisplay();
        this.restartAnimation();
    }
    stop() {
        if (this.intervalId) {
            clearInterval(this.intervalId);
            this.intervalId = null;
        }
    }
    setMessage(message) {
        this.message = message;
        this.updateDisplay();
    }
    invalidate() {
        super.invalidate();
        this.updateDisplay();
    }
    setIndicator(indicator) {
        this.renderIndicatorVerbatim = indicator !== undefined;
        this.frames = indicator?.frames !== undefined ? [
            ...indicator.frames
        ] : [
            ...DEFAULT_FRAMES
        ];
        this.intervalMs = indicator?.intervalMs && indicator.intervalMs > 0 ? indicator.intervalMs : DEFAULT_INTERVAL_MS;
        this.currentFrame = 0;
        this.start();
    }
    restartAnimation() {
        this.stop();
        if (this.frames.length <= 1) {
            return;
        }
        this.intervalId = setInterval(()=>{
            this.currentFrame = (this.currentFrame + 1) % this.frames.length;
            this.updateDisplay();
        }, this.intervalMs);
    }
    getRenderedIndicator() {
        const frame = this.frames[this.currentFrame] ?? "";
        return this.renderIndicatorVerbatim ? frame : this.spinnerColorFn(frame);
    }
    updateDisplay() {
        const renderedFrame = this.getRenderedIndicator();
        const indicator = renderedFrame.length > 0 ? `${renderedFrame} ` : "";
        this.setText(`${indicator}${this.messageColorFn(this.message)}`);
        if (this.ui) {
            this.ui.requestRender();
        }
    }
}
