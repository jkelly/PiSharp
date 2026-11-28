// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/coding-agent/src/utils/frontmatter.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { parse } from "../../vendor/yaml.mjs";
import { stripBom } from "./text.mjs";
const normalizeNewlines = (value)=>value.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
const extractFrontmatter = (content)=>{
    const normalized = normalizeNewlines(stripBom(content));
    if (!normalized.startsWith("---")) {
        return {
            yamlString: null,
            body: normalized
        };
    }
    const endIndex = normalized.indexOf("\n---", 3);
    if (endIndex === -1) {
        return {
            yamlString: null,
            body: normalized
        };
    }
    return {
        yamlString: normalized.slice(4, endIndex),
        body: normalized.slice(endIndex + 4).trim()
    };
};
export const parseFrontmatter = (content)=>{
    const { yamlString, body } = extractFrontmatter(content);
    if (!yamlString) {
        return {
            frontmatter: {},
            body
        };
    }
    const parsed = parse(yamlString);
    return {
        frontmatter: parsed ?? {},
        body
    };
};
export const stripFrontmatter = (content)=>parseFrontmatter(content).body;
