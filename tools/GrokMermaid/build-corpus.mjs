// Builds mermaid-corpus.json, the differential corpus for the C# port of grok-mermaid: hand-written cases for every
// diagram kind and syntax feature, the README examples, Unicode-heavy labels, malformed input, caps and very large
// diagrams, plus a deterministic grammar fuzz. Usage: node build-corpus.mjs <out mermaid-corpus.json>
import { writeFileSync } from 'node:fs'

const outPath = process.argv[2]
if (!outPath) {
  console.error('usage: node build-corpus.mjs <mermaid-corpus.json>')
  process.exit(2)
}

const u = (...cps) => String.fromCodePoint(...cps)
const NBSP = u(0xa0)
const ZWJ = u(0x200d)
const ZWSP = u(0x200b)
const SHY = u(0xad)
const VS16 = u(0xfe0f)
const BOM = u(0xfeff)
const ESC = u(0x1b)
const LS = u(0x2028)
const IDEO_SPACE = u(0x3000)
const FAMILY = [0x1f468, 0x1f469, 0x1f467].map((c) => u(c)).join(ZWJ)
const FLAG_US = u(0x1f1fa, 0x1f1f8)
const FLAG_JP = u(0x1f1ef, 0x1f1f5)
const ROCKET = u(0x1f680)
const THUMB_DARK = u(0x1f44d, 0x1f3ff)
const HEART = u(0x2764) + VS16
const KEYCAP = `1${VS16}${u(0x20e3)}`
const E_ACUTE = `e${u(0x301)}`
const DEVANAGARI = u(0x915, 0x94d, 0x937, 0x924, 0x94d, 0x930, 0x93f, 0x92f) // क्षत्रिय
const HANGUL_JAMO = u(0x1100, 0x1161, 0x11a8) + u(0xd55c)
const THAI = u(0xe01, 0xe33, 0xe41, 0xe1e, 0xe07)
const ARABIC = u(0x645, 0x631, 0x62d, 0x628, 0x627)
const CJK = '中文标签'
const JP = 'こんにちは世界'
const FULLWIDTH = u(0xff21, 0xff22, 0xff23)
const TAG_FLAG = u(0x1f3f4, 0xe0067, 0xe0062, 0xe0073, 0xe0063, 0xe0074, 0xe007f) // Scotland

const cases = []
const add = (id, src) => cases.push({ id, src })

// ------------------------------------------------------------------ README
add('readme-usage', 'flowchart LR\n  A[Start] --> B[Done]')
add(
  'readme-hero',
  'flowchart TD\n  A[Parse source] --> B{Supported?}\n  B -->|yes| C[Lay out]\n  B -->|no| D[Framed source]\n  C --> E[Unicode art]\n  D --> E',
)
add('readme-unclosed', 'graph TD\n A[Start --> B')
add('readme-state-garbage', 'stateDiagram-v2\n A --> B\n some garbage line')
add('readme-demo-colours', 'flowchart LR\n  A[Client] -->|request| B(Server)\n  B -.->|cache miss| C[(Database)]\n  B ==>|response| A')

// ------------------------------------------------------------- empty / misc
add('empty', '')
add('blank-spaces', '   ')
add('blank-lines', '\n\n\t\n')
add('blank-nbsp', `${NBSP}${IDEO_SPACE}${BOM}`)
add('comment-only', '%% just a comment')
add('no-header', 'A --> B')
add('header-only-graph', 'graph TD')
add('header-only-flowchart', 'flowchart')
add('header-only-state', 'stateDiagram-v2')
add('header-only-class', 'classDiagram')
add('header-only-er', 'erDiagram')
add('header-only-seq', 'sequenceDiagram')
for (const kind of ['pie', 'gantt', 'journey', 'gitGraph', 'mindmap', 'timeline', 'quadrantChart', 'xychart-beta', 'block-beta', 'C4Context', 'requirementDiagram', 'sankey-beta']) {
  add(`unsupported-${kind}`, `${kind}\n  title Something\n  A : 1`)
}
add('unsupported-pie-full', 'pie title Pets\n  "Dogs" : 386\n  "Cats" : 85')
add('header-uppercase', 'GRAPH LR\nA-->B')
add('header-mixedcase', 'FlowChart rl\nA-->B')
add('header-semicolon', 'graph TD;A-->B;B-->C;')
add('header-comment-first', '%% leading comment\ngraph LR\nA-->B')
add('header-leading-blank', '\n\n  graph TD\n  A --> B\n')
add('header-crlf', 'graph TD\r\n  A --> B\r\n  B --> C\r\n')
add('header-bom', `${BOM}graph TD\nA-->B`)
add('header-tabs', 'graph\tLR\n\tA\t-->\tB')
add('controls-stripped', `graph TD\nA[a${u(7)}b${ESC}[31mc] --> B[${u(0x85)}x${u(0x9f)}]`)
add('controls-only', `${u(1)}${u(2)}${u(0x7f)}`)
add('line-separator', `graph TD\nA[left${LS}right] --> B`)
add('trailing-whitespace', 'graph LR\n  A --> B   \n\n   \n')

add('box-long-header', `averyveryveryverylongheadertokenthatexceedsthirtycolumns LR\n  ${'x'.repeat(70)}`)
add('box-wide-lines', `pie\n  ${'漢字'.repeat(25)}\n\n  ${ROCKET.repeat(20)} ${FAMILY.repeat(8)}\n  a${'b'.repeat(29)}${CJK}\n\t\ttabbed\t  `)
add('box-zero-width', `journey\n  ${ZWSP.repeat(40)}${SHY}x\n  ${E_ACUTE.repeat(40)}`)

