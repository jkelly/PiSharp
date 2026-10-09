// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/{bash,powershell,read,edit,write,grep,find,ls,index}.ts.
// PiSharp host-backed built-in tool factories. Names, labels, descriptions, prompt snippets/guidelines,
// parameter/output schemas and argument preparation are copied from upstream. `execute` delegates to
// PiSharp's native built-in tool through the bridge hook:
//
//   globalThis.__pisharpBridge.call("builtinTool.execute",
//     { name, toolCallId, params, cwd, operations?, options? }, { signal, onUpdate, ctx })
//
// Upstream's TUI renderers (renderCall/renderResult) are not included: the PiSharp host renders built-in
// tool calls natively.
import { hostCall } from "../bridge.mjs";
import { Type } from "../typebox.mjs";
import { wrapToolDefinition } from "./core/tools/tool-definition-wrapper.mjs";
import { DEFAULT_MAX_BYTES, DEFAULT_MAX_LINES, GREP_MAX_LINE_LENGTH } from "./core/tools/truncate.mjs";

function splitOptions(options) {
	if (!options) return { operations: undefined, rest: undefined };
	const { operations, ...rest } = options;
	return { operations, rest: Object.keys(rest).length > 0 ? rest : undefined };
}

function hostExecute(name, cwd, options) {
	const { operations, rest } = splitOptions(options);
	return async function execute(toolCallId, params, signal, onUpdate, ctx) {
		const request = { name, toolCallId, params, cwd: ctx?.cwd || cwd };
		if (operations !== undefined) request.operations = operations;
		if (rest !== undefined) request.options = rest;
		return await hostCall("builtinTool.execute", request, { signal, onUpdate, ctx });
	};
}

// ---------------------------------------------------------------------------------------------
// bash / powershell
// ---------------------------------------------------------------------------------------------

const bashSchema = Type.Object({
	command: Type.String({ description: "Shell command to execute" }),
	timeout: Type.Optional(Type.Number({ description: "Timeout in seconds (optional, no default timeout)" })),
});

export const bashToolSystemPromptContribution = {
	snippet: "Execute bash commands (ls, grep, find, etc.)",
	guidelines: ["You can inspect PI_* environment variables for current model and session details."],
};

const bashOutputSchema = Type.Object({
	output: Type.String({ description: "Combined stdout and stderr, possibly truncated" }),
	truncated: Type.Boolean(),
	full_output_path: Type.Optional(Type.String({ description: "Full output, when truncated" })),
	exit_code: Type.Number(),
	wall_time_seconds: Type.Number(),
});

export const powershellToolSystemPromptContribution = {
	snippet: "Execute PowerShell commands",
	guidelines: ["You can inspect PI_* environment variables for current model and session details."],
};

const bashToolConfig = {
	name: "bash",
	label: "bash",
	shellName: "bash",
	prompt: "$",
	promptSnippet: bashToolSystemPromptContribution.snippet,
	promptGuidelines: bashToolSystemPromptContribution.guidelines,
	tempFilePrefix: "pi-bash",
};

const powershellToolConfig = {
	name: "powershell",
	label: "powershell",
	shellName: "PowerShell",
	prompt: "PS>",
	promptSnippet: powershellToolSystemPromptContribution.snippet,
	promptGuidelines: powershellToolSystemPromptContribution.guidelines,
	tempFilePrefix: "pi-powershell",
};

/** Host-backed equivalent of upstream createLocalShellOperations(): exec runs in the PiSharp host. */
function createHostShellOperations(shellName, extra) {
	return {
		exec: (command, cwd, options) => hostCall("shellOperations.exec", { shell: shellName, command, cwd, ...extra }, options),
	};
}

export function createLocalBashOperations(options) {
	return createHostShellOperations("bash", { shellPath: options?.shellPath });
}

export function createLocalPowerShellOperations() {
	return createHostShellOperations("PowerShell", {});
}

export function createShellToolDefinition(cwd, config, options) {
	const exposeSessionEnvironment = options?.exposeSessionEnvironment ?? true;
	return {
		name: config.name,
		label: config.label,
		description: `Execute a ${config.shellName} command in the current working directory. Returns stdout and stderr. Output is truncated to last ${DEFAULT_MAX_LINES} lines or ${DEFAULT_MAX_BYTES / 1024}KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.`,
		promptSnippet: config.promptSnippet,
		promptGuidelines: exposeSessionEnvironment && config.promptGuidelines ? [...config.promptGuidelines] : undefined,
		parameters: bashSchema,
		outputSchema: bashOutputSchema,
		constrainedSampling: { type: "json_schema", strict: "prefer" },
		execute: hostExecute(config.name, cwd, options),
	};
}

