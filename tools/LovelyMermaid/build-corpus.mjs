// Historical Grok inputs are reused without altering either their original corpus or capture.
import {readFileSync, writeFileSync} from 'node:fs';
const base = JSON.parse(readFileSync(new URL('../../tests/PiSharp.InteractiveParity.Tests/Mermaid/mermaid-corpus.json', import.meta.url)));
const additions = [
  ['lovely-pie', 'pie title Budget\n"Compute" : 60\n"Storage" : 25\n"Network" : 15'],
  ['lovely-pie-data', 'pie showData title Sales\n"A" : 1\n"B" : 2\n"C" : 3'],
  ['lovely-pie-zero', 'pie\n"Empty":0\n"Also empty":0'],
  ['lovely-pie-sliver', 'pie showData\n"Tiny":1\n"Huge":999999999'],
  ['lovely-pie-invalid', 'pie\n"Negative":-2\nBad:NaN\n"Okay":3\nInf:Infinity'],
  ['lovely-pie-number-formats', 'pie showData\n"Hex":0xff\n"Binary":0b11\n"Empty":\n"Exponent":1e2'],
  ['lovely-mindmap', 'mindmap\n  root((Root))\n    A[One]\n      B(Two)\n    C{Three}'],
  ['lovely-mindmap-roots', 'mindmap\nOne\n  A\nTwo\n  B\n    C'],
  ['lovely-mindmap-classes', 'mindmap\n  root(Root)\n    ::icon(fa fa-book)\n    A:::hot\n    B'],
  ['lovely-timeline', 'timeline\ntitle History\nsection Early\n2000 : First : Second\n2001\nsection Later\n2002 : Third\n: Continued'],
  ['lovely-timeline-bare', 'timeline\n2000\n2001\n: Event\n2002'],
  ['lovely-timeline-invalid', 'timeline\n: Orphan\nsection Only'],
  ['lovely-git', 'gitGraph\ncommit id:"a"\nbranch feature\ncommit id:"b"\ncheckout main\ncommit id:"c"\nmerge feature tag:"v1"'],
  ['lovely-git-quoted', 'gitGraph:\ncommit\nbranch "feat x"\ncommit id:"worked"\ncheckout "main"\nmerge "feat x"'],
  ['lovely-git-invalid', 'gitGraph\ncheckout missing\nmerge missing\nbranch same\nbranch same\nunknown\ncommit'],
  ['lovely-git-crossing', 'gitGraph\ncommit id:"base"\nbranch outer\ncommit\nbranch inner\ncommit\ncheckout main\ncommit\nmerge inner\nmerge outer'],
  ['lovely-frontmatter', '---\ntitle: "A titled diagram"\nconfig:\n  title: nested\n---\nflowchart TD\nA-->B'],
  ['lovely-frontmatter-wide', '---\ntitle: A title much wider than this\n---\nflowchart TD\nA'],
  ['lovely-frontmatter-unclosed', '---\ntitle: Unclosed\nflowchart TD\nA-->B'],
  ['lovely-frontmatter-nested', '---\nconfig:\n  title: Nested ignored\n---\nflowchart TD\nA'],
  ['lovely-flow-kebab', 'flowchart LR\nstep-1-->step-2-->step-3'],
  ['lovely-flow-pipe-quoted', 'flowchart TD\nA -->|"a|b"| B'],
  ['lovely-flow-inline-labels', 'flowchart LR\nA --"a=b"--> B\nB -- a.b-c --> C'],
  ['lovely-flow-parallel', 'flowchart TD\nA -->|one| B\nA -->|two| B\nA --> B'],
  ['lovely-flow-style', 'flowchart LR\nA[Alpha]:::warm --> B[Beta]\nclass A,B strong\nclassDef warm fill:#f96,stroke:#333,color:navy\nclassDef strong font-weight:bold'],
  ['lovely-flow-style-rgb', 'flowchart TD\nA:::hot --> B:::cool\nclassDef hot fill:rgb(255, 0, 0),stroke:rgb(0, 0, 0),font-weight:bolder\nclassDef cool fill:aliceblue,color:rebeccapurple'],
  ['lovely-flow-style-order', 'flowchart LR\nclass A, B first,second\nclassDef first fill:#111,color:white,font-weight:bold\nclassDef second fill:#eee,font-weight:normal\nA-->B'],
  ['lovely-flow-links', 'flowchart LR\nclick A "https://example.test/a"\nA[Open link] --> B\nclick B href "https://example.test/b"\nclick A "https://example.test/latest"'],
  ['lovely-flow-tags-literal', 'flowchart TD\nA["literal :::hot"] --> B["a;%%b"]'],
  ['lovely-flow-tab', 'flowchart LR\nA["tab\there"]-->B'],
  ['lovely-flow-diamond', 'flowchart TD\nA{Choice} --> B[Yes]\nA --> C[No]'],
  ['lovely-flow-v2', 'flowchart TD\nA@{shape: cyl, label: "Database"} --> B@{shape: diamond, label:"Decision"}\nB-->C@{shape: rounded,label:"Done"}'],
  ['lovely-class-label', 'classDiagram\nclass Animal["Living Animal"]\nAnimal : +string name\nAnimal : +run()'],
  ['lovely-class-cardinality', 'classDiagram\nCustomer "1" --> "0..*" Order : places\nOrder "*" --o "1" LineItem : contains'],
  ['lovely-class-style', 'classDiagram\nclass A\nclass B\nclass A focus\ncssClass "A,B" frame\nclassDef focus color:red\nclassDef frame fill:#eee,stroke:blue\nlink A "https://example.test/a"'],
  ['lovely-class-invalid', 'classDiagram\nclass A {\n+string Name\n}\nthis is invalid\nA-->B'],
  ['lovely-er-alias', 'erDiagram\n"ACCOUNTS"["Bank Account"] {\nstring id PK\n}\nACCOUNTS ||--o{ ENTRY : owns'],
  ['lovely-er-unclosed', 'erDiagram\nA["Unclosed"\nA ||--o{ B : has'],
  ['lovely-er-quoted', 'erDiagram\n"Bank Account" ||--|{ "Account Entry" : contains'],
  ['lovely-state-composite', 'stateDiagram-v2\n[*] --> Active\nstate Active {\n[*] --> One\nOne --> Two\nTwo --> [*]\n}\nActive --> [*]'],
  ['lovely-state-regions', 'stateDiagram-v2\nstate Running {\nstate Nested {\n[*] --> Child\n}\n--\n[*] --> Separate\nSeparate --> Done\n}\nRunning --> [*]'],
  ['lovely-state-repeat', 'stateDiagram-v2\nS1 : First description\nS1 : Second description\nS1 --> S2 : go "x --> y"'],
  ['lovely-state-class', 'stateDiagram-v2\nstate Choice <<choice>>\nA:::hot : Waiting\nA --> Choice\nclassDef hot fill:yellow'],
  ['lovely-state-invalid', 'stateDiagram-v2\nA-->B\nthis is not a state statement\nB-->C'],
  ['lovely-sequence-activation', 'sequenceDiagram\nA->>+B: start\nB-->>-A: finish\nactivate A\nA->>A: work\ndeactivate A'],
  ['lovely-sequence-nested', 'sequenceDiagram\nactivate A\nA->>+B: one\nB->>+B: nested\nB-->>-B: back\nB-->>-A: done\ndeactivate A'],
  ['lovely-sequence-kebab', 'sequenceDiagram\npre-x->>B: hello\nB-->>pre-x: bye\nactor test-user as User\ntest-user->>pre-x: go'],
  ['lovely-sequence-note-left', 'sequenceDiagram\nparticipant A\nparticipant B\nNote left of A: A rather wide note\nA->>B: message'],
  ['lovely-sequence-invalid', 'sequenceDiagram\nA->>B: okay\ninvalid statement\nB-->>A: done'],
  ['lovely-state-quoted-composite', 'stateDiagram-v2\nstate "Outer label" as Outer {\nstate "Inner label" as Inner {\n[*] --> One\n}\n--\nTwo:::hot : a ::: literal\n}\nclassDef hot fill:yellow'],
  ['lovely-flow-v2-punctuation', 'flowchart TD\na-b@{shape: cyl, label: "x,y}"}:::hot -->|"a|b"| C\nclassDef hot fill:red'],
  ['lovely-class-labelled-body', 'classDiagram\nclass A["Label with spaces"]:::hot {\n<<interface>>\n+string Name\n+run()\n}\nA "0..*" --> "1" B:::hot : owns\nclassDef hot fill:#eee'],
  ['lovely-pie-numeric-edge', 'pie showData\n"Octal":0o17\n"Underflow":1e-324\n"Large":1e21\n"Bad positive":+0x1\n"Bad negative":-0x1'],
  ['lovely-git-rejoins', 'gitGraph\ncommit id:"a"\nbranch old\ncommit id:"b"\ncheckout main\ncommit id:"c"\nmerge old\ncheckout old\ncommit id:"d"\ncheckout main\nmerge old'],
];
for (const dir of ['TD','BT','LR','RL']) {
  additions.push([`lovely-flow-routing-${dir}`, `flowchart ${dir}\nA[Start] --> B[Middle]\nB --> C[End]\nC --> B\nA -->|skip| C\nA --> D[Alternate]\nD --> C\nC --> A`]);
  additions.push([`lovely-flow-entry-${dir}`, `flowchart ${dir}\nA-->B\nB-->C\nC-->D\nA-->|long labelled skip edge|D\nB-->|second|D\nA-->|direct|C\nD-->|back|A`]);
  additions.push([`lovely-flow-literal-glyphs-${dir}`, `flowchart ${dir}\nA["┌ └ → ↑"]-->B["line one<br/>line two"]`]);
  additions.push([`lovely-class-reading-${dir}`, `classDiagram\ndirection ${dir}\nclass Animal {\n+string name\n+run()\n}\nAnimal-->Other`]);
}
for (const [id, src] of [...additions]) {
  if (!['lovely-state-composite','lovely-flow-style','lovely-sequence-activation','lovely-git','lovely-pie'].includes(id)) continue;
  const lines=src.split('\n');
  for(let i=1;i<=lines.length;i++) additions.push([`prefix-${id}-${i}`,lines.slice(0,i).join('\n')]);
}
for (const [kind,line,limit] of [['flowchart TD','N',128],['sequenceDiagram','participant P',128],['pie','"Slice',128],['timeline','Period',512],['mindmap','Node',128],['gitGraph','commit id:"C',512]]) {
  const prefix=kind.split(' ')[0];
  const body=Array.from({length:limit+3},(_,i)=>kind==='pie'?`"Slice ${i}":1`:kind==='gitGraph'?`${line}${i}"`:line+i).join('\n');
  additions.push([`cap-lovely-${prefix}`,`${kind}\n${body}`]);
}
additions.push(['cap-lovely-activation-message', 'sequenceDiagram\n' + Array.from({length:512},()=> 'A->>B: message').join('\n') + '\nA->>+B: overflow']);
additions.push(['cap-lovely-timeline-events', 'timeline\nPeriod: ' + Array.from({length:515},(_,i)=>'Event '+i).join(' : ') + '\ntitle Trailing']);
const corpus=[...base,...additions.map(([id,src])=>({id,src}))];
writeFileSync(new URL('../../tests/PiSharp.InteractiveParity.Tests/Mermaid/lovely-0.3.3-corpus.json',import.meta.url),JSON.stringify(corpus,null,2)+'\n');
console.log(`${base.length} historical inputs plus ${additions.length} new inputs = ${corpus.length}`);
