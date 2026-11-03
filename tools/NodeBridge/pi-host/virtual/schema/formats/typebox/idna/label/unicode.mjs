// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/formats/typebox/idna/label/unicode.ts.
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
import * as FormatBidi from "../format/bidi.mjs";
import * as FormatPuny from "../format/puny.mjs";
import * as Pattern from "../pattern/pattern.mjs";
function ExceedsMaxALabelLength(value) {
    return Pattern.RE_NON_ASCII.test(value) && FormatPuny.Encode(value).length + 4 > 63;
}
function HasInvalidHyphens(chars) {
    if (chars[0] === "-" || chars[chars.length - 1] === "-") return true;
    return chars.slice(2).join("").startsWith("--");
}
export function IsUnicodeLabel(value) {
    if (ExceedsMaxALabelLength(value)) return false;
    if (FormatBidi.HasRightToLeftCharacters(value) && !FormatBidi.SatisfiesBidiRule(value)) return false;
    const chars = [
        ...value
    ];
    const codePoints = chars.map((c)=>c.codePointAt(0));
    const length = codePoints.length;
    if (HasInvalidHyphens(chars)) return false;
    if (Pattern.RE_COMBINING_MARK.test(chars[0])) return false;
    let hasJapanese = false;
    for(let i = 0; i < length; i++){
        const codePoint = codePoints[i];
        const char = chars[i];
        if (Pattern.RE_RFC5892_DISALLOWED.test(char)) return false;
        if (!Pattern.RE_PERMITTED_CATEGORY.test(char)) return false;
        if (Pattern.RE_SCRIPT_JAPANESE.test(char)) hasJapanese = true;
        const prev = codePoints[i - 1], next = codePoints[i + 1];
        switch(codePoint){
            case 0x00b7:
                if (prev !== 0x006c || next !== 0x006c) return false;
                break;
            case 0x0375:
                if (!next || !Pattern.RE_SCRIPT_GREEK.test(chars[i + 1])) return false;
                break;
            case 0x05f3:
            case 0x05f4:
                if (!prev || !Pattern.RE_SCRIPT_HEBREW.test(chars[i - 1])) return false;
                break;
            case 0x200c:
                if (!prev || prev < 0x0080 && !Pattern.RE_VIRAMA.test(chars[i - 1])) return false;
                break;
            case 0x200d:
                if (!prev || !Pattern.RE_VIRAMA.test(chars[i - 1])) return false;
                break;
            case 0x30fb:
                break;
        }
    }
    if (value.includes("\u30fb") && !hasJapanese) return false;
    return true;
}
