// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/schema/src/formats/typebox/idna/format/bidi.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
/*--------------------------------------------------------------------------

TypeBox

The MIT License (MIT)

Copyright (c) 2017-2026 Haydn Paterson

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

---------------------------------------------------------------------------*/
import * as Pattern from "../pattern/pattern.mjs";
import * as FormatPuny from "./puny.mjs";
const RE_RTL_ALLOWED = /^(?:R|AL|AN|EN|ES|CS|ET|ON|BN|NSM)$/;
const RE_LTR_ALLOWED = /^(?:L|EN|ES|CS|ET|ON|BN|NSM)$/;
const RE_RTL_CLASSES = /^(?:R|AL|AN)$/;
export function HasBidiChars(value) {
    if (FormatPuny.IsAcePrefixed(value)) {
        try {
            return HasRightToLeftCharacters(FormatPuny.Decode(value.slice(4).toLowerCase()));
        } catch  {
            return false;
        }
    }
    return HasRightToLeftCharacters(value);
}
function GetBidiClass(codePoint) {
    const char = String.fromCodePoint(codePoint);
    return Pattern.RE_EUROPEAN_NUMBER.test(char) ? "EN" : Pattern.RE_ARABIC_INDIC_DIGIT.test(char) ? "AN" : Pattern.RE_MARK_NONSPACING.test(char) ? "NSM" : Pattern.RE_SCRIPT_HEBREW.test(char) ? "R" : Pattern.RE_SCRIPT_ARABIC_LETTER.test(char) ? "AL" : Pattern.RE_LETTER.test(char) ? "L" : "ON";
}
export function HasRightToLeftCharacters(value) {
    for (const ch of value)if (RE_RTL_CLASSES.test(GetBidiClass(ch.codePointAt(0)))) return true;
    return false;
}
export function SatisfiesBidiRule(value) {
    let isRtl = false;
    let allowed = RE_LTR_ALLOWED;
    let sawEN = false;
    let sawAN = false;
    let isFirst = true;
    for (const ch of value){
        const bidiClass = GetBidiClass(ch.codePointAt(0));
        if (isFirst) {
            if (bidiClass !== "L" && bidiClass !== "R" && bidiClass !== "AL") return false;
            isRtl = bidiClass === "R" || bidiClass === "AL";
            allowed = isRtl ? RE_RTL_ALLOWED : RE_LTR_ALLOWED;
            isFirst = false;
        }
        if (!allowed.test(bidiClass)) return false;
        if (bidiClass === "EN") sawEN = true;
        else if (bidiClass === "AN") sawAN = true;
    }
    if (isRtl && sawEN && sawAN) return false;
    return true;
}