// ---------------------------------------------------------------- flowchart
for (const dir of ['TD', 'TB', 'BT', 'LR', 'RL', 'lr', 'XY', '']) {
  add(`fc-dir-${dir || 'none'}`, `flowchart ${dir}\n  A[Start] --> B{Check}\n  B -->|yes| C(Do it)\n  B -->|no| D((Stop))\n  C --> E[[Done]]\n  D --> E`)
}
add('fc-shapes', 'graph TD\n  a[rect] --> b(round)\n  b --> c((circle))\n  c --> d[[subroutine]]\n  d --> e[(database)]\n  e --> f([stadium])\n  f --> g{diamond}\n  g --> h{{hexagon}}\n  h --> i>flag]\n  i --> j\n  j --> k[(cyl)]\n  k --> l[/para/]')
add('fc-shapes-lr', 'graph LR\n  a[rect] --> b(round) --> c((circle)) --> d{diamond} --> e{{hex}} --> f>flag]')
add(
  'fc-links-solid',
  'graph TD\n  A --> B\n  A --- C\n  A <--> D\n  A <-- E\n  A o--o F\n  A x--x G\n  A --o H\n  A --x I\n  A ---> J\n  A ----> K',
)
add('fc-links-dotted', 'graph TD\n  A -.-> B\n  A -.- C\n  A <-.-> D\n  A -..-> E\n  A o-.-o F\n  A -.-x G')
add('fc-links-thick', 'graph TD\n  A ==> B\n  A === C\n  A <==> D\n  A ===> E\n  A o==o F\n  A ==x G')
add('fc-links-lr', 'graph LR\n  A --> B\n  A --- C\n  A -.-> D\n  A ==> E\n  A --o F\n  A --x G\n  A <--> H')
add('fc-links-bt', 'graph BT\n  A --> B\n  A -.-> C\n  A ==> D\n  A --o E\n  A <--> F')
add('fc-links-rl', 'graph RL\n  A --> B\n  A -.-> C\n  A ==> D\n  A --x E\n  A <--> F')
add('fc-labels-pipe', 'graph TD\n  A -->|one| B\n  A -.->|two| C\n  A ==>|three| D\n  A ---|four| E\n  A -->|"quoted | pipe"| F')
add('fc-labels-inline', 'graph TD\n  A -- one --> B\n  A -. two .-> C\n  A == three ==> D\n  A -- four --- E\n  A -- five --o F\n  A -- six --x G')
add('fc-labels-inline-lr', 'graph LR\n  A -- one --> B\n  A -. two .-> C\n  A == three ==> D')
add('fc-label-empty-pipe', 'graph LR\n  A -->|| B\n  A -->| | C')
add('fc-label-unclosed-pipe', 'graph LR\n  A -->|never closed B')
add('fc-nospace', 'graph LR\nA-->B-->C\nC-.->D==>E')
add('fc-chain', 'graph TD\n  A --> B --> C --> D --> E')
add('fc-chain-lr-labels', 'graph LR\n  A -->|a| B -->|b| C -->|c| D')
add('fc-amp', 'graph TD\n  A & B --> C & D')
add('fc-amp-chain', 'graph LR\n  A & B --> C --> D & E & F')
add('fc-amp-label', 'graph TD\n  A -->|fan| B & C & D')
add('fc-amp-trailing', 'graph TD\n  A & --> B')
add('fc-semicolons', 'graph TD; A-->B; B-->C; C-->A;')
add('fc-comments', 'graph TD\n  %% a comment\n  A --> B %% trailing comment\n  B --> C;%% after semicolon\n  C["text with %% inside"] --> D')
add('fc-quoted-semicolon', 'graph TD\n  A["a; b; c"] --> B["x %% y"]')
add('fc-ignored-statements', 'graph TD\n  classDef green fill:#9f6\n  class A green\n  style A fill:#f9f\n  linkStyle 0 stroke:#ff3\n  click A callback\n  direction LR\n  A --> B')
add('fc-style-class-shorthand', 'graph LR\n  A:::green --> B:::red\n  C[Label]:::blue --> D\n  E:::x-->F\n  G:::-->H\n  I:::a-b-c --> J')
add('fc-redeclare', 'graph TD\n  A --> B\n  A[First label]\n  A(Second label)\n  B{Decide}')
add('fc-self-loop', 'graph TD\n  A --> A\n  B[Box] -->|again| B\n  B --> C')
add('fc-self-loop-lr', 'graph LR\n  A -->|retry| A\n  A --> B\n  B -.->|loop| B\n  B ==> B')
add('fc-self-loop-styles', 'graph TD\n  A -.-> A\n  B ==> B\n  C --o C\n  D --x D')
add('fc-self-loop-small', 'graph TD\n  x --> x')
add('fc-cycle', 'graph TD\n  A --> B --> C --> A')
add('fc-cycle-lr', 'graph LR\n  A --> B --> C --> A\n  C -->|back| B')
add('fc-skip-edges', 'graph TD\n  A --> B --> C --> D\n  A --> D\n  A -->|skip| C\n  B --> D')
add('fc-skip-edges-lr', 'graph LR\n  A --> B --> C --> D\n  A --> D\n  A -->|skip| C\n  B -.-> D')
add('fc-back-edges-heads', 'graph TD\n  A --> B --> C\n  C --o A\n  C --x A\n  C <--> A\n  C --- A')
add('fc-back-edges-heads-lr', 'graph LR\n  A --> B --> C\n  C --o A\n  C --x A\n  C <--> A\n  C --- A')
add('fc-multi-edge', 'graph TD\n  A --> B\n  A --> B\n  A -->|again| B')
add('fc-diamond-fan', 'graph TD\n  A --> B & C & D & E\n  B & C & D & E --> F')
add('fc-crossings', 'graph TD\n  A --> D\n  B --> C\n  A --> C\n  B --> E\n  C --> F\n  D --> F\n  E --> G\n  A --> G')
add('fc-wide-rank', `graph TD\n  root --> ${Array.from({ length: 12 }, (_, i) => `n${i}`).join(' & ')}`)
add('fc-wide-rank-lr', `graph LR\n  root --> ${Array.from({ length: 12 }, (_, i) => `n${i}`).join(' & ')}`)
add('fc-disconnected', 'graph TD\n  A --> B\n  C --> D\n  E\n  F[Lonely]')
add('fc-isolated-only', 'graph LR\n  A\n  B\n  C')
add('fc-reversed-arrow', 'graph TD\n  A <-- B\n  C <-.- D\n  E <== F\n  G <-- H --> I')
add('fc-label-html', 'graph TD\n  A[<b>Bold</b> and <i>italic</i>] --> B[line<br>break<br/>again<br />x]\n  B --> C[Vec<String> <id>]\n  C --> D[<span style="x">styled</span>]\n  D --> E[<unknown>tag</unknown>]\n  E --> F[<b>unclosed]')
add('fc-label-entities', 'graph TD\n  A[&lt;tag&gt; &amp; &quot;q&quot; &apos;a&apos;] --> B[&#65;&#x42;&#X43; &#128512;]\n  B --> C[&amp;lt; double &nbsp; unknown &#0; &#x110000; &#xD800; &;]\n  C --> D[&toString; &valueOf; &__proto__; &constructor;]')
// `;` ends a statement unless it is quoted, so entities only survive inside quotes.
add('fc-label-entities-quoted', 'graph TD\n  A["&lt;tag&gt; &amp; &quot;q&quot; &apos;a&apos;"] --> B["&#65;&#x42;&#X43; &#128512;"]\n  B --> C["&amp;lt; double &nbsp; unknown &#0; &#x110000; &#xD800; &;"]\n  C --> D["&toString; &valueOf; &__proto__; &constructor;"]\n  D -->|"&lt;b&gt;"| E["&#x1F600;&#x1f680;&#9731;&#xFE0F;"]\n  E -- "&amp;x" --> F["&#12345678901;&#x;&#;&#x7F;&#x9f;&#xa0;x"]')
add('fc-label-entities-edge', 'graph LR\n  A -->|&lt;b&gt;| B\n  A -- &amp;x --> C')
add('fc-label-markdown', 'graph TD\n  A["`**bold** and _italic_ and snake_case`"] --> B["`plain`"]\n  B --> C["`__dunder__ *star*`"]\n  C --> D["` `"]')
add('fc-label-quotes', `graph TD\n  A["double quoted"] --> B['single quoted']\n  B --> C["a] b"]\n  C --> D[5" pipe]\n  D --> E[" spaced "]\n  E --> F["unterminated]`)
add('fc-label-backticks', 'graph LR\n  A["plain ` tick"] --> B["two `` ticks"]\n  B --> C[`code`]\n  C --> D["`nested `md` text`"]')
add('fc-label-long-wrap', 'graph TD\n  A[This is a fairly long label that will need to wrap across several lines of text] --> B[Short]\n  B --> C[one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty]')
add('fc-label-long-word', 'graph TD\n  A[averyveryveryveryverylongidentifierwithoutanybreaks] --> B[some_snake_case_identifier_that_is_long]\n  B --> C[path/to/some/deeply/nested/file.name.ext]\n  C --> D[kebab-case-identifier-that-goes-on-and-on]')
add('fc-label-long-word-lr', 'graph LR\n  A[averyveryveryveryverylongidentifierwithoutanybreaks] --> B[x]')
add('fc-edge-label-long', 'graph TD\n  A -->|this edge label is definitely longer than twenty eight columns| B\n  B -->|short| C\n  C -->|中文的边标签很长很长很长很长很长很长很长| D')
add('fc-edge-label-long-lr', 'graph LR\n  A -->|this edge label is definitely longer than twenty eight columns| B\n  B -->|中文的边标签很长很长很长很长很长很长很长| C')
add('fc-edge-label-collide', 'graph TD\n  A --> B & C & D\n  A -->|label one| B\n  A -->|label two| C\n  A -->|label three| D')
add('fc-id-unicode', `graph TD\n  ${CJK} --> ${JP}\n  é --> ${DEVANAGARI}\n  Ünïcödé --> ${ARABIC}`)
add('fc-unicode-labels', `graph TD\n  A[${CJK}] --> B[${JP}]\n  B --> C[${HANGUL_JAMO}]\n  C --> D[${ROCKET} launch]\n  D --> E[${FAMILY} family]\n  E --> F[${FLAG_US}${FLAG_JP} flags]\n  F --> G[caf${E_ACUTE}]\n  G --> H[${DEVANAGARI}]\n  H --> I[${THAI}]\n  I --> J[${ARABIC}]`)
add('fc-unicode-labels-lr', `graph LR\n  A[${CJK}] -->|${JP}| B[${ROCKET}${THUMB_DARK}${HEART}]\n  B -->|${KEYCAP}| C[${FULLWIDTH}]\n  C --> D[${TAG_FLAG} tag]`)
add('fc-unicode-labels-rl', `graph RL\n  A[${CJK}] -->|${JP}| B[${ROCKET} rocket]\n  B -->|edge ${FAMILY}| C[caf${E_ACUTE}]`)
add('fc-unicode-labels-bt', `graph BT\n  A[${CJK}] -->|${JP}| B[${ROCKET} rocket]\n  B --> C[${FLAG_US}]`)
add('fc-zero-width', `graph TD\n  A[a${ZWSP}b${SHY}c] --> B[${ZWSP}]\n  B --> C[${SHY}${SHY}]\n  C -->|${ZWSP}| D`)
add('fc-wide-wrap', `graph TD\n  A[${'漢字'.repeat(20)}] --> B[${ROCKET.repeat(30)}]\n  B --> C[${FAMILY.repeat(10)}]`)
add('fc-wide-wrap-mixed', `graph TD\n  A[ab${'漢'.repeat(12)}cd ef${'字'.repeat(13)}] --> B[x_${'中'.repeat(11)}_y]`)
add('fc-many-lines', 'graph TD\n  A[one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone twentytwo twentythree twentyfour twentyfive twentysix]')
add('fc-nbsp-label', `graph TD\n  A[a${NBSP}b${NBSP}${NBSP}c] --> B[${NBSP}lead]`)
add('fc-malformed-start', 'graph TD\n  --> B\n  A --> B')
add('fc-malformed-link', 'graph TD\n  A --> B ?? C\n  D E')
add('fc-malformed-target', 'graph TD\n  A -->\n  B --> |x|')
add('fc-malformed-amp', 'graph TD\n  A & & B --> C\n  D & --> E')
add('fc-malformed-unclosed-shapes', 'graph TD\n  A(round --> B\n  C{diamond --> D\n  E[[sub --> F\n  G((c --> H\n  I{{h --> J')
add('fc-malformed-many', 'graph LR\n  A --> B\n  ???\n  C ->> D\n  E --> \n  F[ok]')
add('fc-only-warnings', 'graph TD\n  --> --> -->')
add('fc-end-without-subgraph', 'graph TD\n  A --> B\n  end\n  end\n  B --> C')
add('fc-o-x-ids', 'graph LR\n  o --> x\n  x --> o\n  ox --> xo\n  A --o o\n  B --x x\n  C o--o oo')
add('fc-trailing-o', 'graph LR\n  A --o\n  B --oB\n  C --o|lbl| D\n  E --x&F --> G')
add('fc-link-only-arrows', 'graph LR\n  A <> B\n  C <--- D\n  E ---- F\n  G -.-.-> H')
add('fc-inline-lt', 'graph LR\n  A -- text <-- B\n  C -- text\n  D -- t1 -- t2 --> E')
add('fc-numeric-ids', 'graph TD\n  1 --> 2\n  2 --> 3\n  10[Ten] --> 1')

