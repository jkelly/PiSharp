// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/tui/src/layout-node.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export const LAYOUT_NODE = Symbol.for("@earendil-works/pi-tui/layout-node");
export function getLayoutNode(component) {
    const candidate = component;
    return typeof candidate[LAYOUT_NODE] === "function" ? candidate[LAYOUT_NODE]() : undefined;
}
