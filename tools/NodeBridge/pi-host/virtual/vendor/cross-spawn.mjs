// PiSharp native replacement for the `cross-spawn` npm package. On Windows, commands that resolve to
// .cmd/.bat shims are run through cmd.exe (what cross-spawn does); everything else uses node:child_process.
import { spawn as nodeSpawn, spawnSync as nodeSpawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";

function resolveWindowsCommand(command, options) {
	if (process.platform !== "win32" || options?.shell) return { command, shim: false };
	if (/[\\/]/.test(command) && /\.(?:cmd|bat)$/i.test(command)) return { command, shim: true };
	if (path.extname(command)) return { command, shim: /\.(?:cmd|bat)$/i.test(command) };
	const env = options?.env ?? process.env;
	const pathVar = env.PATH ?? env.Path ?? "";
	const exts = (env.PATHEXT ?? ".COM;.EXE;.BAT;.CMD").split(";").filter(Boolean);
	const dirs = [options?.cwd ?? process.cwd(), ...pathVar.split(path.delimiter)];
	for (const dir of dirs) {
		for (const ext of exts) {
			const candidate = path.join(dir, command + ext);
			if (existsSync(candidate)) return { command: candidate, shim: /\.(?:cmd|bat)$/i.test(ext) };
		}
	}
	return { command, shim: false };
}

function quoteForCmd(arg) {
	const text = String(arg);
	if (text === "") return '""';
	if (!/[\s"^&|<>()%!]/.test(text)) return text;
	return `"${text.replace(/(\\*)"/g, '$1$1\\"').replace(/(\\+)$/, "$1$1")}"`.replace(/[\^&|<>()%!]/g, "^$&");
}

function prepare(command, args = [], options = {}) {
	const resolved = resolveWindowsCommand(command, options);
	if (!resolved.shim) return { command, args, options };
	const line = [resolved.command, ...args].map(quoteForCmd).join(" ");
	return {
		command: process.env.comspec || "cmd.exe",
		args: ["/d", "/s", "/c", `"${line}"`],
		options: { ...options, windowsVerbatimArguments: true },
	};
}

export default function crossSpawn(command, args, options) {
	const prepared = prepare(command, args, options);
	return nodeSpawn(prepared.command, prepared.args, prepared.options);
}

crossSpawn.spawn = crossSpawn;
crossSpawn.sync = (command, args, options) => {
	const prepared = prepare(command, args, options);
	return nodeSpawnSync(prepared.command, prepared.args, prepared.options);
};

export const spawn = crossSpawn;
export const sync = crossSpawn.sync;