// subgraphs
add('sg-basic', 'graph TD\n  subgraph one\n    a1 --> a2\n  end\n  subgraph two\n    b1 --> b2\n  end\n  a2 --> b1')
add('sg-titled', 'graph TD\n  subgraph S1[First group]\n    A --> B\n  end\n  subgraph "Quoted &amp; title"\n    C --> D\n  end\n  subgraph S3 [Spaced title]\n    E\n  end\n  B --> C\n  D --> E')
add('sg-lr', 'flowchart LR\n  subgraph one\n    a1 --> a2\n  end\n  subgraph two\n    b1 --> b2\n  end\n  a2 --> b1\n  a1 -->|lbl| b2')
add('sg-bt', 'flowchart BT\n  subgraph one\n    a1 --> a2\n  end\n  a2 --> c')
add('sg-rl', 'flowchart RL\n  subgraph one[Group One]\n    a1 -->|x| a2\n  end\n  a2 --> c[Outside]')
add('sg-nested', 'graph TD\n  subgraph outer\n    subgraph inner\n      x --> y\n    end\n    z --> x\n  end\n  start --> z\n  y --> finish')
add('sg-nested-deep', 'graph TD\n  subgraph a\n  subgraph b\n  subgraph c\n  subgraph d\n  subgraph e\n  subgraph f\n  n1 --> n2\n  end\n  end\n  end\n  end\n  end\n  end\n  n0 --> n1')
add('sg-nested-too-deep', 'graph TD\n  subgraph a\n  subgraph b\n  subgraph c\n  subgraph d\n  subgraph e\n  subgraph f\n  subgraph g\n  n1 --> n2\n  end\n  end\n  end\n  end\n  end\n  end\n  end')
add('sg-too-many', `graph TD\n${Array.from({ length: 25 }, (_, i) => `  subgraph s${i}\n    n${i}\n  end`).join('\n')}`)
add('sg-max-groups', `graph LR\n${Array.from({ length: 24 }, (_, i) => `  subgraph s${i}\n    n${i}\n  end`).join('\n')}\n  n0 --> n23`)
add('sg-edge-to-group', 'graph TD\n  subgraph G1\n    A --> B\n  end\n  subgraph G2\n    C\n  end\n  G1 --> G2\n  X --> G1\n  G2 --> Y')
add('sg-edge-to-group-lr', 'graph LR\n  subgraph G1\n    A --> B\n  end\n  G1 -->|into| C\n  C --> G1')
add('sg-empty', 'graph TD\n  subgraph empty\n  end\n  A --> B')
add('sg-empty-referenced', 'graph TD\n  subgraph empty\n  end\n  A --> empty')
add('sg-empty-nested', 'graph TD\n  subgraph outer\n    subgraph inner\n    end\n  end\n  A --> B')
add('sg-cross-nesting', 'graph TD\n  subgraph outer\n    subgraph inner1\n      a --> b\n    end\n    subgraph inner2\n      c --> d\n    end\n  end\n  b --> c\n  a --> d\n  d --> e\n  e --> a')
add('sg-direction', 'graph TD\n  subgraph s\n    direction LR\n    a --> b\n  end')
add('sg-unicode', `graph TD\n  subgraph ${CJK}\n    A[${JP}] --> B[${ROCKET}]\n  end\n  subgraph g2[${FAMILY} ${FLAG_US}]\n    C\n  end\n  B --> C`)
add('sg-long-title', 'graph TD\n  subgraph s[A subgraph title that is much longer than its content]\n    a\n  end')
add('sg-unclosed', 'graph TD\n  subgraph open\n    A --> B\n  B --> C')
add('sg-node-before-decl', 'graph TD\n  A --> B\n  subgraph S\n    A\n  end\n  B --> A')
add('sg-same-id-node', 'graph TD\n  S --> T\n  subgraph S\n    a --> b\n  end')
add('sg-multiple-proxy', 'graph TD\n  subgraph X\n    a\n  end\n  subgraph X\n    b\n  end\n  X --> c')
add('sg-label-only-brackets', 'graph TD\n  subgraph [Title]\n    a\n  end\n  subgraph id[]\n    b\n  end\n  subgraph "unterminated\n    c\n  end')
add('sg-self-edge-group', 'graph TD\n  subgraph G\n    a --> a\n  end\n  G --> G')

