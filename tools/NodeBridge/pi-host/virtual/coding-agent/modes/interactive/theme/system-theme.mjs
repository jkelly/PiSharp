// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/theme/system-theme.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { colorToOkhsl, colorToOklch, colorToRgb, okhslColor, oklabToOkhslLightness, oklchColor, rgbColor } from "../../../../pi-tui.mjs";
export const SYSTEM_THEME_NAME = "system";
const FAMILIES = {
    neutral: {
        hue: 231.49,
        saturation: {
            min: 0.02,
            max: 0.08
        },
        slot: 8
    },
    blue: {
        hue: 231.49,
        saturation: {
            min: 0.1,
            max: 0.68
        },
        slot: 4
    },
    green: {
        hue: 158.68,
        saturation: {
            min: 0.1,
            max: 0.76
        },
        slot: 2
    },
    red: {
        hue: 20,
        saturation: {
            min: 0.1,
            max: 0.92
        },
        slot: 1
    },
    yellow: {
        hue: 82.36,
        saturation: {
            min: 0.5,
            max: 1
        },
        slot: 3
    },
    orange: {
        hue: 52,
        saturation: {
            min: 0.12,
            max: 0.85
        },
        slot: 3
    },
    violet: {
        hue: 295,
        saturation: {
            min: 0.2,
            max: 0.6
        },
        slot: 5
    },
    calamine: {
        hue: 202.43,
        saturation: {
            min: 0.1,
            max: 0.74
        },
        slot: 6
    },
    thinkingSlate: {
        hue: 231.49,
        saturation: {
            min: 0.08,
            max: 0.2
        },
        slot: 4
    },
    thinkingBlue: {
        hue: 231.49,
        saturation: {
            min: 0.2,
            max: 0.45
        },
        slot: 4
    },
    thinkingPeriwinkle: {
        hue: 263.25,
        saturation: {
            min: 0.3,
            max: 0.6
        },
        slot: 6
    },
    thinkingViolet: {
        hue: 295,
        saturation: {
            min: 0.4,
            max: 0.75
        },
        slot: 5
    },
    thinkingMagenta: {
        hue: 337.5,
        saturation: {
            min: 0.5,
            max: 0.85
        },
        slot: 13
    },
    thinkingRed: {
        hue: 20,
        saturation: {
            min: 0.95,
            max: 1
        },
        slot: 1
    }
};
const TOKEN_FAMILIES = {
    selectedBg: "blue",
    searchMatchBg: "orange",
    userMessageBg: "blue",
    customMessageBg: "violet",
    toolPendingBg: "neutral",
    toolSuccessBg: "green",
    toolErrorBg: "red",
    text: "neutral",
    userMessageText: "neutral",
    customMessageText: "neutral",
    toolTitle: "neutral",
    syntaxOperator: "neutral",
    syntaxPunctuation: "neutral",
    muted: "neutral",
    dim: "neutral",
    thinkingText: "neutral",
    toolOutput: "neutral",
    mdLinkUrl: "neutral",
    mdQuote: "neutral",
    mdQuoteBorder: "neutral",
    mdHr: "neutral",
    mdCodeBlockBorder: "neutral",
    toolDiffContext: "neutral",
    syntaxComment: "neutral",
    scrollbarTrack: "neutral",
    scrollbarThumb: "neutral",
    searchMatchText: "neutral",
    borderMuted: "neutral",
    accent: "violet",
    borderAccent: "violet",
    customMessageLabel: "violet",
    mdCode: "violet",
    mdListBullet: "violet",
    syntaxType: "violet",
    border: "blue",
    mdLink: "blue",
    syntaxKeyword: "blue",
    syntaxVariable: "calamine",
    success: "green",
    mdCodeBlock: "green",
    toolDiffAdded: "green",
    bashMode: "green",
    syntaxNumber: "green",
    error: "red",
    toolDiffRemoved: "red",
    warning: "yellow",
    mdHeading: "yellow",
    syntaxFunction: "yellow",
    syntaxString: "orange",
    thinkingOff: "neutral",
    thinkingMinimal: "thinkingSlate",
    thinkingLow: "thinkingBlue",
    thinkingMedium: "thinkingPeriwinkle",
    thinkingHigh: "thinkingViolet",
    thinkingXhigh: "thinkingMagenta",
    thinkingMax: "thinkingRed"
};
const TOKEN_SLOTS = {
    syntaxString: 2,
    syntaxNumber: 5,
    searchMatchBg: 3
};
const LEVELS = {
    panel: {
        dark: {
            coefficients: [
                0.29131,
                -0.39746,
                2.33185,
                -0.85524,
                -1.2076,
                0.86276
            ],
            reachable: [
                0,
                0.979
            ]
        },
        light: {
            coefficients: [
                -3.74073,
                27.94549,
                -78.44258,
                112.6798,
                -79.60015,
                22.11277
            ],
            reachable: [
                0.348,
                1
            ]
        }
    },
    track: {
        dark: {
            coefficients: [
                0.39028,
                -0.23015,
                0.83573,
                2.43829,
                -4.38292,
                2.01582
            ],
            reachable: [
                0,
                0.946
            ]
        },
        light: {
            coefficients: [
                -5.24921,
                38.37322,
                -107.28833,
                152.10005,
                -106.17127,
                29.18061
            ],
            reachable: [
                0.368,
                1
            ]
        }
    },
    thinking0: {
        dark: {
            coefficients: [
                0.52988,
                -0.05809,
                -0.30924,
                4.63567,
                -6.52933,
                2.89108
            ],
            reachable: [
                0,
                0.873
            ]
        },
        light: {
            coefficients: [
                -28.27749,
                182.85284,
                -469.62416,
                603.15916,
                -384.59976,
                97.35147
            ],
            reachable: [
                0.51,
                1
            ]
        }
    },
    thinking1: {
        dark: {
            coefficients: [
                0.55278,
                -0.03667,
                -0.45659,
                4.95347,
                -6.90265,
                3.0706
            ],
            reachable: [
                0,
                0.858
            ]
        },
        light: {
            coefficients: [
                -37.10484,
                235.86282,
                -596.62344,
                754.3633,
                -474.00763,
                118.3551
            ],
            reachable: [
                0.535,
                1
            ]
        }
    },
    thinking2: {
        dark: {
            coefficients: [
                0.57486,
                -0.01765,
                -0.58987,
                5.25227,
                -7.27175,
                3.25532
            ],
            reachable: [
                0,
                0.842
            ]
        },
        light: {
            coefficients: [
                -59.89653,
                377.05024,
                -945.07843,
                1182.03145,
                -734.96375,
                181.68658
            ],
            reachable: [
                0.556,
                1
            ]
        }
    },
    thinking3: {
        dark: {
            coefficients: [
                0.59621,
                -0.00062,
                -0.71148,
                5.53588,
                -7.6392,
                3.44606
            ],
            reachable: [
                0,
                0.827
            ]
        },
        light: {
            coefficients: [
                -72.07122,
                445.84082,
                -1099.57352,
                1353.88793,
                -829.53392,
                202.26164
            ],
            reachable: [
                0.58,
                1
            ]
        }
    },
    thinking4: {
        dark: {
            coefficients: [
                0.61691,
                0.01462,
                -0.82288,
                5.80651,
                -8.00641,
                3.64333
            ],
            reachable: [
                0,
                0.811
            ]
        },
        light: {
            coefficients: [
                -110.14338,
                674.21488,
                -1645.75941,
                2004.32367,
                -1215.15899,
                293.3183
            ],
            reachable: [
                0.6,
                1
            ]
        }
    },
    thinking5: {
        dark: {
            coefficients: [
                0.63702,
                0.02826,
                -0.92498,
                6.06465,
                -8.37246,
                3.84651
            ],
            reachable: [
                0,
                0.795
            ]
        },
        light: {
            coefficients: [
                -175.47701,
                1063.54495,
                -2570.70594,
                3098.80776,
                -1860.15527,
                444.76392
            ],
            reachable: [
                0.62,
                1
            ]
        }
    },
    thinking6: {
        dark: {
            coefficients: [
                0.65658,
                0.04044,
                -1.01835,
                6.30989,
                -8.73529,
                4.05439
            ],
            reachable: [
                0,
                0.779
            ]
        },
        light: {
            coefficients: [
                -183.81712,
                1094.70055,
                -2602.68539,
                3088.71276,
                -1826.91131,
                430.75931
            ],
            reachable: [
                0.643,
                1
            ]
        }
    },
    subtle: {
        dark: {
            coefficients: [
                0.56762,
                -0.02475,
                -0.5383,
                5.12628,
                -7.10931,
                3.17324
            ],
            reachable: [
                0,
                0.848
            ]
        },
        light: {
            coefficients: [
                -232.85459,
                1376.54473,
                -3249.11801,
                3827.91186,
                -2248.29472,
                526.55751
            ],
            reachable: [
                0.657,
                1
            ]
        }
    },
    thumb: {
        dark: {
            coefficients: [
                0.60323,
                0.00278,
                -0.73328,
                5.57157,
                -7.68067,
                3.46933
            ],
            reachable: [
                0,
                0.823
            ]
        },
        light: {
            coefficients: [
                -82.89897,
                511.01355,
                -1255.98095,
                1540.76821,
                -940.68087,
                228.58523
            ],
            reachable: [
                0.586,
                1
            ]
        }
    },
    readable: {
        dark: {
            coefficients: [
                0.66937,
                0.04704,
                -1.06871,
                6.43941,
                -8.9332,
                4.17229
            ],
            reachable: [
                0,
                0.77
            ]
        },
        light: {
            coefficients: [
                -1554.52576,
                8733.56817,
                -19604.93507,
                21977.72696,
                -12300.99599,
                2749.81288
            ],
            reachable: [
                0.751,
                1
            ]
        }
    },
    emphasis: {
        dark: {
            coefficients: [
                0.7303,
                0.07695,
                -1.31626,
                7.1681,
                -10.14436,
                4.92846
            ],
            reachable: [
                0,
                0.712
            ]
        },
        light: {
            coefficients: [
                -4948.31942,
                26870.91986,
                -58334.48399,
                63280.17197,
                -34298.01053,
                7430.30146
            ],
            reachable: [
                0.811,
                1
            ]
        }
    },
    textOnPanel: {
        dark: {
            coefficients: [
                0.86713,
                0.05232,
                -0.89428,
                4.79014,
                -5.5432,
                1.75023
            ],
            reachable: [
                0,
                0.542
            ]
        },
        light: {
            coefficients: [
                -8570.89457,
                43954.60805,
                -90084.00702,
                92220.6791,
                -47152.15802,
                9632.27113
            ],
            reachable: [
                0.867,
                1
            ]
        }
    },
    text: {
        dark: {
            coefficients: [
                0.89242,
                0.02311,
                -0.44862,
                2.34417,
                -0.06084,
                -2.63844
            ],
            reachable: [
                0,
                0.5
            ]
        },
        light: {
            coefficients: [
                -2004.67048,
                6664.47299,
                -6060.70202,
                -1792.61209,
                5133.82359,
                -1939.85583
            ],
            reachable: [
                0.894,
                1
            ]
        }
    }
};
const TOOL_PANELS = [
    "toolPendingBg",
    "toolSuccessBg",
    "toolErrorBg"
];
const MESSAGE_PANELS = [
    "userMessageBg",
    "customMessageBg"
];
const PANELS = [
    "userMessageBg",
    "toolPendingBg",
    "toolSuccessBg",
    "toolErrorBg",
    "selectedBg",
    "searchMatchBg",
    "customMessageBg"
];
const THINKING = [
    "thinkingOff",
    "thinkingMinimal",
    "thinkingLow",
    "thinkingMedium",
    "thinkingHigh",
    "thinkingXhigh",
    "thinkingMax"
];
const THINKING_LEVELS = [
    "thinking0",
    "thinking1",
    "thinking2",
    "thinking3",
    "thinking4",
    "thinking5",
    "thinking6"
];
const each = (tokens, on, level)=>tokens.map((token)=>({
            token,
            on,
            level
        }));