export function createBashToolDefinition(cwd, options) {
	return createShellToolDefinition(cwd, bashToolConfig, options);
}

export function createBashTool(cwd, options) {
	const definition = createBashToolDefinition(cwd, options);
	const tool = wrapToolDefinition(definition);
	Object.assign(tool, {
		promptSnippet: definition.promptSnippet,
		promptGuidelines: definition.promptGuidelines,
	});
	return tool;
}

export function createPowerShellToolDefinition(cwd, options) {
	return createShellToolDefinition(cwd, powershellToolConfig, options);
}

export function createPowerShellTool(cwd, options) {
	const definition = createPowerShellToolDefinition(cwd, options);
	const tool = wrapToolDefinition(definition);
	Object.assign(tool, {
		promptSnippet: definition.promptSnippet,
		promptGuidelines: definition.promptGuidelines,
	});
	return tool;
}

// ---------------------------------------------------------------------------------------------
// read
// ---------------------------------------------------------------------------------------------

const readSchema = Type.Object({
	path: Type.String({ description: "Path to the file to read (relative or absolute)" }),
	offset: Type.Optional(Type.Number({ description: "Line number to start reading from (1-indexed)" })),
	limit: Type.Optional(Type.Number({ description: "Maximum number of lines to read" })),
});

export const readToolSystemPromptContribution = {
	snippet: "Read file contents",
	guidelines: ["Use read to examine files instead of cat or sed."],
};

const readOutputSchema = Type.Union([
	Type.String(),
	Type.Object({ type: Type.Literal("image"), data: Type.String(), mimeType: Type.String(), note: Type.String() }),
]);

export function createReadToolDefinition(cwd, options) {
	return {
		name: "read",
		label: "read",
		description: `Read the contents of a file. Supports text files and images (jpg, png, gif, webp, bmp). Images are sent as attachments. For text files, output is truncated to ${DEFAULT_MAX_LINES} lines or ${DEFAULT_MAX_BYTES / 1024}KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.`,
		promptSnippet: readToolSystemPromptContribution.snippet,
		promptGuidelines: [...readToolSystemPromptContribution.guidelines],
		parameters: readSchema,
		outputSchema: readOutputSchema,
		constrainedSampling: { type: "json_schema", strict: "prefer" },
		execute: hostExecute("read", cwd, options),
	};
}

export function createReadTool(cwd, options) {
	return wrapToolDefinition(createReadToolDefinition(cwd, options));
}

// ---------------------------------------------------------------------------------------------
// edit
// ---------------------------------------------------------------------------------------------

const replaceEditSchema = Type.Object(
	{
		oldText: Type.String({
			description:
				"Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call.",
		}),
		newText: Type.String({ description: "Replacement text for this targeted edit." }),
	},
	{},
);

const editSchema = Type.Object(
	{
		path: Type.String({ description: "Path to the file to edit (relative or absolute)" }),
		edits: Type.Array(replaceEditSchema, {
			description:
				"One or more targeted replacements. Each edit is matched against the original file, not incrementally. Do not include overlapping or nested edits. If two changes touch the same block or nearby lines, merge them into one edit instead.",
		}),
	},
	{},
);

export const editToolSystemPromptContribution = {
	snippet: "Make precise file edits with exact text replacement, including multiple disjoint edits in one call",
	guidelines: [
		"Use edit for precise changes (edits[].oldText must match exactly)",
		"When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls",
		"Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.",
		"Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions.",
	],
};

function isSingleEditInput(value) {
	if (!value || typeof value !== "object" || Array.isArray(value)) {
		return false;
	}
	const edit = value;
	return typeof edit.oldText === "string" && typeof edit.newText === "string";
}

function prepareEditArguments(input) {
	if (!input || typeof input !== "object") {
		return input;
	}
	const args = input;
	// Some models (Opus 4.6, GLM-5.1) send edits as a JSON string instead of an array.
	// Others send a single edit object instead of a one-element edits array.
	if (typeof args.edits === "string") {
		try {
			const parsed = JSON.parse(args.edits);
			if (Array.isArray(parsed)) {
				args.edits = parsed;
			} else if (isSingleEditInput(parsed)) {
				args.edits = [parsed];
			}
		} catch {}
	} else if (isSingleEditInput(args.edits)) {
		args.edits = [args.edits];
	}
	const legacy = args;
	if (typeof legacy.oldText !== "string" || typeof legacy.newText !== "string") {
		return args;
	}
	const edits = Array.isArray(legacy.edits) ? [...legacy.edits] : [];
	edits.push({ oldText: legacy.oldText, newText: legacy.newText });
	const { oldText: _oldText, newText: _newText, ...rest } = legacy;
	return { ...rest, edits };
}