// ----------------------------------------------------------------- state
add('st-basic', 'stateDiagram-v2\n  [*] --> Still\n  Still --> [*]\n  Still --> Moving\n  Moving --> Still\n  Moving --> Crash\n  Crash --> [*]')
add('st-v1', 'stateDiagram\n  [*] --> A\n  A --> B: go\n  B --> [*]')
add('st-labels', 'stateDiagram-v2\n  A --> B: first transition\n  B --> C : second\n  C --> A:back\n  A --> C: &lt;entity&gt;')
add('st-desc', 'stateDiagram-v2\n  s1: The first state\n  s2 : Second &amp; more\n  s1 --> s2')
add('st-decl-as', 'stateDiagram-v2\n  state "A long state name" as long1\n  state "Plain"\n  [*] --> long1\n  long1 --> Plain')
add('st-choice', 'stateDiagram-v2\n  state check <<choice>>\n  [*] --> check\n  check --> Yes: if x\n  check --> No: else\n  state fork1 <<fork>>\n  Yes --> fork1')
add('st-composite', 'stateDiagram-v2\n  [*] --> First\n  state First {\n    [*] --> second\n    second --> [*]\n  }\n  First --> Last\n  --\n  Last --> [*]')
add('st-notes', 'stateDiagram-v2\n  A --> B\n  note right of A : single line note\n  note left of B\n    multi line\n    note body\n  end note\n  B --> C')
add('st-direction', 'stateDiagram-v2\n  direction LR\n  [*] --> A\n  A --> B\n  B --> [*]')
add('st-direction-rl', 'stateDiagram-v2\n  direction RL\n  [*] --> A\n  A --> B: lbl\n  B --> [*]')
add('st-direction-bt', 'stateDiagram-v2\n  direction BT\n  [*] --> A\n  A --> B\n  B --> [*]')
add('st-ignored', 'stateDiagram-v2\n  classDef bad fill:#f00\n  class A bad\n  hide empty description\n  scale 350 width\n  A --> B')
add('st-chain', 'stateDiagram-v2\n  A --> B --> C --> D')
add('st-chain-label', 'stateDiagram-v2\n  A --> B: one --> C: two')
add('st-style-tags', 'stateDiagram-v2\n  A:::red --> B:::green\n  C:::x-y --> D\n  E:::: weird')
add('st-self', 'stateDiagram-v2\n  A --> A: tick\n  A --> B')
add('st-cycle', 'stateDiagram-v2\n  A --> B\n  B --> C\n  C --> A\n  C --> [*]')
add('st-unicode', `stateDiagram-v2\n  [*] --> 待機\n  待機 --> 実行: 開始 ${ROCKET}\n  実行 --> [*]\n  state "${FAMILY} family" as fam\n  実行 --> fam`)
add('st-bad-middle', 'stateDiagram-v2\n  A --> B\n  this is garbage\n  B --> C')
add('st-bad-last', 'stateDiagram-v2\n  A --> B\n  B --> C\n  this is garbage')
add('st-bad-last-crlf', 'stateDiagram-v2\r\n  A --> B\r\n  garbage line here\r\n\r\n')
add('st-bad-decl', 'stateDiagram-v2\n  state "unterminated as x\n  A --> B')
add('st-bad-transition', 'stateDiagram-v2\n  --> B')
add('st-bad-chain', 'stateDiagram-v2\n  A --> B x --> C')
add('st-bare', 'stateDiagram-v2\n  Idle\n  Busy\n  Idle --> Busy')
add('st-desc-empty', 'stateDiagram-v2\n  A:\n  A --> B')
add('st-arrow-variants', 'stateDiagram-v2\n  A ---> B\n  C -->> D\n  E-->F')
add('st-stereo-noid', 'stateDiagram-v2\n  state <<choice>>\n  A --> B')
add('st-state-empty', 'stateDiagram-v2\n  state\n  state {\n  A --> B')
add('st-note-unterminated', 'stateDiagram-v2\n  A --> B\n  note left of A\n  never ends')