const RULES = [
    ...each(PANELS, [
        "background"
    ], "panel"),
    {
        token: "text",
        on: [
            "background"
        ],
        level: "text"
    },
    {
        token: "text",
        on: [
            "selectedBg"
        ],
        level: "textOnPanel"
    },
    {
        token: "userMessageText",
        on: [
            "userMessageBg"
        ],
        level: "textOnPanel"
    },
    {
        token: "toolTitle",
        on: TOOL_PANELS,
        level: "textOnPanel"
    },
    ...each([
        "accent",
        "success",
        "error",
        "warning"
    ], [
        "background",
        "selectedBg",
        ...TOOL_PANELS
    ], "readable"),
    {
        token: "muted",
        on: [
            "background",
            "selectedBg",
            "customMessageBg",
            ...TOOL_PANELS
        ],
        level: "readable"
    },
    {
        token: "dim",
        on: [
            "background",
            "selectedBg",
            "customMessageBg",
            ...TOOL_PANELS
        ],
        level: "subtle"
    },
    {
        token: "thinkingText",
        on: [
            "background"
        ],
        level: "readable"
    },
    {
        token: "customMessageText",
        on: [
            "customMessageBg",
            ...TOOL_PANELS
        ],
        level: "readable"
    },
    {
        token: "customMessageLabel",
        on: [
            "background",
            "customMessageBg",
            "selectedBg",
            ...TOOL_PANELS
        ],
        level: "readable"
    },
    {
        token: "toolOutput",
        on: [
            "background",
            ...TOOL_PANELS
        ],
        level: "readable"
    },
    ...each([
        "mdHeading",
        "mdLink",
        "mdLinkUrl",
        "mdCode",
        "mdQuote",
        "mdCodeBlockBorder",
        "mdListBullet"
    ], [
        "background",
        ...MESSAGE_PANELS
    ], "readable"),
    {
        token: "mdCodeBlock",
        on: [
            "background",
            ...MESSAGE_PANELS,
            ...TOOL_PANELS
        ],
        level: "readable"
    },
    ...each([
        "toolDiffAdded",
        "toolDiffRemoved",
        "toolDiffContext"
    ], [
        "background",
        ...TOOL_PANELS
    ], "readable"),
    ...each([
        "syntaxComment",
        "syntaxKeyword",
        "syntaxFunction",
        "syntaxVariable",
        "syntaxString",
        "syntaxNumber",
        "syntaxType",
        "syntaxOperator",
        "syntaxPunctuation"
    ], [
        "background",
        ...MESSAGE_PANELS,
        ...TOOL_PANELS
    ], "readable"),
    {
        token: "searchMatchText",
        on: [
            "searchMatchBg"
        ],
        level: "readable"
    },
    ...each([
        "bashMode",
        "border",
        "borderAccent"
    ], [
        "background"
    ], "readable"),
    {
        token: "borderMuted",
        on: [
            "background"
        ],
        level: "subtle"
    },
    ...each([
        "mdQuoteBorder",
        "mdHr"
    ], [
        "background",
        ...MESSAGE_PANELS,
        ...TOOL_PANELS
    ], "readable"),
    {
        token: "scrollbarTrack",
        on: [
            "background"
        ],
        level: "track"
    },
    {
        token: "scrollbarThumb",
        on: [
            "scrollbarTrack"
        ],
        level: "thumb"
    },
    ...THINKING.map((token, index)=>({
            token,
            on: [
                "background"
            ],
            level: THINKING_LEVELS[index]
        }))
];
const READABLE_FLOOR = {
    dark: "readable",
    light: "subtle"
};
const FOREGROUND_LEVEL = "emphasis";
const FOREGROUND_TOKENS = [
    "text",
    "userMessageText",
    "toolTitle"
];
const TEXT_MINIMUM_WCAG_CONTRAST = 4.5;
const SOLVE_ORDER = (()=>{
    const order = [];
    const visit = (token)=>{
        if (order.includes(token)) return;
        for (const rule of RULES){
            if (rule.token !== token) continue;
            for (const surface of rule.on)if (surface !== "background") visit(surface);
        }
        order.push(token);
    };
    for (const rule of RULES)visit(rule.token);
    return order;
})();
function oklabLightness(color) {
    return colorToOklch(rgbColor(color.r, color.g, color.b)).l;
}
export function relativeLuminance({ r, g, b }) {
    const linear = (channel)=>{
        const value = channel / 255;
        return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}
export function wcagContrast(first, second) {
    const a = relativeLuminance(first);
    const b = relativeLuminance(second);
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}
export function terminalAppearance(background, foreground) {
    const white = {
        r: 255,
        g: 255,
        b: 255
    };
    const black = {
        r: 0,
        g: 0,
        b: 0
    };
    const whiteContrast = wcagContrast(white, background);
    const blackContrast = wcagContrast(black, background);
    if (foreground) {
        const foregroundL = oklabLightness(foreground);
        const backgroundL = oklabLightness(background);
        if (Math.abs(foregroundL - backgroundL) > 0.05) {
            const appearance = foregroundL > backgroundL ? "dark" : "light";
            const best = appearance === "dark" ? whiteContrast : blackContrast;
            if (best >= TEXT_MINIMUM_WCAG_CONTRAST) return appearance;
        }
    }
    return whiteContrast >= blackContrast ? "dark" : "light";
}
const clamp = (value, min, max)=>Math.min(max, Math.max(min, value));
function hexOf({ r, g, b }) {
    return `#${[
        r,
        g,
        b
    ].map((channel)=>Math.round(channel).toString(16).padStart(2, "0")).join("")}`;
}
function bellWeight(lightness) {
    const gaussian = (x)=>Math.exp(-((x - 0.5) ** 2) / (2 * 0.25 ** 2));
    return (gaussian(lightness) - gaussian(0)) / (1 - gaussian(0));
}
function saturationCurve({ saturation: { min, max } }, lightness) {
    const floor = max > 0 ? min / max : 1;
    return floor + (1 - floor) * bellWeight(lightness);
}
function levelTarget(level, appearance, surfaceL) {
    const curve = LEVELS[level][appearance];
    if (surfaceL < curve.reachable[0] || surfaceL > curve.reachable[1]) return undefined;
    return curve.coefficients.reduce((sum, coefficient, power)=>sum + coefficient * surfaceL ** power, 0);
}
export function generateSystemThemeColors(input) {
    const saturation = clamp(input.saturation ?? 1, 0, 1);
    const { background, foreground } = input;
    if (!background) return indexedColors(saturation, input.appearanceHint);
    const palette = input.palette?.length === 16 ? input.palette.map(sourceOf) : undefined;
    const appearance = terminalAppearance(background, foreground);
    const lighter = appearance === "dark";
    const extreme = lighter ? 1 : 0;
    const backgroundL = oklabLightness(background);
    const paint = (token, oklabL)=>{
        const lightness = oklabToOkhslLightness(oklabL);
        const family = FAMILIES[TOKEN_FAMILIES[token]];
        if (!palette) {
            const { min, max } = family.saturation;
            return okhslColor(family.hue, (min + (max - min) * bellWeight(lightness)) * saturation, lightness);
        }
        return anchored(palette[TOKEN_SLOTS[token] ?? family.slot], family, lightness, saturation);
    };
    const target = (level, surfaceL, t)=>{
        const reached = levelTarget(level, appearance, surfaceL);
        if (reached === undefined && t === 0) return undefined;
        const distance = (reached ?? extreme) - surfaceL;
        const floor = (levelTarget(READABLE_FLOOR[appearance], appearance, surfaceL) ?? extreme) - surfaceL;
        const compressed = Math.abs(distance) > Math.abs(floor) ? distance - (distance - floor) * Math.min(t, 1) : distance;
        return surfaceL + compressed * (1 - Math.max(0, t - 1));
    };
    const extremeText = lighter ? {
        r: 255,
        g: 255,
        b: 255
    } : {
        r: 0,
        g: 0,
        b: 0
    };
    const readable = (color)=>wcagContrast(extremeText, color) >= TEXT_MINIMUM_WCAG_CONTRAST;
    const limitPanel = (token, l)=>{
        const color = paint(token, l);
        if (readable(color)) return color;
        let [low, high] = [
            backgroundL,
            l
        ];
        for(let index = 0; index < 20; index++){
            const middle = (low + high) / 2;
            if (readable(paint(token, middle))) low = middle;
            else high = middle;
        }
        return paint(token, low);
    };
    const solve = (t)=>{
        const colors = new Map([
            [
                "background",
                background
            ]
        ]);
        for (const token of SOLVE_ORDER){
            const targets = [];
            for (const rule of RULES){
                if (rule.token !== token) continue;
                for (const surface of rule.on){
                    const value = target(rule.level, oklabLightness(colors.get(surface) ?? background), t);
                    if (value === undefined || value < 0 || value > 1) return undefined;
                    targets.push(value);
                }
            }
            const l = lighter ? Math.max(...targets) : Math.min(...targets);
            colors.set(token, PANELS.includes(token) ? limitPanel(token, l) : paint(token, l));
        }
        return colors;
    };
    let relaxation = 0;
    let colors = solve(0);
    if (!colors) {
        let [low, high] = [
            0,
            2
        ];
        colors = solve(high);
        for(let index = 0; index < 20; index++){
            const middle = (low + high) / 2;
            const attempt = solve(middle);
            if (attempt) [high, colors] = [
                middle,
                attempt
            ];
            else low = middle;
        }
        relaxation = high;
    }
    const solved = colors ?? new Map();
    const surfacesOf = (token)=>RULES.filter((rule)=>rule.token === token).flatMap((rule)=>rule.on.map((surface)=>solved.get(surface) ?? background));
    const result = {};
    for (const token of Object.keys(TOKEN_FAMILIES)){
        const color = solved.get(token);
        result[token] = color ? hexOf(color) : "";
    }
    for (const token of FOREGROUND_TOKENS){
        const surfaces = surfacesOf(token);
        let text = solved.get(token);
        if (foreground) {
            const targets = surfaces.map((surface)=>target(FOREGROUND_LEVEL, oklabLightness(surface), relaxation));
            if (targets.every((value)=>value !== undefined && value >= 0 && value <= 1)) {
                const needed = lighter ? Math.max(...targets) : Math.min(...targets);
                const foregroundL = oklabLightness(foreground);
                if (lighter ? foregroundL >= needed : foregroundL <= needed) {
                    result[token] = "";
                    continue;
                }
                text = anchored(sourceOf(foreground), FAMILIES.neutral, oklabToOkhslLightness(needed), saturation);
            }
        }
        if (text) result[token] = hexOf(withTextContrast(text, surfaces, lighter));
    }
    return {
        colors: result,
        dim: [],
        appearance
    };
}
function okhslOf({ r, g, b }) {
    return colorToOkhsl(rgbColor(r, g, b));
}
function sourceOf(color) {
    return {
        ...okhslOf(color),
        chroma: colorToOklch(rgbColor(color.r, color.g, color.b)).c
    };
}
function anchored(source, family, lightness, saturation) {
    const anchor = saturationCurve(family, source.l);
    const falloff = anchor > 0 ? Math.min(1, saturationCurve(family, lightness) / anchor) : 1;
    const color = okhslColor(source.h, source.s * falloff * saturation, lightness);
    const cap = source.chroma * falloff * saturation;
    const { l, c } = colorToOklch(color);
    return c <= cap ? color : colorToRgb(oklchColor(l, cap, source.h));
}
function withTextContrast(color, surfaces, lighter) {
    const meets = (candidate)=>surfaces.every((surface)=>wcagContrast(candidate, surface) >= TEXT_MINIMUM_WCAG_CONTRAST);
    if (meets(color)) return color;
    const { h, s, l } = okhslOf(color);
    const at = (lightness)=>okhslColor(h, s, lightness);
    const extreme = lighter ? 1 : 0;
    if (!meets(at(extreme))) return at(extreme);
    let [low, high] = [
        l,
        extreme
    ];
    for(let index = 0; index < 20; index++){
        const middle = (low + high) / 2;
        if (meets(at(middle))) high = middle;
        else low = middle;
    }
    return at(high);
}
function indexedColors(saturation, appearance) {
    const colors = {};
    const dim = [];
    for (const [token, familyName] of Object.entries(TOKEN_FAMILIES)){
        if (PANELS.includes(token)) {
            colors[token] = "";
            continue;
        }
        const neutral = familyName === "neutral";
        colors[token] = !neutral && saturation > 0 ? TOKEN_SLOTS[token] ?? FAMILIES[familyName].slot : "";
        if (neutral && !FOREGROUND_TOKENS.includes(token)) dim.push(token);
    }
    return {
        colors,
        dim,
        appearance
    };
}
