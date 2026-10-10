// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/index.ts.
// PiSharp shim for `@earendil-works/pi-coding-agent` as seen by extensions. Every runtime export of
// upstream index.ts is present:
//  - pure helpers, components, theme, config, compaction, session-entry helpers, skills, frontmatter,
//    truncation, keybinding hints, extension type guards are mechanically ported upstream code;
//  - built-in tool factories are host-backed (coding-agent/tools.mjs: execute -> "builtinTool.execute");
//  - host-heavy functions forward to the bridge hook (globalThis.__pisharpBridge.call(name, ...args)) and
//    throw "<name> is not available in the PiSharp Node bridge" without it;
//  - host-heavy classes (sessions, settings, registries, interactive components) are stubs that throw on
//    construction or static use.
import { setKeybindings } from "./pi-tui.mjs";
import { hostFunction, unavailableClass } from "./bridge.mjs";
import { KeybindingsManager } from "./coding-agent/core/keybindings.mjs";

// Config paths
export {
	CONFIG_DIR_NAME,
	getAgentDir,
	getDocsPath,
	getExamplesPath,
	getPackageDir,
	getReadmePath,
	VERSION,
} from "./coding-agent/config.mjs";

// Agent session (host-owned)
export const AgentSession = unavailableClass("AgentSession");

// Pi abe508e1 (MIT): packages/coding-agent/src/core/agent-session.ts parseSkillBlock.
export function parseSkillBlock(text) {
	const match = text.match(/^<skill name="([^"]+)" location="([^"]+)">\n([\s\S]*?)\n<\/skill>(?:\n\n([\s\S]+))?$/);
	if (!match) return null;
	return {
		name: match[1],
		location: match[2],
		content: match[3],
		userMessage: match[4]?.trim() || undefined,
	};
}

export const readStoredCredential = hostFunction("readStoredCredential");

// Compaction
export {
	calculateContextTokens,
	collectEntriesForBranchSummary,
	compact,
	DEFAULT_COMPACTION_SETTINGS,
	estimateTokens,
	findCutPoint,
	findTurnStartIndex,
	generateBranchSummary,
	generateSummary,
	generateSummaryWithUsage,
	getLastAssistantUsage,
	prepareBranchEntries,
	serializeConversation,
	shouldCompact,
} from "./coding-agent/core/compaction/index.mjs";
export { createEventBus } from "./coding-agent/core/event-bus.mjs";

// Extension system
export const createExtensionRuntime = hostFunction("createExtensionRuntime");
export const discoverAndLoadExtensions = hostFunction("discoverAndLoadExtensions");
export const ExtensionRunner = unavailableClass("ExtensionRunner");
export {
	defineTool,
	isBashToolResult,
	isEditToolResult,
	isFindToolResult,
	isGrepToolResult,
	isLsToolResult,
	isPowerShellToolResult,
	isReadToolResult,
	isToolCallEventType,
	isWriteToolResult,
} from "./coding-agent/core/extensions/types.mjs";
export { wrapRegisteredTool, wrapRegisteredTools } from "./coding-agent/core/extensions/wrapper.mjs";
export { convertToLlm } from "./coding-agent/core/messages.mjs";
export const ModelRegistry = unavailableClass("ModelRegistry");
export const resolveCliModel = hostFunction("resolveCliModel");
export const resolveModelScopeWithDiagnostics = hostFunction("resolveModelScopeWithDiagnostics");

// Pi abe508e1 (MIT): packages/coding-agent/src/core/model-runtime.ts CredentialSynchronizationError.
/** Credentials changed successfully, but the local model/auth snapshot could not be synchronized. */
export class CredentialSynchronizationError extends Error {
	providerId;
	operation;
	credential;
	constructor(providerId, operation, credential, options) {
		super(`Credential ${operation} committed for ${providerId}, but local synchronization failed`, options);
		this.name = "CredentialSynchronizationError";
		this.providerId = providerId;
		this.operation = operation;
		this.credential = credential;
	}
}
export const ModelRuntime = unavailableClass("ModelRuntime");
export const DefaultPackageManager = unavailableClass("DefaultPackageManager");
export const DefaultResourceLoader = unavailableClass("DefaultResourceLoader");
export const loadProjectContextFiles = hostFunction("loadProjectContextFiles");

// SDK
export const AgentSessionRuntime = unavailableClass("AgentSessionRuntime");
export const createAgentSession = hostFunction("createAgentSession");
export const createAgentSessionFromServices = hostFunction("createAgentSessionFromServices");
export const createAgentSessionRuntime = hostFunction("createAgentSessionRuntime");
export const createAgentSessionServices = hostFunction("createAgentSessionServices");
export {
	createBashTool,
	createCodingTools,
	createEditTool,
	createFindTool,
	createGrepTool,
	createLsTool,
	createPowerShellTool,
	createReadOnlyTools,
	createReadTool,
	createWriteTool,
} from "./coding-agent/tools.mjs";

// Session entries (the SessionManager itself is host-owned)
export {
	buildContextEntries,
	buildSessionContext,
	buildSessionProjection,
	CURRENT_SESSION_VERSION,
	getLatestCompactionEntry,
	migrateSessionEntries,
	parseSessionEntries,
	sessionEntryToContextMessages,
} from "./coding-agent/core/session-manager.mjs";
export const SessionManager = unavailableClass("SessionManager");
export const SettingsManager = unavailableClass("SettingsManager");

// Skills
export { formatSkillsForPrompt, loadSkills, loadSkillsFromDir } from "./coding-agent/core/skills.mjs";
export { createSyntheticSourceInfo } from "./coding-agent/core/source-info.mjs";
export const generateDiffString = hostFunction("generateDiffString");
export const generateUnifiedPatch = hostFunction("generateUnifiedPatch");