export function createEditToolDefinition(cwd, options) {
	return {
		name: "edit",
		label: "edit",
		description:
			"Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.",
		promptSnippet: editToolSystemPromptContribution.snippet,
		promptGuidelines: [...editToolSystemPromptContribution.guidelines],
		parameters: editSchema,
		constrainedSampling: { type: "json_schema", strict: "prefer" },
		renderShell: "self",
		prepareArguments: prepareEditArguments,
		execute: hostExecute("edit", cwd, options),
	};
}

export function createEditTool(cwd, options) {
	return wrapToolDefinition(createEditToolDefinition(cwd, options));
}

// ---------------------------------------------------------------------------------------------
// write
// ---------------------------------------------------------------------------------------------

const writeSchema = Type.Object({
	path: Type.String({ description: "Path to the file to write (relative or absolute)" }),
	content: Type.String({ description: "Content to write to the file" }),
});

export const writeToolSystemPromptContribution = {
	snippet: "Create or overwrite files",
	guidelines: ["Use write only for new files or complete rewrites."],
};

export function createWriteToolDefinition(cwd, options) {
	return {
		name: "write",
		label: "write",
		description:
			"Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.",
		promptSnippet: writeToolSystemPromptContribution.snippet,
		promptGuidelines: [...writeToolSystemPromptContribution.guidelines],
		parameters: writeSchema,
		constrainedSampling: { type: "json_schema", strict: "prefer" },
		execute: hostExecute("write", cwd, options),
	};
}

export function createWriteTool(cwd, options) {
	return wrapToolDefinition(createWriteToolDefinition(cwd, options));
}

// ---------------------------------------------------------------------------------------------
// grep
// ---------------------------------------------------------------------------------------------

const grepSchema = Type.Object({
	pattern: Type.String({ description: "Search pattern (regex or literal string)" }),
	path: Type.Optional(Type.String({ description: "Directory or file to search (default: current directory)" })),
	glob: Type.Optional(Type.String({ description: "Filter files by glob pattern, e.g. '*.ts' or '**/*.spec.ts'" })),
	ignoreCase: Type.Optional(Type.Boolean({ description: "Case-insensitive search (default: false)" })),
	literal: Type.Optional(
		Type.Boolean({ description: "Treat pattern as literal string instead of regex (default: false)" }),
	),
	context: Type.Optional(
		Type.Number({ description: "Number of lines to show before and after each match (default: 0)" }),
	),
	limit: Type.Optional(Type.Number({ description: "Maximum number of matches to return (default: 100)" })),
});

export const grepToolSystemPromptContribution = {
	snippet: "Search file contents for patterns (respects .gitignore)",
	guidelines: [],
};

const GREP_DEFAULT_LIMIT = 100;

export function createGrepToolDefinition(cwd, options) {
	return {
		name: "grep",
		label: "grep",
		description: `Search file contents for a pattern. Returns matching lines with file paths and line numbers. Respects .gitignore. Output is truncated to ${GREP_DEFAULT_LIMIT} matches or ${DEFAULT_MAX_BYTES / 1024}KB (whichever is hit first). Long lines are truncated to ${GREP_MAX_LINE_LENGTH} chars.`,
		promptSnippet: grepToolSystemPromptContribution.snippet,
		parameters: grepSchema,
		execute: hostExecute("grep", cwd, options),
	};
}

export function createGrepTool(cwd, options) {
	return wrapToolDefinition(createGrepToolDefinition(cwd, options));
}

// ---------------------------------------------------------------------------------------------
// find
// ---------------------------------------------------------------------------------------------

const findSchema = Type.Object({
	pattern: Type.String({
		description: "Glob pattern to match files, e.g. '*.ts', '**/*.json', or 'src/**/*.spec.ts'",
	}),
	path: Type.Optional(Type.String({ description: "Directory to search in (default: current directory)" })),
	limit: Type.Optional(Type.Number({ description: "Maximum number of results (default: 1000)" })),
});

export const findToolSystemPromptContribution = {
	snippet: "Find files by glob pattern (respects .gitignore)",
	guidelines: [],
};

const FIND_DEFAULT_LIMIT = 1000;

export function createFindToolDefinition(cwd, options) {
	return {
		name: "find",
		label: "find",
		description: `Search for files by glob pattern. Returns matching file paths relative to the search directory. Respects .gitignore. Output is truncated to ${FIND_DEFAULT_LIMIT} results or ${DEFAULT_MAX_BYTES / 1024}KB (whichever is hit first).`,
		promptSnippet: findToolSystemPromptContribution.snippet,
		parameters: findSchema,
		execute: hostExecute("find", cwd, options),
	};
}