// ----------------------------------------------------------------- class
add(
  'cls-basic',
  'classDiagram\n  class Animal {\n    +String name\n    +int age\n    +makeSound() void\n  }\n  class Dog\n  Animal <|-- Dog\n  Dog : +bark() void\n  Dog : +String breed',
)
add(
  'cls-relations',
  'classDiagram\n  A <|-- B\n  C --|> D\n  E <|.. F\n  G ..|> H\n  I *-- J\n  K --* L\n  M o-- N\n  O --o P\n  Q <-- R\n  S --> T\n  U <.. V\n  W ..> X\n  Y -- Z\n  AA .. BB',
)
add('cls-relations-lr', 'classDiagram\n  direction LR\n  A <|-- B\n  C *-- D\n  E o-- F\n  G ..> H\n  B --> D : uses')
add('cls-relations-rl', 'classDiagram\n  direction RL\n  A <|-- B\n  C *-- D\n  B --> D : uses')
add('cls-relations-bt', 'classDiagram\n  direction BT\n  A <|-- B\n  C *-- D\n  B --> D : uses')
add('cls-cardinality', 'classDiagram\n  Customer "1" --> "*" Ticket : buys\n  Student "0..*" --o "1..*" Course\n  A "1" -- B\n  C -- "many" D : rel')
add('cls-cardinality-ok', 'classDiagram\n  Customer "1" --> "*" Ticket : buys\n  A "1" -- B\n  C -- "many" D : rel\n  Student "1" --o "n" Course\n  X "0" <|-- "1" Y : "&amp;"')
add('cls-labels-quoted', 'classDiagram\n  A --> B : has "&amp;" holds\n  B --> C : x\n  A : +field "&lt;T&gt;"')
add('cls-annotations', 'classDiagram\n  class Shape {\n    <<interface>>\n    +draw()\n  }\n  <<abstract>> Base\n  class Color\n  <<enumeration>> Color\n  Color : RED\n  Color : GREEN')
add('cls-generics', 'classDiagram\n  class Square~Shape~ {\n    +List~int~ ids\n    +getMap() Map~String, List~T~~\n  }\n  Square~Shape~ <|-- Big')
add('cls-many-members', `classDiagram\n  class Big {\n${Array.from({ length: 12 }, (_, i) => `    +int field${i}`).join('\n')}\n${Array.from({ length: 11 }, (_, i) => `    +method${i}()`).join('\n')}\n  }`)
add('cls-ignored', 'classDiagram\n  note "a note"\n  note for A "x"\n  callback A "cb"\n  click A href "x"\n  link A "url"\n  style A fill:#f9f\n  cssClass "A" styleClass\n  classDef foo fill:#f00\n  namespace N {\n  class A\n  }\n  A --> B')
add('cls-style-tags', 'classDiagram\n  class A:::someclass\n  B:::x --> C\n  A --> B')
add('cls-o-ids', 'classDiagram\n  foo --> bar\n  photo o-- album\n  zoo --o go\n  o -- x')
add('cls-labels', 'classDiagram\n  A --> B : has &amp; holds\n  B --> C :\n  C ..> D : <<use>>')
add('cls-self', 'classDiagram\n  Node --> Node : next\n  Node --> Tree')
add('cls-unicode', `classDiagram\n  class 動物 {\n    +String 名前\n    +鳴く() ${ROCKET}\n  }\n  動物 <|-- 犬\n  犬 : +${FAMILY} family`)
add('cls-bad-middle', 'classDiagram\n  A --> B\n  this is not valid\n  B --> C')
add('cls-bad-last', 'classDiagram\n  A --> B\n  this is not valid')
add('cls-bad-class', 'classDiagram\n  class Two Words\n  A --> B')
add('cls-bad-annotation', 'classDiagram\n  <<interface\n  A --> B')
add('cls-member-only', 'classDiagram\n  Duck : +swim()\n  Duck : +quack()\n  Duck : +int age')
add('cls-member-annotation', 'classDiagram\n  class X {\n    <<service>>\n    <<broken\n    +run()\n  }')
add('cls-unclosed-block', 'classDiagram\n  class X {\n    +a\n    +b()')
add('cls-long-names', 'classDiagram\n  class AVeryLongClassNameThatExceedsTheWrapWidth {\n    +aVeryLongAttributeNameThatAlsoExceedsTheWidth\n  }\n  AVeryLongClassNameThatExceedsTheWrapWidth --> B')

// -------------------------------------------------------------------- ER
add(
  'er-basic',
  'erDiagram\n  CUSTOMER ||--o{ ORDER : places\n  ORDER ||--|{ LINE-ITEM : contains\n  CUSTOMER }|..|{ DELIVERY-ADDRESS : uses',
)
add(
  'er-attributes',
  'erDiagram\n  CUSTOMER {\n    string name\n    string custNumber PK\n    string sector "comment here"\n  }\n  ORDER {\n    int orderNumber PK\n    string deliveryAddress FK "the address"\n  }\n  CUSTOMER ||--o{ ORDER : places',
)
add('er-cardinalities', 'erDiagram\n  A |o--o| B : zero-one\n  C ||--|| D : one\n  E }o--o{ F : zero-many\n  G }|--|{ H : one-many\n  I |o..o{ J : dotted\n  K ||..|| L')
add('er-alias', 'erDiagram\n  p[Person] {\n    string name\n  }\n  c["Car &amp; truck"]\n  p ||--o{ c : drives')
add('er-alias-ok', 'erDiagram\n  p[Person] {\n    string name\n  }\n  c["Car"]\n  d[<b>Dog</b>]\n  p ||--o{ c : drives\n  p }|..|| d : "walks &amp; feeds"\n  q[] ||--|| p')
add('er-many-attrs', `erDiagram\n  WIDE {\n${Array.from({ length: 11 }, (_, i) => `    int col${i}`).join('\n')}\n  }`)
add('er-entity-only', 'erDiagram\n  LONELY\n  OTHER {\n  }')
add('er-no-label', 'erDiagram\n  A ||--o{ B\n  B }o--|| C :')
add('er-quoted-label', 'erDiagram\n  A ||--o{ B : "has many"\n  B ||--|| C : "<b>bold</b>"')
add('er-unicode', `erDiagram\n  顧客 ||--o{ 注文 : 発注\n  注文 {\n    string 品名 ${ROCKET}\n  }`)
add('er-bad-op', 'erDiagram\n  A ||-x-o{ B : bad\n  C ||--o{ D : fine')
add('er-bad-tokens', 'erDiagram\n  A ||--o{ B C : extra')
add('er-bad-last', 'erDiagram\n  A ||--o{ B : ok\n  totally broken line here')
add('er-bad-decl', 'erDiagram\n  TWO WORDS')
add('er-nonascii-op', 'erDiagram\n  A ||——o{ B : dash')
add('er-direction', 'erDiagram\n  direction LR\n  A ||--o{ B : x')
add('er-unclosed-block', 'erDiagram\n  A {\n    int x')