// Tools
export {
	createBashToolDefinition,
	createEditToolDefinition,
	createFindToolDefinition,
	createGrepToolDefinition,
	createLocalBashOperations,
	createLocalPowerShellOperations,
	createLsToolDefinition,
	createPowerShellToolDefinition,
	createReadToolDefinition,
	createWriteToolDefinition,
} from "./coding-agent/tools.mjs";
export {
	DEFAULT_MAX_BYTES,
	DEFAULT_MAX_LINES,
	formatSize,
	truncateHead,
	truncateLine,
	truncateTail,
} from "./coding-agent/core/tools/truncate.mjs";
export { withFileMutationQueue } from "./coding-agent/core/tools/file-mutation-queue.mjs";
export const hasTrustRequiringProjectResources = hostFunction("hasTrustRequiringProjectResources");
export const ProjectTrustStore = unavailableClass("ProjectTrustStore");
export { VIRTUAL_MODEL_STATE_ENTRY } from "./coding-agent/core/virtual-models.mjs";

// Built-in extensions (run by the host)
export const createCodemodeExtension = hostFunction("createCodemodeExtension");
export const createMcpExtension = hostFunction("createMcpExtension");
export const createToolSearchExtension = hostFunction("createToolSearchExtension");

// Main entry point and run modes (host-owned)
export const main = hostFunction("main");
export const InteractiveMode = unavailableClass("InteractiveMode");
export const RpcClient = unavailableClass("RpcClient");
export const runPrintMode = hostFunction("runPrintMode");
export const runRpcMode = hostFunction("runRpcMode");

// UI components for extensions
export { BorderedLoader } from "./coding-agent/modes/interactive/components/bordered-loader.mjs";
export { CustomEditor } from "./coding-agent/modes/interactive/components/custom-editor.mjs";
export { DynamicBorder } from "./coding-agent/modes/interactive/components/dynamic-border.mjs";
export { keyHint, keyText, rawKeyHint } from "./coding-agent/modes/interactive/components/keybinding-hints.mjs";
export { truncateToVisualLines } from "./coding-agent/modes/interactive/components/visual-truncate.mjs";
export const renderDiff = hostFunction("renderDiff");
export const ArminComponent = unavailableClass("ArminComponent");
export const AssistantMessageComponent = unavailableClass("AssistantMessageComponent");
export const BashExecutionComponent = unavailableClass("BashExecutionComponent");
export const BranchSummaryMessageComponent = unavailableClass("BranchSummaryMessageComponent");
export const CompactionSummaryMessageComponent = unavailableClass("CompactionSummaryMessageComponent");
export const CustomMessageComponent = unavailableClass("CustomMessageComponent");
export const ExtensionEditorComponent = unavailableClass("ExtensionEditorComponent");
export const ExtensionInputComponent = unavailableClass("ExtensionInputComponent");
export const ExtensionSelectorComponent = unavailableClass("ExtensionSelectorComponent");
export const FooterComponent = unavailableClass("FooterComponent");
export const LoginDialogComponent = unavailableClass("LoginDialogComponent");
export const ModelSelectorComponent = unavailableClass("ModelSelectorComponent");
export const OAuthSelectorComponent = unavailableClass("OAuthSelectorComponent");
export const SessionSelectorComponent = unavailableClass("SessionSelectorComponent");
export const SettingsSelectorComponent = unavailableClass("SettingsSelectorComponent");
export const ShowImagesSelectorComponent = unavailableClass("ShowImagesSelectorComponent");
export const SkillInvocationMessageComponent = unavailableClass("SkillInvocationMessageComponent");
export const ThemeSelectorComponent = unavailableClass("ThemeSelectorComponent");
export const ThinkingSelectorComponent = unavailableClass("ThinkingSelectorComponent");
export const ToolExecutionComponent = unavailableClass("ToolExecutionComponent");
export const TreeSelectorComponent = unavailableClass("TreeSelectorComponent");
export const UserMessageComponent = unavailableClass("UserMessageComponent");
export const UserMessageSelectorComponent = unavailableClass("UserMessageSelectorComponent");

// Theme utilities for custom tools and extensions
export {
	getLanguageFromPath,
	getMarkdownTheme,
	getSelectListTheme,
	getSettingsListTheme,
	highlightCode,
	initTheme,
	Theme,
} from "./theme.mjs";

// Clipboard and file utilities
export const copyToClipboard = hostFunction("copyToClipboard");
export { parseFrontmatter, stripFrontmatter } from "./coding-agent/utils/frontmatter.mjs";
export const convertToPng = hostFunction("convertToPng");

// Pi abe508e1 (MIT): packages/coding-agent/src/utils/image-resize.ts formatDimensionNote.
/**
 * Format a dimension note for resized images.
 * This helps the model understand the coordinate mapping.
 */
export function formatDimensionNote(result) {
	if (!result.wasResized) {
		return undefined;
	}
	const scale = result.originalWidth / result.width;
	return `[Image: original ${result.originalWidth}x${result.originalHeight}, displayed at ${result.width}x${result.height}. Multiply coordinates by ${scale.toFixed(2)} to map to original image.]`;
}
export const resizeImage = hostFunction("resizeImage");
export { detectSupportedImageMimeTypeFromFile } from "./coding-agent/utils/mime.mjs";
export { getPowerShellConfig, getShellConfig } from "./coding-agent/utils/shell.mjs";
export const parseArgs = hostFunction("parseArgs");

// Interactive Pi installs the app keybindings (TUI + app.*, plus ~/.pi/agent/keybindings.json) at startup,
// which keyHint()/keyText() and editor components rely on. The bridge does the same when this module loads.
try {
	setKeybindings(KeybindingsManager.create());
} catch {
	setKeybindings(new KeybindingsManager());
}
