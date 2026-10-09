// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/components/settings-list.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { fuzzyFilter } from "../fuzzy.mjs";
import { getKeybindings } from "../keybindings.mjs";
import { truncateToWidth, visibleWidth, wrapTextWithAnsi } from "../utils.mjs";
import { Input } from "./input.mjs";
export class SettingsList {
    items;
    filteredItems;
    theme;
    selectedIndex = 0;
    mousePressedIndex;
    maxVisible;
    onChange;
    onCancel;
    searchInput;
    searchEnabled;
    submenuComponent = null;
    submenuItemIndex = null;
    navigateAfterClose = null;
    constructor(items, maxVisible, theme, onChange, onCancel, options = {}){
        this.items = items;
        this.filteredItems = items;
        this.maxVisible = maxVisible;
        this.theme = theme;
        this.onChange = onChange;
        this.onCancel = onCancel;
        this.searchEnabled = options.enableSearch ?? false;
        if (this.searchEnabled) {
            this.searchInput = new Input();
        }
    }
    updateValue(id, newValue) {
        const item = this.items.find((i)=>i.id === id);
        if (item) {
            item.currentValue = newValue;
        }
    }
    selectItem(id) {
        const items = this.searchEnabled ? this.filteredItems : this.items;
        const index = items.findIndex((i)=>i.id === id);
        if (index !== -1) {
            this.selectedIndex = index;
        }
    }
    invalidate() {
        this.submenuComponent?.invalidate?.();
    }
    render(width) {
        if (this.submenuComponent) {
            return this.submenuComponent.render(width);
        }
        return this.renderMainList(width);
    }
    renderMainList(width) {
        const lines = [];
        if (this.searchEnabled && this.searchInput) {
            lines.push(...this.searchInput.render(width));
            lines.push("");
        }
        if (this.items.length === 0) {
            lines.push(this.theme.hint("  No settings available"));
            if (this.searchEnabled) {
                this.addHintLine(lines, width);
            }
            return lines;
        }
        const displayItems = this.getDisplayItems();
        if (displayItems.length === 0) {
            lines.push(truncateToWidth(this.theme.hint("  No matching settings"), width));
            this.addHintLine(lines, width);
            return lines;
        }
        const { startIndex, endIndex } = this.getVisibleRange(displayItems);
        const maxLabelWidth = Math.min(36, Math.max(...this.items.map((item)=>visibleWidth(item.label))));
        for(let i = startIndex; i < endIndex; i++){
            const item = displayItems[i];
            if (!item) continue;
            const isSelected = i === this.selectedIndex;
            const prefix = isSelected ? this.theme.cursor : "  ";
            const prefixWidth = visibleWidth(prefix);
            const labelPadded = item.label + " ".repeat(Math.max(0, maxLabelWidth - visibleWidth(item.label)));
            const labelText = this.theme.label(labelPadded, isSelected);
            const separator = "  ";
            const usedWidth = prefixWidth + maxLabelWidth + visibleWidth(separator);
            const valueMaxWidth = width - usedWidth - 2;
            const valueText = this.theme.value(truncateToWidth(item.currentValue, valueMaxWidth, ""), isSelected);
            lines.push(truncateToWidth(prefix + labelText + separator + valueText, width));
        }
        if (startIndex > 0 || endIndex < displayItems.length) {
            const scrollText = `  (${this.selectedIndex + 1}/${displayItems.length})`;
            lines.push(this.theme.hint(truncateToWidth(scrollText, width - 2, "")));
        }
        const selectedItem = displayItems[this.selectedIndex];
        if (selectedItem?.description) {
            lines.push("");
            const wrappedDesc = wrapTextWithAnsi(selectedItem.description, width - 4);
            for (const line of wrappedDesc){
                lines.push(this.theme.description(`  ${line}`));
            }
        }
        this.addHintLine(lines, width);
        return lines;
    }
    handleMouse(event) {
        if (this.submenuComponent) {
            const result = this.submenuComponent.handleMouse?.(event);
            return result ? {
                ...result,
                focus: true
            } : undefined;
        }
        if (this.searchEnabled && this.searchInput) {
            if (event.y === 0) {
                const result = this.searchInput.handleMouse?.(event);
                return result ? {
                    ...result,
                    focus: true
                } : undefined;
            }
            if (event.y === 1) return undefined;
        }
        const displayItems = this.getDisplayItems();
        if (displayItems.length === 0) return undefined;
        if (event.type === "wheel" && event.wheelDelta) {
            const delta = event.wheelDelta < 0 ? -1 : 1;
            const previousIndex = this.selectedIndex;
            this.selectedIndex = Math.max(0, Math.min(displayItems.length - 1, this.selectedIndex + delta));
            return {
                handled: true,
                render: this.selectedIndex !== previousIndex
            };
        }
        if (event.button !== "left" || event.type !== "press" && event.type !== "click") return undefined;
        const rowOffset = this.searchEnabled ? 2 : 0;
        const { startIndex, endIndex } = this.getVisibleRange(displayItems);
        const itemIndex = startIndex + event.y - rowOffset;
        if (itemIndex < startIndex || itemIndex >= endIndex) return undefined;
        if (event.type === "press") {
            this.mousePressedIndex = itemIndex;
            this.selectedIndex = itemIndex;
            return {
                handled: true,
                focus: true
            };
        }
        if (event.type === "click") {
            this.selectedIndex = this.mousePressedIndex ?? itemIndex;
            this.mousePressedIndex = undefined;
            this.activateItem();
            return {
                handled: true
            };
        }
        return undefined;
    }
    handleInput(data) {
        if (this.submenuComponent) {
            this.submenuComponent.handleInput?.(data);
            return;
        }
        const kb = getKeybindings();
        const displayItems = this.getDisplayItems();
        if (kb.matches(data, "tui.select.up")) {
            if (displayItems.length === 0) return;
            this.selectedIndex = this.selectedIndex === 0 ? displayItems.length - 1 : this.selectedIndex - 1;
        } else if (kb.matches(data, "tui.select.down")) {
            if (displayItems.length === 0) return;
            this.selectedIndex = this.selectedIndex === displayItems.length - 1 ? 0 : this.selectedIndex + 1;
        } else if (kb.matches(data, "tui.select.confirm") || data === " " && (!this.searchEnabled || this.searchInput?.getValue().length === 0)) {
            this.activateItem();
        } else if (kb.matches(data, "tui.select.cancel")) {
            this.onCancel();
        } else if (this.searchEnabled && this.searchInput) {
            this.searchInput.handleInput(data);
            this.applyFilter(this.searchInput.getValue());
        }
    }
    getDisplayItems() {
        return this.searchEnabled ? this.filteredItems : this.items;
    }
    getVisibleRange(displayItems) {
        const startIndex = Math.max(0, Math.min(this.selectedIndex - Math.floor(this.maxVisible / 2), displayItems.length - this.maxVisible));
        return {
            startIndex,
            endIndex: Math.min(startIndex + this.maxVisible, displayItems.length)
        };
    }
    activateItem() {
        const item = this.getDisplayItems()[this.selectedIndex];
        if (!item) return;
        if (item.submenu) {
            this.submenuItemIndex = this.selectedIndex;
            this.submenuComponent = item.submenu(item.currentValue, (selectedValue, options)=>{
                if (selectedValue !== undefined) {
                    item.currentValue = selectedValue;
                    this.onChange(item.id, selectedValue);
                }
                if (options?.navigateTo) {
                    this.navigateAfterClose = options.navigateTo;
                }
                this.closeSubmenu();
            });
        } else if (item.values && item.values.length > 0) {
            const currentIndex = item.values.indexOf(item.currentValue);
            const nextIndex = (currentIndex + 1) % item.values.length;
            const newValue = item.values[nextIndex];
            item.currentValue = newValue;
            this.onChange(item.id, newValue);
        }
    }
    closeSubmenu() {
        this.submenuComponent = null;
        if (this.navigateAfterClose !== null) {
            const id = this.navigateAfterClose;
            this.navigateAfterClose = null;
            this.submenuItemIndex = null;
            this.selectItem(id);
            this.activateItem();
        } else if (this.submenuItemIndex !== null) {
            this.selectedIndex = this.submenuItemIndex;
            this.submenuItemIndex = null;
        }
    }
    applyFilter(query) {
        this.filteredItems = fuzzyFilter(this.items, query, (item)=>item.label);
        this.selectedIndex = 0;
    }
    addHintLine(lines, width) {
        lines.push("");
        lines.push(truncateToWidth(this.theme.hint(this.searchEnabled ? "  Type to search · Enter/Space to change · Esc to cancel" : "  Enter/Space to change · Esc to cancel"), width));
    }
}