// -------------------------------------------------------------- sequence
add(
  'seq-basic',
  'sequenceDiagram\n  participant A as Alice\n  participant B as Bob\n  A->>B: Hello Bob\n  B-->>A: Hi Alice\n  A-xB: lost\n  B--xA: lost reply\n  A-)B: async\n  B--)A: async reply\n  A->B: open\n  B-->A: open dashed',
)
add('seq-actors', 'sequenceDiagram\n  actor U as User\n  participant S as "Server &amp; co"\n  U->>S: login\n  S-->>U: token')
add('seq-implicit', 'sequenceDiagram\n  Alice->>John: Hello John, how are you?\n  John-->>Alice: Great!\n  Alice-)John: See you later!')
add('seq-self', 'sequenceDiagram\n  A->>A: think\n  A->>B: tell\n  B->>B: process it carefully\n  B-xB: oops\n  B-->>B')
add('seq-self-last', 'sequenceDiagram\n  A->>B: x\n  B->>B: self message on the last participant')
add('seq-notes', 'sequenceDiagram\n  participant A\n  participant B\n  participant C\n  Note over A: over one\n  Note over A,B: spanning two\n  Note over C,A: reversed span\n  Note left of A: left note\n  Note right of C: right note\n  Note right of A: right of first\n  Note left of C: left of last\n  note over B : lower case')
add('seq-notes-long', 'sequenceDiagram\n  A->>B: hi\n  Note over A: a note that is quite a bit longer than the gap\n  Note right of B: another rather long note text here\n  Note left of A: and one on the left side too')
add(
  'seq-blocks',
  'sequenceDiagram\n  A->>B: start\n  loop Every minute\n    A->>B: ping\n  end\n  alt is ok\n    B->>A: ok\n  else is not ok\n    B->>A: fail\n  end\n  opt optional\n    A->>B: maybe\n  end\n  par first\n    A->>B: p1\n  and second\n    A->>B: p2\n  end\n  critical crit\n    A->>B: c\n  option fallback\n    A->>B: f\n  end\n  break stop\n    A->>B: b\n  end',
)
add('seq-rect-box', 'sequenceDiagram\n  box Aqua Group\n  participant A\n  participant B\n  end\n  rect rgb(200,150,255)\n    A->>B: inside rect\n  end\n  else stray\n  and stray\n  end\n  A->>B: after')
add('seq-autonumber', 'sequenceDiagram\n  autonumber\n  A->>B: first\n  B-->>A: second\n  A->>A: self\n  A->>B\n  Note over A: notes are not numbered')
add('seq-activation', 'sequenceDiagram\n  A->>+B: activate\n  B-->>-A: deactivate\n  activate A\n  deactivate A\n  A->>+-B: both\n  create participant C\n  destroy C\n  title My title\n  accTitle: t\n  accDescr: d\n  links A: {}\n  link A: x\n  properties A: {}')
add('seq-activation-ok', 'sequenceDiagram\n  A->>+B: activate\n  B-->>-A: deactivate\n  activate A\n  deactivate A\n  A->>+-B: both\n  A->>++B: twice\n  create participant C\n  destroy C\n  title My title\n  links A: {}\n  link A: x\n  properties A: {}\n  B->>C: hi')
add('seq-entities-quoted', 'sequenceDiagram\n  participant A as "<i>It</i> &lt;x&gt;"\n  A->>B: "&lt;b&gt; &amp; more"\n  Note over A: "&quot;q&quot;"\n  loop "&amp;-loop"\n  A->>B: x\n  end')
add('seq-participant-relabel', 'sequenceDiagram\n  A->>B: first\n  participant A as Alice\n  participant B as\n  participant C as  spaced  \n  A->>C: x')
add('seq-long-labels', 'sequenceDiagram\n  participant A as A participant with a really long display name\n  participant B\n  A->>B: a message whose label is much longer than the default gap between lifelines\n  B-->>A: short')
add('seq-unicode', `sequenceDiagram\n  participant 客 as 顧客 ${ROCKET}\n  participant S as サーバー\n  客->>S: こんにちは ${FAMILY}\n  S-->>客: ${FLAG_US} ok ${HEART}\n  Note over 客,S: ${CJK}`)
add('seq-divider-long', `sequenceDiagram\n  A->>B: x\n  loop ${'very long loop condition '.repeat(4)}\n  A->>B: y\n  end`)
add('seq-entities', 'sequenceDiagram\n  A->>B: &lt;b&gt;html&lt;/b&gt; &amp; more\n  Note over A: &quot;quoted&quot;\n  loop &amp;-loop\n  end')
add('seq-bad-middle', 'sequenceDiagram\n  A->>B: hi\n  this is not a message\n  B->>A: bye')
add('seq-bad-last', 'sequenceDiagram\n  A->>B: hi\n  B->>A: bye\n  half typed mess')
add('seq-bad-note', 'sequenceDiagram\n  A->>B: hi\n  Note somewhere: x\n  B->>A: y')
add('seq-bad-note-noid', 'sequenceDiagram\n  A->>B: hi\n  Note over : x')
add('seq-bad-participant', 'sequenceDiagram\n  participant\n  A->>B: x')
add('seq-no-target', 'sequenceDiagram\n  A->>: x\n  A->>B: y')
add('seq-no-source', 'sequenceDiagram\n  ->>B: x')
add('seq-participants-only', 'sequenceDiagram\n  participant A\n  participant B as Bob\n  actor C')
add('seq-reorder', 'sequenceDiagram\n  participant C\n  participant A\n  A->>C: right to left?\n  C->>A: back')
add('seq-far', 'sequenceDiagram\n  participant A\n  participant B\n  participant C\n  participant D\n  A->>D: far message spanning everything\n  D-->>A: and back\n  B->>C: near')
add('seq-ops-ordering', 'sequenceDiagram\n  A-->>B: dashed arrow\n  A--xB: dashed cross\n  A--)B: dashed open\n  A-->B: dashed\n  A->>B: arrow\n  A-xB: cross\n  A-)B: open\n  A->B: solid')
add('seq-message-colons', 'sequenceDiagram\n  A->>B: time is 10:30: late\n  A->>B:\n  A->>B :  spaced  ')
add('seq-alias-only-label', 'sequenceDiagram\n  participant A as <b>Bold</b> &amp; `md`\n  A->>A: x')
add('seq-semicolons', 'sequenceDiagram; A->>B: one; B->>A: two %% comment')
add('seq-end-unbalanced', 'sequenceDiagram\n  end\n  end\n  A->>B: x\n  else nothing')

