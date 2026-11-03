// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/utils/shell.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { accessSync, constants } from "node:fs";
import { delimiter, join, win32 } from "node:path";
import { spawn, spawnSync } from "child_process";
import { getBinDir } from "../config.mjs";
function isLegacyWslBashPath(path) {
    const normalized = win32.normalize(path).toLowerCase();
    const systemRoot = process.env.SystemRoot;
    if (systemRoot) {
        if (!/^[a-z]:[\\/]/i.test(systemRoot)) return false;
        return [
            "System32",
            "Sysnative"
        ].some((directory)=>normalized === win32.join(systemRoot, directory, "bash.exe").toLowerCase());
    }
    return /^[a-z]:\\windows\\(?:system32|sysnative)\\bash\.exe$/.test(normalized);
}
function getBashShellConfig(shell) {
    return isLegacyWslBashPath(shell) ? {
        shell,
        args: [
            "-s"
        ],
        commandTransport: "stdin"
    } : {
        shell,
        args: [
            "-c"
        ]
    };
}
function shellPathExists(path) {
    try {
        accessSync(path, constants.F_OK);
        return true;
    } catch  {
        return false;
    }
}
function findExecutableOnPath(executable) {
    if (process.platform === "win32") {
        try {
            const result = spawnSync("where", [
                executable
            ], {
                encoding: "utf-8",
                timeout: 5000,
                windowsHide: true
            });
            if (result.status === 0 && result.stdout) {
                const firstMatch = result.stdout.trim().split(/\r?\n/)[0];
                if (firstMatch && shellPathExists(firstMatch)) {
                    return firstMatch;
                }
            }
        } catch  {}
        return null;
    }
    try {
        const result = spawnSync("which", [
            executable
        ], {
            encoding: "utf-8",
            timeout: 5000
        });
        if (result.status === 0 && result.stdout) {
            const firstMatch = result.stdout.trim().split(/\r?\n/)[0];
            if (firstMatch) {
                return firstMatch;
            }
        }
    } catch  {}
    return null;
}
export function getShellConfig(customShellPath) {
    if (customShellPath) {
        if (shellPathExists(customShellPath)) {
            return getBashShellConfig(customShellPath);
        }
        throw new Error(`Custom shell path not found: ${customShellPath}`);
    }
    if (process.platform === "win32") {
        const paths = [];
        const programFiles = process.env.ProgramFiles;
        if (programFiles) {
            paths.push(`${programFiles}\\Git\\bin\\bash.exe`);
        }
        const programFilesX86 = process.env["ProgramFiles(x86)"];
        if (programFilesX86) {
            paths.push(`${programFilesX86}\\Git\\bin\\bash.exe`);
        }
        for (const path of paths){
            if (shellPathExists(path)) {
                return getBashShellConfig(path);
            }
        }
        const bashOnPath = findExecutableOnPath("bash.exe");
        if (bashOnPath) {
            return getBashShellConfig(bashOnPath);
        }
        throw new Error(`No bash shell found. Options:\n` + `  1. Install Git for Windows: https://git-scm.com/download/win\n` + `  2. Add your bash to PATH (Cygwin, MSYS2, etc.)\n` + "  3. Set shellPath in settings.json\n\n" + `Searched Git Bash in:\n${paths.map((p)=>`  ${p}`).join("\n")}`);
    }
    if (shellPathExists("/bin/bash")) {
        return getBashShellConfig("/bin/bash");
    }
    const bashOnPath = findExecutableOnPath("bash");
    if (bashOnPath) {
        return getBashShellConfig(bashOnPath);
    }
    return {
        shell: "sh",
        args: [
            "-c"
        ]
    };
}
export const POWERSHELL_ARGS = [
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy",
    "Bypass",
    "-Command"
];
export function getPowerShellConfig() {
    if (process.platform !== "win32") {
        throw new Error("The powershell tool is only available on Windows.");
    }
    let shell = findExecutableOnPath("pwsh.exe");
    const programFiles = process.env.ProgramFiles;
    if (!shell && programFiles) {
        const pwsh7 = win32.join(programFiles, "PowerShell", "7", "pwsh.exe");
        if (shellPathExists(pwsh7)) shell = pwsh7;
    }
    shell ??= findExecutableOnPath("powershell.exe");
    const systemRoot = process.env.SystemRoot;
    if (!shell && systemRoot) {
        const windowsPowerShell = win32.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (shellPathExists(windowsPowerShell)) shell = windowsPowerShell;
    }
    if (!shell) {
        throw new Error("No PowerShell executable found. Install PowerShell or add powershell.exe/pwsh.exe to PATH.");
    }
    return {
        shell,
        args: [
            ...POWERSHELL_ARGS
        ]
    };
}
export function getShellEnv() {
    const binDir = getBinDir();
    const pathKey = Object.keys(process.env).find((key)=>key.toLowerCase() === "path") ?? "PATH";
    const currentPath = process.env[pathKey] ?? "";
    const pathEntries = currentPath.split(delimiter).filter(Boolean);
    const hasBinDir = pathEntries.includes(binDir);
    const updatedPath = hasBinDir ? currentPath : [
        binDir,
        currentPath
    ].filter(Boolean).join(delimiter);
    return {
        ...process.env,
        [pathKey]: updatedPath
    };
}
export function sanitizeBinaryOutput(str) {
    return str.replace(/[\x00-\x08\x0B\x0C\x0E-\x1F\uFFF9-\uFFFB]/g, "");
}
const trackedDetachedChildPids = new Set();
export function trackDetachedChildPid(pid) {
    trackedDetachedChildPids.add(pid);
}
export function untrackDetachedChildPid(pid) {
    trackedDetachedChildPids.delete(pid);
}
export function killTrackedDetachedChildren() {
    for (const pid of trackedDetachedChildPids){
        killProcessTree(pid);
    }
    trackedDetachedChildPids.clear();
}
export function killProcessTree(pid) {
    if (process.platform === "win32") {
        try {
            const child = spawn(join(process.env.SystemRoot || "C:\\Windows", "System32", "taskkill.exe"), [
                "/F",
                "/T",
                "/PID",
                String(pid)
            ], {
                stdio: "ignore",
                detached: true,
                windowsHide: true
            });
            child.once("error", ()=>{});
        } catch  {}
    } else {
        if (process.platform === "darwin") {
            try {
                process.kill(-pid, "SIGSTOP");
            } catch  {}
        }
        try {
            process.kill(-pid, "SIGKILL");
        } catch  {
            try {
                process.kill(pid, "SIGKILL");
            } catch  {}
        }
    }
}
