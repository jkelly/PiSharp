// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/custom-editor.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { Editor, visibleWidth } from "../../../../pi-tui.mjs";
export class CustomEditor extends Editor {
    keybindings;
    workingStatusIndicator;
    embedWorkingStatus;
    actionHandlers = new Map();
    onEscape;
    onCtrlD;
    onPasteImage;
    onExtensionShortcut;
    constructor(tui, theme, keybindings, options){
        super(tui, theme, options);
        this.keybindings = keybindings;
        this.embedWorkingStatus = options?.embedWorkingStatus ?? false;
    }
    setWorkingStatusIndicator(indicator) {
        this.workingStatusIndicator = indicator;
    }
    renderTopBorder(width, hiddenLineCount) {
        if (!this.embedWorkingStatus || !this.workingStatusIndicator || width <= 0) {
            return super.renderTopBorder(width, hiddenLineCount);
        }
        let status = this.workingStatusIndicator.renderInBorder(Math.max(1, width - 5));
        let statusWidth = visibleWidth(status);
        if (statusWidth === 0) return super.renderTopBorder(width, hiddenLineCount);
        const overflowLabel = hiddenLineCount > 0 ? ` ↑ ${hiddenLineCount} more ` : undefined;
        const overflowLabelWidth = overflowLabel ? visibleWidth(overflowLabel) : 0;
        const overflowStart = Math.floor((width - overflowLabelWidth) / 2);
        const canFitOverflow = ()=>overflowLabel !== undefined && overflowLabelWidth + 2 <= width && overflowStart - (3 + statusWidth + 1) >= 1;
        if (overflowLabel && !canFitOverflow()) {
            status = this.workingStatusIndicator.renderSpinnerInBorder(width);
            statusWidth = visibleWidth(status);
        }
        if (canFitOverflow()) {
            const leftBlockWidth = 3 + statusWidth + 1;
            return this.borderColor("── ") + status + this.borderColor(` ${"─".repeat(overflowStart - leftBlockWidth)}${overflowLabel}${"─".repeat(width - overflowStart - overflowLabelWidth)}`);
        }
        if (width >= statusWidth + 5) {
            return this.borderColor("── ") + status + this.borderColor(` ${"─".repeat(width - statusWidth - 4)}`);
        }
        status = this.workingStatusIndicator.renderSpinnerInBorder(width);
        statusWidth = visibleWidth(status);
        const prefixWidth = Math.min(3, Math.max(0, width - statusWidth));
        return this.borderColor("─".repeat(prefixWidth)) + status + this.borderColor("─".repeat(Math.max(0, width - prefixWidth - statusWidth)));
    }
    onAction(action, handler) {
        this.actionHandlers.set(action, handler);
    }
    handleInput(data) {
        if (this.onExtensionShortcut?.(data)) {
            return;
        }
        if (this.keybindings.matches(data, "app.clipboard.pasteImage")) {
            this.onPasteImage?.();
            return;
        }
        if (this.keybindings.matches(data, "app.interrupt")) {
            if (!this.isShowingAutocomplete()) {
                const handler = this.onEscape ?? this.actionHandlers.get("app.interrupt");
                if (handler) {
                    handler();
                    return;
                }
            }
            super.handleInput(data);
            return;
        }
        if (this.keybindings.matches(data, "app.exit")) {
            if (this.getText().length === 0) {
                const handler = this.onCtrlD ?? this.actionHandlers.get("app.exit");
                if (handler) handler();
                return;
            }
        }
        if (this.keybindings.matches(data, "tui.editor.historyPrevious") || this.keybindings.matches(data, "tui.editor.historyNext")) {
            super.handleInput(data);
            return;
        }
        for (const [action, handler] of this.actionHandlers){
            if (action !== "app.interrupt" && action !== "app.exit" && this.keybindings.matches(data, action)) {
                handler();
                return;
            }
        }
        super.handleInput(data);
    }
}
