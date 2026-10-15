// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/tui/src/layout-node.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export const LAYOUT_NODE = Symbol.for("@earendil-works/pi-tui/layout-node");
export function getLayoutNode(component) {
    const candidate = component;
    return typeof candidate[LAYOUT_NODE] === "function" ? candidate[LAYOUT_NODE]() : undefined;
}
