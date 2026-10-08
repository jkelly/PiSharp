import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';

const repo = path.resolve(import.meta.dirname, '../..');
const base = 'dbf14a1e83afda3ee335d16680feff2898882494';
const output = path.join(repo, 'artifacts', 'controls-editor-integration-proposal-r2');
if (fs.existsSync(output)) throw Error('Fresh proposal output required.');
fs.mkdirSync(output, { recursive: true });
const gitExecutable = 'C:/Program Files/Git/cmd/git.exe';
const git = (...args) => execFileSync(gitExecutable, ['-C', repo, ...args]);
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const changes = [];
function read(file) { return git('show', `${base}:${file}`).toString('utf8'); }
function replace(text, old, value) {
  if (!text.includes(old) || text.indexOf(old) !== text.lastIndexOf(old)) throw Error('Exact integration source pattern differs.');
  return text.replace(old, value);
}
const inputPath = 'src/PiSharp.Cli/Interactive/TerminalChatInput.cs';
let input = read(inputPath).replace('using System.Globalization;\n', '').replace('using System.Text;\n', '');
input = replace(input, 'var draft = new StringBuilder();', 'var draft = new TerminalTextEditorPasteController();');
input = replace(input, 'if (input is TerminalText text) Append(text.Text);', 'if (input is TerminalText text) draft.HandleInput(text);');
input = replace(input, 'else if (input is TerminalPaste paste) Append(paste.Text);', 'else if (input is TerminalPaste paste) draft.HandleInput(paste);');
input = replace(input, 'var line = draft.ToString();', 'var line = draft.GetExpandedText();');
input = replace(input,
`else if (key.Key == "Backspace" && key.Modifiers == TerminalModifiers.None && draft.Length > 0)
                    {
                        // This staging editor uses the same disclosed runtime text-element boundary family as layout.
                        var boundaries = StringInfo.ParseCombiningCharacters(draft.ToString()); draft.Length = boundaries[^1];
                    }`,
`else if (key.Modifiers == TerminalModifiers.None && key.Key is "Backspace" or "Delete" or "Left" or "Right" or "Home" or "End")
                    {
                        draft.HandleInput(key);
                    }`);
input = replace(input, 'key.Modifiers == TerminalModifiers.Control && draft.Length == 0', 'key.Modifiers == TerminalModifiers.Control && draft.Snapshot.Text.Length == 0');
input = replace(input, 'await renderDraft(new(draft.ToString(), draft.Length), stop.Token).ConfigureAwait(false);', 'var snapshot = draft.Snapshot;\n                await renderDraft(new(snapshot.Text, snapshot.CursorUtf16Offset), stop.Token).ConfigureAwait(false);');
input = replace(input,
`if (value.Length > ChatEditor.MaximumCharacters - draft.Length) throw new InvalidOperationException("Terminal draft exceeds its bounded profile.");
            ChatEditor.Validate(value); draft.Append(value);`,
`draft.InsertText(value);`);
input = replace(input,
`else if (key.Modifiers == TerminalModifiers.None && key.Key is "Backspace" or "Delete" or "Left" or "Right" or "Home" or "End")`,
`else if (key.Key == "-" && key.Modifiers == TerminalModifiers.Control)
                    {
                        draft.HandleInput(key);
                    }
                    else if (key.Modifiers == TerminalModifiers.None && key.Key is "Backspace" or "Delete" or "Left" or "Right" or "Home" or "End")`);
if (input.split('draft.Clear();').length !== 4) throw Error('Exact three host clear boundaries required.');
input = input.replaceAll('draft.Clear();', 'draft.Reset();');
input = replace(input, String.raw`else if (key.Key == "Enter" && key.Modifiers == TerminalModifiers.Shift) Append("\n");`,
`else if (key.Key == "Enter" && key.Modifiers is TerminalModifiers.Shift or TerminalModifiers.Alt ||
                        key.Key == "j" && key.Modifiers == TerminalModifiers.Control)
                    {
                        draft.HandleInput(key);
                    }`);
input = replace(input,
`else if (key.Key == "-" && key.Modifiers == TerminalModifiers.Control)
                    {
                        draft.HandleInput(key);
                    }
                    else if (key.Modifiers == TerminalModifiers.None && key.Key is "Backspace" or "Delete" or "Left" or "Right" or "Home" or "End")
                    {
                        draft.HandleInput(key);
                    }
                    `, '');