// ------------------------------------------------------------------ large
{
  const chain = Array.from({ length: 60 }, (_, i) => `  n${i}[Node number ${i}] --> n${i + 1}[Node number ${i + 1}]`).join('\n')
  add('large-chain-td', `graph TD\n${chain}`)
  add('large-chain-lr', `graph LR\n${chain}`)
}
{
  const lines = []
  for (let r = 0; r < 6; r++) for (let c = 0; c < 6; c++) {
    if (c < 5) lines.push(`  r${r}c${c} --> r${r}c${c + 1}`)
    if (r < 5) lines.push(`  r${r}c${c} --> r${r + 1}c${c}`)
  }
  add('large-grid-td', `graph TD\n${lines.join('\n')}`)
  add('large-grid-lr', `graph LR\n${lines.join('\n')}`)
  add('large-grid-rl', `graph RL\n${lines.join('\n')}`)
}
{
  const lines = []
  let seed = 7
  const rnd = (n) => {
    seed = (seed * 1103515245 + 12345) & 0x7fffffff
    return seed % n
  }
  for (let i = 0; i < 200; i++) lines.push(`  v${rnd(60)} -->${rnd(4) === 0 ? `|e${i}|` : ''} v${rnd(60)}`)
  add('large-random-dag-td', `graph TD\n${lines.join('\n')}`)
  add('large-random-dag-lr', `graph LR\n${lines.join('\n')}`)
}
add('cap-nodes-128', `graph TD\n${Array.from({ length: 128 }, (_, i) => `  n${i}`).join('\n')}\n  n0 --> n127`)
add('cap-nodes-129', `graph TD\n${Array.from({ length: 129 }, (_, i) => `  n${i}`).join('\n')}`)
add('cap-nodes-129-last-line', `graph LR\n${Array.from({ length: 128 }, (_, i) => `  n${i}`).join('\n')}\n  extra --> n0`)
add('cap-edges-512', `graph TD\n${Array.from({ length: 512 }, (_, i) => `  a${i % 8} --> b${i % 5}`).join('\n')}`)
add('cap-edges-513', `graph TD\n${Array.from({ length: 513 }, (_, i) => `  a${i % 8} --> b${i % 5}`).join('\n')}`)
add('cap-edges-amp', `graph TD\n  ${Array.from({ length: 30 }, (_, i) => `a${i}`).join(' & ')} --> ${Array.from({ length: 30 }, (_, i) => `b${i}`).join(' & ')}`)
add('cap-state-nodes', `stateDiagram-v2\n${Array.from({ length: 130 }, (_, i) => `  s${i} --> s${i + 1}`).join('\n')}`)
add('cap-class-edges', `classDiagram\n${Array.from({ length: 513 }, (_, i) => `  A${i % 7} --> B${i % 9}`).join('\n')}`)
add('cap-er-edges', `erDiagram\n${Array.from({ length: 513 }, (_, i) => `  A${i % 7} ||--o{ B${i % 9} : r`).join('\n')}`)
add('cap-seq-participants', `sequenceDiagram\n${Array.from({ length: 129 }, (_, i) => `  participant P${i}`).join('\n')}`)
add('cap-seq-items', `sequenceDiagram\n${Array.from({ length: 513 }, (_, i) => `  A->>B: m${i}`).join('\n')}`)
add('canvas-too-big-seq', `sequenceDiagram\n${Array.from({ length: 128 }, (_, i) => `  participant P${i} as Participant number ${i} long`).join('\n')}\n${Array.from({ length: 400 }, (_, i) => `  P${i % 128}->>P${(i * 37) % 128}: message ${i} with some text`).join('\n')}`)
{
  // 64 wide columns by 63 stacked four-line boxes with self-loops: over the 2^21-cell canvas cap, so refused.
  const lbl = (i) => `node ${i} with a label long enough to wrap over four full lines of text in the box`
  const chain = Array.from({ length: 63 }, (_, i) => `  c${i}[${lbl(i)}] -->|edge label number ${i} wide text| c${i + 1}`)
  const fan = Array.from({ length: 63 }, (_, i) => `  c0 --> f${i}[${lbl(i)}]\n  f${i} --> f${i}`)
  add('canvas-too-big-fc', `graph LR\n${chain.join('\n')}\n${fan.join('\n')}`)
}
add('large-seq', `sequenceDiagram\n${Array.from({ length: 12 }, (_, i) => `  participant P${i}`).join('\n')}\n${Array.from({ length: 80 }, (_, i) => `  P${i % 12}${['->>', '-->>', '-x', '--)'][i % 4]}P${(i * 5 + 1) % 12}: msg ${i}`).join('\n')}`)
add('large-class', `classDiagram\n${Array.from({ length: 30 }, (_, i) => `  C${i} ${['<|--', '*--', 'o--', '-->', '..>', '..|>'][i % 6]} C${(i * 7 + 3) % 30} : r${i}\n  C${i} : +int f${i}\n  C${i} : +m${i}()`).join('\n')}`)
add('large-er', `erDiagram\n${Array.from({ length: 25 }, (_, i) => `  E${i} ${['||--o{', '}o--||', '|o..|{', '}|--o|'][i % 4]} E${(i * 3 + 1) % 25} : rel${i}`).join('\n')}`)
add('large-state', `stateDiagram-v2\n  [*] --> s0\n${Array.from({ length: 40 }, (_, i) => `  s${i} --> s${(i * 3 + 1) % 40}: t${i}`).join('\n')}\n  s39 --> [*]`)
add('large-subgraphs', `graph TD\n${Array.from({ length: 6 }, (_, g) => `  subgraph G${g}[Group ${g}]\n${Array.from({ length: 5 }, (_, i) => `    g${g}n${i} --> g${g}n${(i + 1) % 5}`).join('\n')}\n  end`).join('\n')}\n${Array.from({ length: 10 }, (_, i) => `  g${i % 6}n${i % 5} --> g${(i + 2) % 6}n${(i + 3) % 5}`).join('\n')}`)

// --------------------------------------------------------------- streaming
{
  const full = 'flowchart TD\n  A[Parse source] --> B{Supported?}\n  B -->|yes| C[Lay out]\n  B -->|no| D[Framed source]\n  C --> E[Unicode art]\n  D --> E'
  for (let n = 1; n <= full.length; n += 3) add(`stream-fc-${n}`, full.slice(0, n))
  const seq = 'sequenceDiagram\n  participant A as Alice\n  A->>B: Hello\n  loop retry\n    B-->>A: Hi\n  end\n  Note over A,B: done'
  for (let n = 1; n <= seq.length; n += 4) add(`stream-seq-${n}`, seq.slice(0, n))
  const st = 'stateDiagram-v2\n  [*] --> Idle\n  Idle --> Busy: start\n  state Busy {\n    a --> b\n  }\n  Busy --> [*]'
  for (let n = 1; n <= st.length; n += 4) add(`stream-state-${n}`, st.slice(0, n))
  const cls = 'classDiagram\n  class A {\n    +int x\n    +run()\n  }\n  A <|-- B : extends\n  B "1" --> "*" C'
  for (let n = 1; n <= cls.length; n += 4) add(`stream-class-${n}`, cls.slice(0, n))
  const er = 'erDiagram\n  CUSTOMER ||--o{ ORDER : places\n  ORDER {\n    int id PK\n  }'
  for (let n = 1; n <= er.length; n += 3) add(`stream-er-${n}`, er.slice(0, n))
}

// -------------------------------------------------------------------- fuzz
{
  let seed = 20260811
  const rnd = (n) => {
    seed = (seed * 1103515245 + 12345) & 0x7fffffff
    return seed % n
  }
  const pick = (arr) => arr[rnd(arr.length)]
  const ids = ['A', 'B', 'C', 'D', 'E', 'node1', 'x', 'o', 'n_2', '中', 'é', 'Z9']
  const texts = ['', 'label', 'two words', 'a much longer label that will wrap around', CJK, `${ROCKET} go`, FAMILY, FLAG_US, `caf${E_ACUTE}`, DEVANAGARI, '&lt;x&gt;', '<b>b</b>', '"q"', '`**md**`', 'snake_case_name', 'x'.repeat(30), `${ZWSP}z`, `a${NBSP}b`, HANGUL_JAMO]
  const shapes = [(t) => `[${t}]`, (t) => `(${t})`, (t) => `((${t}))`, (t) => `{${t}}`, (t) => `{{${t}}}`, (t) => `[[${t}]]`, (t) => `([${t}])`, (t) => `[(${t})]`, (t) => `>${t}]`, () => '', () => '', (t) => `[${t}`, (t) => `["${t}"]`]
  const links = ['-->', '---', '-.->', '-.-', '==>', '===', '<-->', '<--', 'o--o', 'x--x', '--o', '--x', '--->', '-->|lbl|', '-- txt -->', '-. txt .->', '== txt ==>', '->', '-->|', '<>', '&']
  const fcHeads = ['graph TD', 'graph LR', 'graph BT', 'graph RL', 'flowchart TB', 'flowchart LR', 'graph']
  const node = () => `${pick(ids)}${pick(shapes)(pick(texts))}${rnd(8) === 0 ? ':::cls' : ''}`
  for (let k = 0; k < 320; k++) {
    const lines = [pick(fcHeads)]
    const n = 1 + rnd(9)
    let depth = 0
    for (let i = 0; i < n; i++) {
      const r = rnd(20)
      if (r === 0) {
        lines.push(`subgraph ${pick(ids)}${rnd(2) ? `[${pick(texts)}]` : ''}`)
        depth++
      } else if (r === 1 && depth > 0) {
        lines.push('end')
        depth--
      } else if (r === 2) {
        lines.push(pick(['classDef a fill:#f00', 'style A fill:#0f0', '%% comment', 'direction LR', 'click A cb', '???', '']))
      } else {
        let st = node()
        const hops = rnd(3)
        for (let h = 0; h < hops + 1; h++) st += ` ${pick(links).replace('lbl', pick(texts)).replace('txt', pick(texts) || 't')} ${node()}${rnd(6) === 0 ? ` & ${node()}` : ''}`
        lines.push(st)
      }
    }
    add(`fuzz-fc-${k}`, lines.join(rnd(5) === 0 ? ';' : '\n  '))
  }
  const stLines = () => {
    const a = pick([...ids, '[*]'])
    const b = pick([...ids, '[*]'])
    return pick([
      `${a} --> ${b}`,
      `${a} --> ${b}: ${pick(texts)}`,
      `${pick(ids)}: ${pick(texts)}`,
      `state "${pick(texts)}" as ${pick(ids)}`,
      `state ${pick(ids)} <<choice>>`,
      `state ${pick(ids)} {`,
      '}',
      `note right of ${pick(ids)}: ${pick(texts)}`,
      'direction LR',
      `${a} --> ${b} --> ${pick(ids)}`,
      `${pick(ids)}:::c --> ${pick(ids)}`,
      pick(ids),
      'garbage here',
    ])
  }
  for (let k = 0; k < 130; k++) {
    const lines = ['stateDiagram-v2']
    for (let i = 0, n = 1 + rnd(8); i < n; i++) lines.push(stLines())
    add(`fuzz-state-${k}`, lines.join('\n  '))
  }
  const clsLine = () =>
    pick([
      `${pick(ids)} ${pick(['<|--', '--|>', '<|..', '..|>', '*--', '--*', 'o--', '--o', '<--', '-->', '<..', '..>', '--', '..'])} ${pick(ids)}`,
      `${pick(ids)} "${pick(['1', '*', '0..1'])}" --> "${pick(['1', 'n'])}" ${pick(ids)} : ${pick(texts)}`,
      `${pick(ids)} : +${pick(['int x', 'run()', 'List~T~ items', pick(texts)])}`,
      `class ${pick(ids)}`,
      `class ${pick(ids)} {\n    +field\n    +method()\n    <<interface>>\n  }`,
      `<<${pick(['interface', 'abstract'])}>> ${pick(ids)}`,
      'direction LR',
      'note "x"',
      'bogus statement',
    ])
  for (let k = 0; k < 130; k++) {
    const lines = ['classDiagram']
    for (let i = 0, n = 1 + rnd(8); i < n; i++) lines.push(clsLine())
    add(`fuzz-class-${k}`, lines.join('\n  '))
  }
  const card = () => pick(['|o', 'o|', '||', '}o', 'o{', '}|', '|{', 'xx'])
  const erLine = () =>
    pick([
      `${pick(ids)} ${card()}${pick(['--', '..'])}${card()} ${pick(ids)} : ${pick(texts)}`,
      `${pick(ids)} ${card()}--${card()} ${pick(ids)}`,
      `${pick(ids)} {\n    string name PK "c"\n    int ${pick(['a', 'b'])}\n  }`,
      `${pick(ids)}[${pick(texts)}]`,
      pick(ids),
      'two words here',
    ])
  for (let k = 0; k < 100; k++) {
    const lines = ['erDiagram']
    for (let i = 0, n = 1 + rnd(7); i < n; i++) lines.push(erLine())
    add(`fuzz-er-${k}`, lines.join('\n  '))
  }
  const seqLine = () =>
    pick([
      `${pick(ids)}${pick(['->>', '-->>', '-x', '--x', '-)', '--)', '->', '-->'])}${pick(['', '+', '-'])}${pick(ids)}: ${pick(texts)}`,
      `${pick(ids)}->>${pick(ids)}`,
      `participant ${pick(ids)}${rnd(2) ? ` as ${pick(texts) || 'x'}` : ''}`,
      `actor ${pick(ids)}`,
      `Note ${pick(['over', 'left of', 'right of'])} ${pick(ids)}${rnd(2) ? `,${pick(ids)}` : ''}: ${pick(texts)}`,
      `${pick(['loop', 'alt', 'opt', 'par', 'critical', 'break', 'rect', 'box'])} ${pick(texts)}`,
      pick(['else x', 'and y', 'option z', 'end', 'end', 'autonumber', 'activate A', 'title T']),
      'garbage line',
    ])
  for (let k = 0; k < 160; k++) {
    const lines = ['sequenceDiagram']
    for (let i = 0, n = 1 + rnd(10); i < n; i++) lines.push(seqLine())
    add(`fuzz-seq-${k}`, lines.join('\n  '))
  }
}

writeFileSync(outPath, `[\n${cases.map((c) => JSON.stringify(c)).join(',\n')}\n]\n`)
console.log(`${cases.length} cases`)
