// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/schema/src/formats/typebox/time.ts.
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
const TIME = /^(\d\d):(\d\d):(\d\d)(?:\.\d+)?(?:([Zz])|([+-])(\d\d):(\d\d))?$/;
export function IsTime(value, strictTimeZone = true) {
    const matches = TIME.exec(value);
    if (!matches) return false;
    if (strictTimeZone && !matches[4] && !matches[5]) return false;
    const hr = +matches[1];
    const min = +matches[2];
    const sec = +matches[3];
    if (hr > 23 || min > 59 || sec > 60) return false;
    if (matches[5]) {
        const tzH = +matches[6];
        const tzM = +matches[7];
        if (tzH > 23 || tzM > 59) return false;
    }
    if (sec < 60) return true;
    const tzSign = matches[5] === "-" ? -1 : 1;
    const tzH = +(matches[6] || 0);
    const tzM = +(matches[7] || 0);
    const totalUtcMin = hr * 60 + min - tzSign * (tzH * 60 + tzM);
    return (totalUtcMin % 1440 + 1440) % 1440 === 1439;
}