input = replace(input, '// Protocol/unknown sequences and unsupported keys never become terminal output instructions.',
`else if (key.Modifiers == TerminalModifiers.None && key.Key is "Backspace" or "Delete" or "Left" or "Right" or "Home" or "End" ||
                        key.Modifiers == TerminalModifiers.Control && key.Key is "-" or "a" or "e" or "b" or "f" or "Home" or "End" or "d" or "u" or "k" or "y" ||
                        key.Modifiers == TerminalModifiers.Shift && key.Key is "Backspace" or "Delete" ||
                        key.Modifiers == TerminalModifiers.Alt && key.Key == "y" ||
                        key.Modifiers is TerminalModifiers.None or TerminalModifiers.Shift && !string.IsNullOrEmpty(key.Key) &&
                        System.Text.Rune.TryGetRuneAt(key.Key, 0, out var printable) && printable.Value >= 32 && printable.Utf16SequenceLength == key.Key.Length)
                    {
                        draft.HandleInput(key);
                    }
                    // Protocol/unknown sequences and unsupported keys never become terminal output instructions.`);
input = replace(input, `        void Append(string value)
        {
            draft.InsertText(value);
        }
`, '');
changes.push({ path: inputPath, original: read(inputPath), proposed: input });

const viewPath = 'src/PiSharp.Cli/Interactive/TerminalSessionView.cs';
let view = read(viewPath);
view = replace(view, 'using PiSharp.Tui;\n', 'using PiSharp.Tui;\nusing PiSharp.Tui.Input;\n');
view = replace(view, 'if (value.Text.Length > MaximumDraftCharacters || value.CursorUtf16Offset != value.Text.Length)',
`if (value.Text.Length > MaximumDraftCharacters || value.CursorUtf16Offset < 0 || value.CursorUtf16Offset > value.Text.Length ||
            value.CursorUtf16Offset > 0 && value.CursorUtf16Offset < value.Text.Length &&
            char.IsHighSurrogate(value.Text[value.CursorUtf16Offset - 1]) && char.IsLowSurrogate(value.Text[value.CursorUtf16Offset]))`);
view = replace(view, '"The terminal preview editor uses its complete end-of-draft boundary."', '"The terminal preview cursor must be at a valid draft scalar boundary."');
view = replace(view,
`var input = Wrap(Project("> " + draft.Text), columns);
        if (input[^1].Length == columns) input.Add("");
        var inputRows = Math.Min(rows, Math.Min(3, input.Count));`,
`var projected = TerminalTextEditorProjection.Create(new(draft.Text, draft.CursorUtf16Offset), columns, Math.Min(rows, 3));
        var input = projected.Rows;
        var inputRows = input.Length;`);
view = replace(view,
`visible.AddRange(input.TakeLast(inputRows));
        var cursor = new TerminalCursor(rows - 1, input[^1].Length, true);`,
`visible.AddRange(input);
        var cursor = new TerminalCursor(historyRows + projected.CursorRow, projected.CursorColumn, true);`);
changes.push({ path: viewPath, original: read(viewPath), proposed: view });
let patch = '';
for (const change of changes) {
  const name = path.basename(change.path), originalPath = path.join(output, 'base-' + name), proposedPath = path.join(output, name);
  fs.writeFileSync(originalPath, change.original); fs.writeFileSync(proposedPath, change.proposed);
  const result = spawnSync(gitExecutable, ['diff', '--no-index', '--', originalPath, proposedPath], { encoding: 'utf8' });
  if (result.status !== 1) throw Error('Integration proposal diff failed.');
  patch += result.stdout.replace(/^diff --git .+$/m, `diff --git a/${change.path} b/${change.path}`)
    .replace(/^--- .+$/m, `--- a/${change.path}`).replace(/^\+\+\+ .+$/m, `+++ b/${change.path}`);
}
const patchPath = path.join(output, 'editor-cli-integration.patch');
fs.writeFileSync(patchPath, patch);
git('apply', '--check', patchPath);
const priorHandoffPath = path.join(repo, '..', 'TERMINAL_EDITOR_UNDO_HANDOFF.json');
const priorHandoff = JSON.parse(fs.readFileSync(priorHandoffPath, 'utf8'));
if (priorHandoff.candidate !== '039b3fe816d0235060b398279fb9dd02b5a01d7e') throw Error('Exact predecessor proposal required.');
const priorContract = JSON.parse(fs.readFileSync(priorHandoff.integrationProposal.contract.path, 'utf8'));
const priorInput = path.join(path.dirname(priorHandoff.integrationProposal.contract.path), 'TerminalChatInput.cs');
if (hash(fs.readFileSync(priorInput)) !== priorContract.paths.find(row => row.path === inputPath).proposedSha256 ||
    hash(view) !== priorContract.paths.find(row => row.path === viewPath).proposedSha256) throw Error('Prior proposal or unchanged view identity differs.');
