// Golden outputs for the C# port of grok-mermaid, computed by the original library under Node.
//
// Usage: node gen-goldens.mjs <path to grok-mermaid package root> <mermaid-corpus.json> <mermaid-goldens.json>
//
// The corpus is a JSON array of { id, src }. Per case the goldens record
//   kind    diagramKind(src)
//   render  render(src): null or { width, plain, styled, warnings }
//   ansi    toAnsi(render(src)) with the default theme, or null                 (detail cases only)
//   box     sourceBox(src)                                                       (detail cases only)
//   box30   sourceBox(src, 30)                                                   (detail cases only)
// where `styled` rows are written compactly as arrays of [cls, text] pairs, and detail cases are those whose id does
// not start with fuzz-, large-, cap- or canvas- (keeps the file small; those prefixes still check kind and render).
import { readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

const [libRoot, corpusPath, outPath] = process.argv.slice(2)
if (!libRoot || !corpusPath || !outPath) {
  console.error('usage: node gen-goldens.mjs <grok-mermaid root> <corpus.json> <goldens.json>')
  process.exit(2)
}
const pkg = JSON.parse(readFileSync(join(libRoot, 'package.json'), 'utf8'))
const lib = await import(pathToFileURL(join(libRoot, 'dist', 'index.js')).href)
const corpus = JSON.parse(readFileSync(corpusPath, 'utf8'))

const art = (a) =>
  a === null ? null : { width: a.width, plain: a.plain, styled: a.styled.map((row) => row.map((s) => [s.cls, s.text])), warnings: a.warnings }
const detail = (id) => !/^(fuzz|large|cap|canvas)-/.test(id)

const cases = {}
for (const { id, src } of corpus) {
  if (Object.hasOwn(cases, id)) throw new Error(`duplicate id ${id}`)
  const rendered = lib.render(src)
  const entry = { kind: lib.diagramKind(src), render: art(rendered) }
  if (detail(id)) {
    entry.ansi = rendered === null ? null : lib.toAnsi(rendered)
    entry.box = art(lib.sourceBox(src))
    entry.box30 = art(lib.sourceBox(src, 30))
  }
  cases[id] = entry
}

// One line per case keeps the file diffable.
const runtime = `node ${process.version} (ICU ${process.versions.icu}, Unicode ${process.versions.unicode})`
const head = `{"library":${JSON.stringify(`${pkg.name}@${pkg.version}`)},"runtime":${JSON.stringify(runtime)},"cases":{`
const body = Object.entries(cases).map(([id, c]) => `${JSON.stringify(id)}:${JSON.stringify(c)}`)
writeFileSync(outPath, `${head}\n${body.join(',\n')}\n}}\n`)
const rendered = Object.values(cases).filter((c) => c.render !== null).length
console.log(`${corpus.length} cases, ${rendered} rendered, ${corpus.length - rendered} null`)
