// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/formats/typebox/idna/idn-hostname.ts.
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
import * as FormatBidi from "./format/bidi.mjs";
import * as LabelPuny from "./label/puny.mjs";
import * as LabelUnicode from "./label/unicode.mjs";
function IsValidLabelLength(value) {
    return value.length > 0 && value.length <= 63;
}
function IsLabel(value) {
    return IsValidLabelLength(value) && (LabelPuny.IsPunyLabel(value) || LabelUnicode.IsUnicodeLabel(value));
}
function NormalizeHostname(value) {
    return value.replace(/[\uff01-\uff5e]/g, (char)=>String.fromCharCode(char.charCodeAt(0) - 0xfee0)).normalize("NFC").replace(/[\u00ad\u034f\u180b-\u180d\u200b\ufe00-\ufe0f\u{e0100}-\u{e01ef}]/gu, "").replace(/[\u002E\u3002\uFF0E\uFF61]/g, ".");
}
export function IsIdnHostname(value) {
    if (value.length === 0 || value.includes(" ")) return false;
    const normalized = NormalizeHostname(value);
    if (normalized.length > 253) return false;
    const labels = normalized.split(".");
    const hasBidiChars = labels.some((label)=>FormatBidi.HasBidiChars(label));
    return labels.every((label)=>IsLabel(label) && (!hasBidiChars || FormatBidi.SatisfiesBidiRule(label)));
}