export function createFindTool(cwd, options) {
	return wrapToolDefinition(createFindToolDefinition(cwd, options));
}

// ---------------------------------------------------------------------------------------------
// ls
// ---------------------------------------------------------------------------------------------

const lsSchema = Type.Object({
	path: Type.Optional(Type.String({ description: "Directory to list (default: current directory)" })),
	limit: Type.Optional(Type.Number({ description: "Maximum number of entries to return (default: 500)" })),
});

export const lsToolSystemPromptContribution = {
	snippet: "List directory contents",
	guidelines: [],
};

const LS_DEFAULT_LIMIT = 500;

export function createLsToolDefinition(cwd, options) {
	return {
		name: "ls",
		label: "ls",
		description: `List directory contents. Returns entries sorted alphabetically, with '/' suffix for directories. Includes dotfiles. Output is truncated to ${LS_DEFAULT_LIMIT} entries or ${DEFAULT_MAX_BYTES / 1024}KB (whichever is hit first).`,
		promptSnippet: lsToolSystemPromptContribution.snippet,
		parameters: lsSchema,
		execute: hostExecute("ls", cwd, options),
	};
}

export function createLsTool(cwd, options) {
	return wrapToolDefinition(createLsToolDefinition(cwd, options));
}

// ---------------------------------------------------------------------------------------------
// index.ts helpers
// ---------------------------------------------------------------------------------------------

export const allToolNames = new Set(["read", "bash", "powershell", "edit", "write", "grep", "find", "ls"]);

export function createToolDefinition(toolName, cwd, options) {
	switch (toolName) {
		case "read":
			return createReadToolDefinition(cwd, options?.read);
		case "bash":
			return createBashToolDefinition(cwd, options?.bash);
		case "powershell":
			return createPowerShellToolDefinition(cwd, options?.powershell);
		case "edit":
			return createEditToolDefinition(cwd, options?.edit);
		case "write":
			return createWriteToolDefinition(cwd, options?.write);
		case "grep":
			return createGrepToolDefinition(cwd, options?.grep);
		case "find":
			return createFindToolDefinition(cwd, options?.find);
		case "ls":
			return createLsToolDefinition(cwd, options?.ls);
		default:
			throw new Error(`Unknown tool name: ${toolName}`);
	}
}

export function createTool(toolName, cwd, options) {
	switch (toolName) {
		case "read":
			return createReadTool(cwd, options?.read);
		case "bash":
			return createBashTool(cwd, options?.bash);
		case "powershell":
			return createPowerShellTool(cwd, options?.powershell);
		case "edit":
			return createEditTool(cwd, options?.edit);
		case "write":
			return createWriteTool(cwd, options?.write);
		case "grep":
			return createGrepTool(cwd, options?.grep);
		case "find":
			return createFindTool(cwd, options?.find);
		case "ls":
			return createLsTool(cwd, options?.ls);
		default:
			throw new Error(`Unknown tool name: ${toolName}`);
	}
}

export function createCodingToolDefinitions(cwd, options) {
	return [
		createReadToolDefinition(cwd, options?.read),
		createBashToolDefinition(cwd, options?.bash),
		createEditToolDefinition(cwd, options?.edit),
		createWriteToolDefinition(cwd, options?.write),
	];
}

export function createReadOnlyToolDefinitions(cwd, options) {
	return [
		createReadToolDefinition(cwd, options?.read),
		createGrepToolDefinition(cwd, options?.grep),
		createFindToolDefinition(cwd, options?.find),
		createLsToolDefinition(cwd, options?.ls),
	];
}

export function createCodingTools(cwd, options) {
	return [
		createReadTool(cwd, options?.read),
		createBashTool(cwd, options?.bash),
		createEditTool(cwd, options?.edit),
		createWriteTool(cwd, options?.write),
	];
}

export function createReadOnlyTools(cwd, options) {
	return [
		createReadTool(cwd, options?.read),
		createGrepTool(cwd, options?.grep),
		createFindTool(cwd, options?.find),
		createLsTool(cwd, options?.ls),
	];
}

export function createAllTools(cwd, options) {
	return {
		read: createReadTool(cwd, options?.read),
		bash: createBashTool(cwd, options?.bash),
		powershell: createPowerShellTool(cwd, options?.powershell),
		edit: createEditTool(cwd, options?.edit),
		write: createWriteTool(cwd, options?.write),
		grep: createGrepTool(cwd, options?.grep),
		find: createFindTool(cwd, options?.find),
		ls: createLsTool(cwd, options?.ls),
	};
}
