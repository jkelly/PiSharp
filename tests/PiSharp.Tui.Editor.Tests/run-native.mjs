import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';

const args = process.argv.slice(2);
if (![6, 8].includes(args.length) || args[0] !== '--dotnet' || args[2] !== '--dll' || args[4] !== '--report' || (args.length === 8 && args[6] !== '--source-observations'))
  throw Error('Usage: run-native.mjs --dotnet <absolute-host> --dll <absolute-harness-DLL> --report <fresh-report> [--source-observations <frozen-source-capture>]');
const host = path.resolve(args[1]), dll = path.resolve(args[3]), report = path.resolve(args[5]);
const source = args.length === 8 ? path.resolve(args[7]) : null;
const root = path.dirname(report), scratch = path.join(root, 'native-run-scratch');
if (fs.existsSync(report) || fs.existsSync(report + '.execution.json')) throw Error('Fresh report/receipt required.');
fs.mkdirSync(scratch, { recursive: true });
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const receipt = file => ({ path: file, bytes: fs.statSync(file).size, sha256: hash(fs.readFileSync(file)) });
const executedFiles = fs.readdirSync(path.dirname(dll)).filter(name => /\.(dll|deps\.json|runtimeconfig\.json)$/.test(name)).map(name => receipt(path.join(path.dirname(dll), name)));
const command = [dll, '--report', report, ...(source ? ['--source-observations', source] : [])];
const stdout = [], stderr = []; let timedOut = false;
const startedUtc = new Date().toISOString();
const child = spawn(host, command, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  env: { SystemRoot: process.env.SystemRoot, TEMP: scratch, TMP: scratch, APPDATA: scratch, LOCALAPPDATA: scratch,
    DOTNET_CLI_HOME: scratch, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false', DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: 'false' } });
const timer = setTimeout(() => { timedOut = true; child.kill(); }, 45_000);
child.stdout.on('data', chunk => stdout.push(chunk)); child.stderr.on('data', chunk => stderr.push(chunk));
const outcome = await new Promise(resolve => { child.on('error', error => resolve({ error: error.message })); child.on('close', (code, signal) => resolve({ code, signal })); });
clearTimeout(timer);
fs.writeFileSync(report + '.stdout.log', Buffer.concat(stdout)); fs.writeFileSync(report + '.stderr.log', Buffer.concat(stderr));
for (const row of executedFiles) if (receipt(row.path).sha256 !== row.sha256) throw Error('Executed DLL/runtime identity changed during run.');
const execution = { schemaVersion: 1, startedUtc, settledUtc: new Date().toISOString(), childPid: child.pid, command: { host, args: command },
  host: receipt(host), executedFiles, source: source ? receipt(source) : null, outcome, timedOut, childJoined: true,
  stdout: receipt(report + '.stdout.log'), stderr: receipt(report + '.stderr.log'), report: fs.existsSync(report) ? receipt(report) : null,
  privateScratch: scratch, physicalConsoleRun: false, fullNativeGate: false };
fs.writeFileSync(report + '.execution.json', JSON.stringify(execution, null, 2) + '\n', { flag: 'wx' });
process.stdout.write(Buffer.concat(stdout)); process.stderr.write(Buffer.concat(stderr));
console.log(JSON.stringify({ executionReceipt: report + '.execution.json', outcome, timedOut, childJoined: true }));
process.exitCode = outcome.code === 0 && !timedOut ? 0 : 1;
