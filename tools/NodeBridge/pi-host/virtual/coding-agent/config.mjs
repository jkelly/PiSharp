// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/config.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged except for the marked PiSharp adaptations).
import { accessSync, constants, existsSync, readFileSync, realpathSync } from "fs";
import { createRequire } from "module";
import { homedir } from "os";
import { basename, dirname, join, resolve, sep, win32 } from "path";
import { fileURLToPath } from "url";
import { spawnProcessSync } from "./utils/child-process.mjs";
import { normalizePath } from "./utils/paths.mjs";
import { stripBom } from "./utils/text.mjs";
const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
export const isBunBinary = import.meta.url.includes("$bunfs") || import.meta.url.includes("~BUN") || import.meta.url.includes("%7EBUN");
export const isBunRuntime = !!process.versions.bun;
export const isBundledNode = typeof PI_BUNDLED_NODE !== "undefined" && PI_BUNDLED_NODE;
function normalizeSelfUpdatePackageTarget(target) {
    if (typeof target === "string") {
        return {
            packageName: target,
            installSpec: target
        };
    }
    return {
        packageName: target.packageName,
        installSpec: target.installSpec ?? target.packageName
    };
}
function makeSelfUpdateCommand(installStep, uninstallStep) {
    if (!uninstallStep) return installStep;
    return {
        ...installStep,
        display: `${uninstallStep.display} && ${installStep.display}`,
        steps: [
            uninstallStep,
            installStep
        ]
    };
}
function makeSelfUpdateCommandStep(command, args) {
    return {
        command,
        args,
        display: [
            command,
            ...args
        ].map((arg)=>/\s/.test(arg) ? `"${arg}"` : arg).join(" ")
    };
}
export function detectInstallMethod() {
    if (isBunBinary) {
        return "bun-binary";
    }
    const resolvedPath = `${__dirname}\0${process.execPath || ""}`.toLowerCase().replace(/\\/g, "/");
    if (resolvedPath.includes("/pnpm/") || resolvedPath.includes("/.pnpm/")) {
        return "pnpm";
    }
    if (resolvedPath.includes("/yarn/") || resolvedPath.includes("/.yarn/")) {
        return "yarn";
    }
    if (isBunRuntime || resolvedPath.includes("/install/global/node_modules/")) {
        return "bun";
    }
    if (resolvedPath.includes("/npm/") || resolvedPath.includes("/node_modules/")) {
        return "npm";
    }
    return "unknown";
}
function getInferredNpmInstall() {
    const packageDir = getPackageDir();
    const path = process.platform === "win32" || packageDir.includes("\\") ? win32 : {
        basename,
        dirname
    };
    const parent = path.dirname(packageDir);
    let root;
    if (path.basename(parent).startsWith("@") && path.basename(path.dirname(parent)) === "node_modules") {
        root = path.dirname(parent);
    } else if (path.basename(parent) === "node_modules") {
        root = parent;
    }
    if (!root) return undefined;
    const rootParent = path.dirname(root);
    if (path.basename(rootParent) === "lib") return {
        root,
        prefix: path.dirname(rootParent)
    };
    return undefined;
}
function getSelfUpdateCommandForMethod(method, installedPackageName, updatePackageTarget = installedPackageName, npmCommand) {
    const target = normalizeSelfUpdatePackageTarget(updatePackageTarget);
    switch(method){
        case "bun-binary":
            return undefined;
        case "pnpm":
            {
                const match = readCommandOutput("pnpm", [
                    "root",
                    "-g"
                ]) ? undefined : /^(.*[\\/]global[\\/][^\\/]+)[\\/]\.pnpm[\\/]/.exec(getPackageDir());
                const binDirArgs = match ? [
                    `--config.global-bin-dir=${process.env.PNPM_HOME || dirname(dirname(match[1]))}`
                ] : [];
                return makeSelfUpdateCommand(makeSelfUpdateCommandStep("pnpm", [
                    "install",
                    "-g",
                    "--ignore-scripts",
                    "--config.minimumReleaseAge=0",
                    ...binDirArgs,
                    target.installSpec
                ]), target.packageName === installedPackageName ? undefined : makeSelfUpdateCommandStep("pnpm", [
                    "remove",
                    "-g",
                    ...binDirArgs,
                    installedPackageName
                ]));
            }
        case "yarn":
            return makeSelfUpdateCommand(makeSelfUpdateCommandStep("yarn", [
                "global",
                "add",
                "--ignore-scripts",
                target.installSpec
            ]), target.packageName === installedPackageName ? undefined : makeSelfUpdateCommandStep("yarn", [
                "global",
                "remove",
                installedPackageName
            ]));
        case "bun":
            return makeSelfUpdateCommand(makeSelfUpdateCommandStep("bun", [
                "install",
                "-g",
                "--ignore-scripts",
                "--minimum-release-age=0",
                target.installSpec
            ]), target.packageName === installedPackageName ? undefined : makeSelfUpdateCommandStep("bun", [
                "uninstall",
                "-g",
                installedPackageName
            ]));
        case "npm":
            {
                const [command = "npm", ...npmArgs] = npmCommand ?? [];
                const inferred = npmCommand?.length ? undefined : getInferredNpmInstall();
                const prefixArgs = [
                    ...npmArgs,
                    ...inferred ? [
                        "--prefix",
                        inferred.prefix
                    ] : []
                ];
                const installStep = makeSelfUpdateCommandStep(command, [
                    ...prefixArgs,
                    "install",
                    "-g",
                    "--ignore-scripts",
                    "--min-release-age=0",
                    target.installSpec
                ]);
                const uninstallStep = target.packageName === installedPackageName ? undefined : makeSelfUpdateCommandStep(command, [
                    ...prefixArgs,
                    "uninstall",
                    "-g",
                    installedPackageName
                ]);
                return makeSelfUpdateCommand(installStep, uninstallStep);
            }
        case "unknown":
            return undefined;
    }
}
function readCommandOutput(command, args, options = {}) {
    const result = spawnProcessSync(command, args, {
        encoding: "utf-8",
        stdio: [
            "ignore",
            "pipe",
            "pipe"
        ]
    });
    if (result.status === 0) return result.stdout.trim() || undefined;
    if (options.requireSuccess) {
        const reason = result.error?.message || result.stderr.trim() || `exit code ${result.status ?? "unknown"}`;
        throw new Error(`Failed to run ${[
            command,
            ...args
        ].join(" ")}: ${reason}`);
    }
    return undefined;
}
function getGlobalPackageRoots(method, _packageName, npmCommand) {
    switch(method){
        case "npm":
            {
                const configured = !!npmCommand?.length;
                const [command = "npm", ...npmArgs] = npmCommand ?? [];
                if (configured && command === "bun") {
                    const bunBin = readCommandOutput(command, [
                        ...npmArgs,
                        "pm",
                        "bin",
                        "-g"
                    ], {
                        requireSuccess: true
                    });
                    const roots = [
                        join(homedir(), ".bun", "install", "global", "node_modules")
                    ];
                    if (bunBin) {
                        roots.push(join(dirname(bunBin), "install", "global", "node_modules"));
                    }
                    return roots;
                }
                const root = readCommandOutput(command, [
                    ...npmArgs,
                    "root",
                    "-g"
                ], {
                    requireSuccess: configured
                });
                const inferred = configured ? undefined : getInferredNpmInstall();
                return [
                    root,
                    inferred?.root
                ].filter((x)=>!!x);
            }
        case "pnpm":
            {
                const root = readCommandOutput("pnpm", [
                    "root",
                    "-g"
                ]);
                if (root) return [
                    root,
                    dirname(root)
                ];
                const match = /^(.*[\\/]global[\\/][^\\/]+)[\\/]\.pnpm[\\/]/.exec(getPackageDir());
                return match ? [
                    match[1]
                ] : [];
            }
        case "yarn":
            {
                const dir = readCommandOutput("yarn", [
                    "global",
                    "dir"
                ]);
                return dir ? [
                    dir,
                    join(dir, "node_modules")
                ] : [];
            }
        case "bun":
            {
                const bunBin = readCommandOutput("bun", [
                    "pm",
                    "bin",
                    "-g"
                ]);
                const roots = [
                    join(homedir(), ".bun", "install", "global", "node_modules")
                ];
                if (bunBin) {
                    roots.push(join(dirname(bunBin), "install", "global", "node_modules"));
                }
                return roots;
            }
        case "bun-binary":
        case "unknown":
            return [];
    }
}
function normalizeExistingPathForComparison(path, resolveSymlinks) {
    const resolvedPath = resolve(path);
    if (!existsSync(resolvedPath)) {
        return undefined;
    }
    let normalizedPath = resolvedPath;
    if (resolveSymlinks) {
        try {
            normalizedPath = realpathSync(resolvedPath);
        } catch  {
            return undefined;
        }
    }
    if (process.platform === "win32") {
        normalizedPath = normalizedPath.toLowerCase();
    }
    return normalizedPath;
}
function getPathComparisonCandidates(path) {
    return Array.from(new Set([
        normalizeExistingPathForComparison(path, false),
        normalizeExistingPathForComparison(path, true)
    ].filter((candidate)=>!!candidate)));
}
function getEntrypointPackageDir() {
    const entrypoint = process.argv[1];
    if (!entrypoint) return undefined;
    let dir = dirname(entrypoint);
    while(dir !== dirname(dir)){
        if (existsSync(join(dir, "package.json"))) {
            return dir;
        }
        dir = dirname(dir);
    }
    return undefined;
}
function isSelfUpdatePathWritable() {
    const packageDir = getPackageDir();
    try {
        accessSync(packageDir, constants.W_OK);
        accessSync(dirname(packageDir), constants.W_OK);
        return true;
    } catch  {
        return false;
    }
}
function isManagedByGlobalPackageManager(method, packageName, npmCommand) {
    const packageDirs = [
        getPackageDir(),
        getEntrypointPackageDir()
    ].filter((dir)=>!!dir);
    const packageDirCandidates = packageDirs.flatMap((dir)=>getPathComparisonCandidates(dir));
    return getGlobalPackageRoots(method, packageName, npmCommand).some((root)=>{
        return getPathComparisonCandidates(root).some((normalizedRoot)=>{
            const rootPrefix = normalizedRoot.endsWith(sep) ? normalizedRoot : `${normalizedRoot}${sep}`;
            return packageDirCandidates.some((packageDir)=>packageDir.startsWith(rootPrefix));
        });
    });
}
export function getSelfUpdateCommand(packageName, npmCommand, updatePackageTarget = packageName) {
    const method = detectInstallMethod();
    const command = getSelfUpdateCommandForMethod(method, packageName, updatePackageTarget, npmCommand);
    if (!command || !isManagedByGlobalPackageManager(method, packageName, npmCommand) || !isSelfUpdatePathWritable()) {
        return undefined;
    }
    return command;
}
export function getSelfUpdateUnavailableInstruction(packageName, npmCommand, updatePackageTarget = packageName) {
    const method = detectInstallMethod();
    const target = normalizeSelfUpdatePackageTarget(updatePackageTarget);
    if (method === "bun-binary") {
        return `Download from: https://github.com/earendil-works/pi/releases/latest`;
    }
    const command = getSelfUpdateCommandForMethod(method, packageName, target, npmCommand);
    if (command) {
        if (isManagedByGlobalPackageManager(method, packageName, npmCommand) && !isSelfUpdatePathWritable()) {
            return `This installation is managed by a global ${method} install, but the install path is not writable. Update it yourself with: ${command.display}`;
        }
        return `This installation is not managed by a global ${method} install. Update it with the package manager, wrapper, or source checkout that provides it.`;
    }
    return `Update ${target.installSpec} using the package manager, wrapper, or source checkout that provides this installation.`;
}
export function getUpdateInstruction(packageName) {
    const method = detectInstallMethod();
    const command = getSelfUpdateCommandForMethod(method, packageName);
    if (command) {
        return `Run: ${command.display}`;
    }
    return getSelfUpdateUnavailableInstruction(packageName);
}
export function findNodePackageDir(startDir) {
    let dir = startDir;
    while(dir !== dirname(dir)){
        if (existsSync(join(dir, "package.json"))) {
            const parent = dirname(dir);
            if (basename(dir) === "dist" && existsSync(join(parent, "package.json"))) {
                return parent;
            }
            return dir;
        }
        dir = dirname(dir);
    }
    return startDir;
}
export function getPackageDir() {
    const envDir = process.env.PI_PACKAGE_DIR;
    if (envDir) {
        return normalizePath(envDir);
    }
    if (isBunBinary) {
        return dirname(process.execPath);
    }
    return findNodePackageDir(__dirname);
}
export function getThemesDir() {
    if (isBunBinary) {
        return join(getPackageDir(), "theme");
    }
    const packageDir = getPackageDir();
    const srcOrDist = existsSync(join(packageDir, "src")) ? "src" : "dist";
    return join(packageDir, srcOrDist, "modes", "interactive", "theme");
}
export function getExportTemplateDir() {
    if (isBunBinary) {
        return join(getPackageDir(), "export-html");
    }
    const packageDir = getPackageDir();
    const srcOrDist = existsSync(join(packageDir, "src")) ? "src" : "dist";
    return join(packageDir, srcOrDist, "core", "export-html");
}
export function getPackageJsonPath() {
    return join(getPackageDir(), "package.json");
}
export function getReadmePath() {
    return resolve(join(getPackageDir(), "README.md"));
}
export function getDocsPath() {
    return resolve(join(getPackageDir(), "docs"));
}
export function getExamplesPath() {
    return resolve(join(getPackageDir(), "examples"));
}
export function getChangelogPath() {
    return resolve(join(getPackageDir(), "CHANGELOG.md"));
}
export function getInteractiveAssetsDir() {
    if (isBunBinary) {
        return join(getPackageDir(), "assets");
    }
    const packageDir = getPackageDir();
    const srcOrDist = existsSync(join(packageDir, "src")) ? "src" : "dist";
    return join(packageDir, srcOrDist, "modes", "interactive", "assets");
}
export function getBundledInteractiveAssetPath(name) {
    return join(getInteractiveAssetsDir(), name);
}
let quickJSWasmPath;
export function setEmbeddedQuickJSWasmPath(path) {
    quickJSWasmPath = path;
}
export function getQuickJSWasmPath() {
    quickJSWasmPath ??= createRequire(import.meta.url).resolve("quickjs-wasi/quickjs.wasm");
    return quickJSWasmPath;
}
export function resolveCodemodeWorkerSpecifier(runtime, moduleUrl) {
    if (runtime === "bun-binary") return "./src/extensions/codemode/worker.ts";
    if (runtime === "bundled-node") return new URL("./codemode-worker.js", moduleUrl);
    return undefined;
}
let codemodeWorkerDataUrl;
export function getCodemodeWorkerSpecifier() {
    const runtime = isBunBinary ? "bun-binary" : isBundledNode ? "bundled-node" : "unbundled";
    const specifier = resolveCodemodeWorkerSpecifier(runtime, import.meta.url);
    if (runtime !== "bundled-node" || !(specifier instanceof URL)) return specifier;
    codemodeWorkerDataUrl ??= new URL(`data:text/javascript;base64,${readFileSync(specifier).toString("base64")}`);
    return codemodeWorkerDataUrl;
}
export function detectInstallChange(packageJsonPath = startupPackageJsonPath) {
    if (isBunBinary || !packageJsonPath) return undefined;
    let installed;
    try {
        installed = JSON.parse(stripBom(readFileSync(packageJsonPath, "utf-8")));
    } catch (error) {
        return error.code === "ENOENT" ? {
            kind: "removed"
        } : undefined;
    }
    return installed.version && installed.version !== VERSION ? {
        kind: "updated",
        version: installed.version
    } : undefined;
}
let pkg = {
    name: "@earendil-works/pi-coding-agent",
    version: "1.1.0"
};
let startupPackageJsonPath;
startupPackageJsonPath = undefined;
const piConfigName = pkg.piConfig?.name;
export const PACKAGE_NAME = pkg.name || "@earendil-works/pi-coding-agent";
export const APP_NAME = piConfigName || "pi";
export const APP_TITLE = piConfigName ? APP_NAME : "π";
export const CONFIG_DIR_NAME = pkg.piConfig?.configDir || ".pi";
export const VERSION = pkg.version || "0.0.0";
export const ENV_AGENT_DIR = `${APP_NAME.toUpperCase()}_CODING_AGENT_DIR`;
export const ENV_SESSION_DIR = `${APP_NAME.toUpperCase()}_CODING_AGENT_SESSION_DIR`;
export function expandTildePath(path) {
    return normalizePath(path);
}
const DEFAULT_SHARE_VIEWER_URL = "https://pi.dev/session/";
export function getShareViewerUrl(gistId) {
    const baseUrl = process.env.PI_SHARE_VIEWER_URL || DEFAULT_SHARE_VIEWER_URL;
    return `${baseUrl}#${gistId}`;
}
export function getAgentDir() {
    const envDir = process.env[ENV_AGENT_DIR];
    if (envDir) {
        return expandTildePath(envDir);
    }
    return join(homedir(), CONFIG_DIR_NAME, "agent");
}
export function getCustomThemesDir() {
    return join(getAgentDir(), "themes");
}
export function getModelsPath() {
    return join(getAgentDir(), "models.json");
}
export function getAuthPath() {
    return join(getAgentDir(), "auth.json");
}
export function getSettingsPath() {
    return join(getAgentDir(), "settings.json");
}
export function getToolsDir() {
    return join(getAgentDir(), "tools");
}
export function getBinDir() {
    return join(getAgentDir(), "bin");
}
export function getPromptsDir() {
    return join(getAgentDir(), "prompts");
}
export function getSessionsDir() {
    return join(getAgentDir(), "sessions");
}
export function getDebugLogPath() {
    return join(getAgentDir(), `${APP_NAME}-debug.log`);
}