const deltaResult = spawnSync(gitExecutable, ['diff', '--no-index', '--', priorInput, path.join(output, 'TerminalChatInput.cs')], { encoding: 'utf8' });
if (deltaResult.status !== 1) throw Error('Incremental proposal diff failed.');
const delta = deltaResult.stdout.replace(/^diff --git .+$/m, `diff --git a/${inputPath} b/${inputPath}`)
  .replace(/^--- .+$/m, `--- a/${inputPath}`).replace(/^\+\+\+ .+$/m, `+++ b/${inputPath}`);
const deltaPath = path.join(output, 'controls-from-undo-proposal.patch');
fs.writeFileSync(deltaPath, delta);
const checkRoot = path.join(output, 'lead-delta-check');
const checkInput = path.join(checkRoot, inputPath); fs.mkdirSync(path.dirname(checkInput), { recursive: true }); fs.copyFileSync(priorInput, checkInput);
git('apply', '--check', '--directory=artifacts/controls-editor-integration-proposal-r2/lead-delta-check', deltaPath);
const contract = { schemaVersion: 1, base, upstreamCommit: 'd86654abb8862e201933517d6f1fce9f88dd117f',
  patch: { path: patchPath, bytes: Buffer.byteLength(patch), sha256: hash(patch) },
  paths: changes.map(change => ({ path: change.path, baseBlob: git('rev-parse', `${base}:${change.path}`).toString().trim(),
    baseSha256: hash(change.original), proposedSha256: hash(change.proposed) })),
  applyCheckPassed: true, patchApplied: false, compiledOrExecuted: false,
  requiredIntegrationOwner: 'Session lead; review/coordinate active frontend changes before applying',
  contract: 'Explicitly opt into TerminalTextEditorPasteController, continue rendering marker Snapshot.Text, and use GetExpandedText() for submission. Host submission/cancel/teardown use Reset to discard old draft history. Supported native keyboard editing and decoded printable scalars are forwarded after host quit/cancel decisions; raw LF is Control j, CSI-u 57349 is printable and 57426 is Delete; raw U+001F decoder identity is Control minus, while coded Control underscore remains distinct. Borrowed lease and joined read/decoder shutdown stay host-owned. The scalar-caret projection is unchanged from the independently accepted bounded controller proposal.',
  incrementalFromAcceptedUndoProposal: { path: deltaPath, bytes: Buffer.byteLength(delta), sha256: hash(delta), predecessor: priorHandoff.candidate,
    priorHandoffSha256: hash(fs.readFileSync(priorHandoffPath)), baseProposalInputSha256: hash(fs.readFileSync(priorInput)),
    applyCheckPassedAgainstMaterializedPriorProposal: true, actualLeadCandidateApplyChecked: false, changes: ['Forward default cursor/kill/yank/delete/newline aliases and decoded printable scalars', 'Preserve host empty-draft Control D before generic editor dispatch'] },
  sharedControllerDefaultBehaviorChanged: false, newControllerOptedIntoByLead: false,
  remainingQualification: ['Lead integration build/tests', 'Caret/resize/small-window and queued-input regression cases', 'Actual published CLI bootstrap and coordinated physical terminal receipts', 'Full P5-08 and OS matrix'] };
fs.writeFileSync(path.join(output, 'integration-contract.json'), JSON.stringify(contract, null, 2) + '\n');
console.log(JSON.stringify(contract));
