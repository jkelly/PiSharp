// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/session-manager.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { getCurrentSystemMessage, uuidv7 } from "../../pi-ai.mjs";
import { randomUUID } from "crypto";
import { appendFileSync, closeSync, createReadStream, existsSync, mkdirSync, openSync, readdirSync, readSync, statSync, writeFileSync } from "fs";
import { readdir, stat } from "fs/promises";
import { basename, join, resolve } from "path";
import { createInterface } from "readline";
import { StringDecoder } from "string_decoder";
import { APP_NAME, getAgentDir as getDefaultAgentDir, getSessionsDir } from "../config.mjs";
import { normalizePath, resolvePath } from "../utils/paths.mjs";
import { createBranchSummaryMessage, createCompactionSummaryMessage, createCustomMessage } from "./messages.mjs";
export const CURRENT_SESSION_VERSION = 3;
function createSessionId() {
    return uuidv7();
}
export function assertValidSessionId(id) {
    if (!/^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$/.test(id)) {
        throw new Error("Session id must be non-empty, contain only alphanumeric characters, '-', '_', and '.', and start and end with an alphanumeric character");
    }
}
function generateId(byId) {
    for(let i = 0; i < 100; i++){
        const id = randomUUID().slice(0, 8);
        if (!byId.has(id)) return id;
    }
    return randomUUID();
}
function migrateV1ToV2(entries) {
    const ids = new Set();
    let prevId = null;
    for (const entry of entries){
        if (entry.type === "session") {
            entry.version = 2;
            continue;
        }
        entry.id = generateId(ids);
        entry.parentId = prevId;
        prevId = entry.id;
        if (entry.type === "compaction") {
            const comp = entry;
            if (typeof comp.firstKeptEntryIndex === "number") {
                const targetEntry = entries[comp.firstKeptEntryIndex];
                if (targetEntry && targetEntry.type !== "session") {
                    comp.firstKeptEntryId = targetEntry.id;
                }
                delete comp.firstKeptEntryIndex;
            }
        }
    }
}
function migrateV2ToV3(entries) {
    for (const entry of entries){
        if (entry.type === "session") {
            entry.version = 3;
            continue;
        }
        if (entry.type === "message") {
            const msgEntry = entry;
            if (msgEntry.message && msgEntry.message.role === "hookMessage") {
                msgEntry.message.role = "custom";
            }
        }
    }
}
function migrateToCurrentVersion(entries) {
    const header = entries.find((e)=>e.type === "session");
    const version = header?.version ?? 1;
    if (version >= CURRENT_SESSION_VERSION) return false;
    if (version < 2) migrateV1ToV2(entries);
    if (version < 3) migrateV2ToV3(entries);
    return true;
}
export function migrateSessionEntries(entries) {
    migrateToCurrentVersion(entries);
}
export function parseSessionEntries(content) {
    const entries = [];
    const lines = content.trim().split("\n");
    for (const line of lines){
        if (!line.trim()) continue;
        try {
            const entry = JSON.parse(line);
            entries.push(entry);
        } catch  {}
    }
    return entries;
}
export function getLatestCompactionEntry(entries) {
    for(let i = entries.length - 1; i >= 0; i--){
        if (entries[i].type === "compaction") {
            return entries[i];
        }
    }
    return null;
}
function buildEntryIndex(entries, byId) {
    if (byId) return byId;
    const index = new Map();
    for (const entry of entries){
        index.set(entry.id, entry);
    }
    return index;
}
function buildSessionPath(entries, leafId, byId) {
    const index = buildEntryIndex(entries, byId);
    let leaf;
    if (leafId === null) {
        return [];
    }
    if (leafId) {
        leaf = index.get(leafId);
    }
    leaf ??= entries[entries.length - 1];
    if (!leaf) {
        return [];
    }
    const path = [];
    let current = leaf;
    while(current){
        path.push(current);
        current = current.parentId ? index.get(current.parentId) : undefined;
    }
    path.reverse();
    return path;
}
function getSessionContextSettings(path) {
    let thinkingLevel = "off";
    let model = null;
    for (const entry of path){
        if (entry.type === "thinking_level_change") {
            thinkingLevel = entry.thinkingLevel;
        } else if (entry.type === "model_change") {
            model = {
                provider: entry.provider,
                modelId: entry.modelId
            };
        } else if (entry.type === "message" && entry.message.role === "assistant") {
            model = {
                provider: entry.message.provider,
                modelId: entry.message.model
            };
        }
    }
    return {
        thinkingLevel,
        model
    };
}
export function sessionEntryToContextMessages(entry) {
    if (entry.type === "message") {
        const message = entry.message;
        if (message.role === "system" && message.content == null) return [
            {
                ...message,
                content: ""
            }
        ];
        if ((message.role === "user" || message.role === "assistant" || message.role === "toolResult") && message.content == null) {
            return [
                {
                    ...message,
                    content: []
                }
            ];
        }
        return [
            message
        ];
    }
    if (entry.type === "custom_message") {
        return [
            createCustomMessage(entry.customType, entry.content ?? [], entry.display, entry.details, entry.timestamp)
        ];
    }
    if (entry.type === "branch_summary" && entry.summary) {
        return [
            createBranchSummaryMessage(entry.summary, entry.fromId, entry.timestamp)
        ];
    }
    if (entry.type === "compaction") {
        const summary = createCompactionSummaryMessage(entry.summary, entry.tokensBefore, entry.timestamp);
        return entry.systemMessage ? [
            entry.systemMessage,
            summary
        ] : [
            summary
        ];
    }
    return [];
}
export function buildContextEntries(entries, leafId, byId) {
    const path = buildSessionPath(entries, leafId, byId);
    let compaction = null;
    for (const entry of path){
        if (entry.type === "compaction") {
            compaction = entry;
        }
    }
    if (!compaction) {
        return path;
    }
    const compactionIdx = path.findIndex((entry)=>entry.id === compaction.id);
    if (compactionIdx < 0) {
        return path;
    }
    const contextEntries = [
        compaction
    ];
    let foundFirstKept = false;
    for(let i = 0; i < compactionIdx; i++){
        const entry = path[i];
        if (entry.id === compaction.firstKeptEntryId) {
            foundFirstKept = true;
        }
        if (foundFirstKept && !(entry.type === "message" && entry.message.role === "system")) {
            contextEntries.push(entry);
        }
    }
    contextEntries.push(...path.slice(compactionIdx + 1));
    return contextEntries;
}
function projectContextEntry(entry, edit) {
    const messages = sessionEntryToContextMessages(entry);
    if (!edit) return messages;
    const replacement = edit.replacement;
    if (replacement === null) return [];
    return messages.map((message)=>{
        if (message.role !== "user" && message.role !== "assistant" && message.role !== "toolResult" && message.role !== "custom") {
            return message;
        }
        const content = (message.role === "assistant" || message.role === "toolResult") && typeof replacement.content === "string" ? [
            {
                type: "text",
                text: replacement.content
            }
        ] : replacement.content;
        return {
            ...message,
            content
        };
    });
}
export function buildSessionProjection(entries, leafId, byId) {
    const path = buildSessionPath(entries, leafId, byId);
    const { thinkingLevel, model } = getSessionContextSettings(path);
    const contextEntries = buildContextEntries(entries, leafId, byId);
    const edits = new Map();
    for (const entry of contextEntries){
        if (entry.type === "context_edit") edits.set(entry.targetId, entry);
    }
    const projectedEntries = contextEntries.map((sourceEntry, index)=>({
            sourceEntry,
            messages: sourceEntry.type === "compaction" && index > 0 ? [] : projectContextEntry(sourceEntry, edits.get(sourceEntry.id))
        }));
    return {
        entries: projectedEntries,
        messages: projectedEntries.flatMap((entry)=>entry.messages),
        thinkingLevel,
        model
    };
}
export function buildSessionContext(entries, leafId, byId) {
    const { messages, thinkingLevel, model } = buildSessionProjection(entries, leafId, byId);
    return {
        messages,
        thinkingLevel,
        model
    };
}
function getDefaultSessionDirPath(cwd, agentDir = getDefaultAgentDir()) {
    const resolvedCwd = resolvePath(cwd);
    const resolvedAgentDir = resolvePath(agentDir);
    const safePath = `--${resolvedCwd.replace(/^[/\\]/, "").replace(/[/\\:]/g, "-")}--`;
    return join(resolvedAgentDir, "sessions", safePath);
}
export function getDefaultSessionDir(cwd, agentDir = getDefaultAgentDir()) {
    const sessionDir = getDefaultSessionDirPath(cwd, agentDir);
    if (!existsSync(sessionDir)) {
        mkdirSync(sessionDir, {
            recursive: true
        });
    }
    return sessionDir;
}
const SESSION_READ_BUFFER_SIZE = 1024 * 1024;
const SESSION_HEADER_READ_BUFFER_SIZE = 4096;
const MAX_SESSION_HEADER_SCAN_BYTES = 1024 * 1024;
class SessionHeaderScanLimitError extends Error {
    constructor(filePath){
        super(`Session header exceeds ${MAX_SESSION_HEADER_SCAN_BYTES}-byte scan limit: ${filePath}`);
        this.name = "SessionHeaderScanLimitError";
    }
}
function parseSessionEntryLine(line) {
    if (!line.trim()) return null;
    try {
        return JSON.parse(line);
    } catch  {
        return null;
    }
}
export function loadEntriesFromFile(filePath) {
    const resolvedFilePath = normalizePath(filePath);
    if (!existsSync(resolvedFilePath)) return [];
    const entries = [];
    let pending = "";
    const fd = openSync(resolvedFilePath, "r");
    try {
        const decoder = new StringDecoder("utf8");
        const buffer = Buffer.allocUnsafe(SESSION_READ_BUFFER_SIZE);
        while(true){
            const bytesRead = readSync(fd, buffer, 0, buffer.length, null);
            if (bytesRead === 0) break;
            pending += decoder.write(buffer.subarray(0, bytesRead));
            let lineStart = 0;
            let newlineIndex = pending.indexOf("\n", lineStart);
            while(newlineIndex !== -1){
                const entry = parseSessionEntryLine(pending.slice(lineStart, newlineIndex));
                if (entry) entries.push(entry);
                lineStart = newlineIndex + 1;
                newlineIndex = pending.indexOf("\n", lineStart);
            }
            pending = pending.slice(lineStart);
        }
        pending += decoder.end();
        const finalEntry = parseSessionEntryLine(pending);
        if (finalEntry) entries.push(finalEntry);
    } finally{
        closeSync(fd);
    }
    if (entries.length === 0) return entries;
    const header = entries[0];
    if (header.type !== "session" || typeof header.id !== "string") {
        return [];
    }
    if (pending) appendFileSync(resolvedFilePath, "\n");
    return entries;
}
function parseSessionHeaderCandidate(line) {
    if (!line.trim()) return undefined;
    const entry = parseSessionEntryLine(line);
    if (!entry) return undefined;
    if (entry.type !== "session" || typeof entry.id !== "string") return null;
    return entry;
}
function readSessionHeader(filePath) {
    const fd = openSync(filePath, "r");
    try {
        const decoder = new StringDecoder("utf8");
        const buffer = Buffer.allocUnsafe(SESSION_HEADER_READ_BUFFER_SIZE);
        const lineChunks = [];
        let scannedBytes = 0;
        while(scannedBytes < MAX_SESSION_HEADER_SCAN_BYTES){
            const readLength = Math.min(buffer.length, MAX_SESSION_HEADER_SCAN_BYTES - scannedBytes);
            const bytesRead = readSync(fd, buffer, 0, readLength, null);
            if (bytesRead === 0) {
                lineChunks.push(decoder.end());
                return parseSessionHeaderCandidate(lineChunks.join("")) ?? null;
            }
            scannedBytes += bytesRead;
            const chunk = decoder.write(buffer.subarray(0, bytesRead));
            let lineStart = 0;
            let newlineIndex = chunk.indexOf("\n", lineStart);
            while(newlineIndex !== -1){
                lineChunks.push(chunk.slice(lineStart, newlineIndex));
                const header = parseSessionHeaderCandidate(lineChunks.join(""));
                if (header !== undefined) return header;
                lineChunks.length = 0;
                lineStart = newlineIndex + 1;
                newlineIndex = chunk.indexOf("\n", lineStart);
            }
            lineChunks.push(chunk.slice(lineStart));
        }
        const probe = Buffer.allocUnsafe(1);
        if (readSync(fd, probe, 0, probe.length, null) === 0) {
            lineChunks.push(decoder.end());
            return parseSessionHeaderCandidate(lineChunks.join("")) ?? null;
        }
        throw new SessionHeaderScanLimitError(filePath);
    } finally{
        closeSync(fd);
    }
}
function readSessionHeaderForDiscovery(filePath) {
    try {
        return readSessionHeader(filePath);
    } catch  {
        return null;
    }
}
function getSessionHeaderCwd(header) {
    const cwd = header.cwd;
    return typeof cwd === "string" ? cwd : undefined;
}
function sessionCwdMatches(cwd, resolvedCwd) {
    return cwd !== undefined && cwd !== "" && resolvePath(cwd) === resolvedCwd;
}
export function findMostRecentSession(sessionDir, cwd) {
    const resolvedSessionDir = normalizePath(sessionDir);
    const resolvedCwd = cwd ? resolvePath(cwd) : undefined;
    try {
        const files = readdirSync(resolvedSessionDir).filter((file)=>file.endsWith(".jsonl")).map((file)=>join(resolvedSessionDir, file)).map((path)=>({
                path,
                mtime: statSync(path).mtimeMs
            })).sort((a, b)=>b.mtime - a.mtime);
        for (const { path } of files){
            const header = readSessionHeaderForDiscovery(path);
            if (header && (!resolvedCwd || sessionCwdMatches(getSessionHeaderCwd(header), resolvedCwd))) return path;
        }
        return null;
    } catch  {
        return null;
    }
}
function isMessageWithContent(message) {
    return typeof message.role === "string" && "content" in message;
}
function extractTextContent(message) {
    const content = message.content;
    if (typeof content === "string") {
        return content;
    }
    return content.filter((block)=>block.type === "text").map((block)=>block.text).join(" ");
}
function getMessageActivityTime(entry) {
    const message = entry.message;
    if (!isMessageWithContent(message)) return undefined;
    if (message.role !== "user" && message.role !== "assistant") return undefined;
    const msgTimestamp = message.timestamp;
    if (typeof msgTimestamp === "number") {
        return msgTimestamp;
    }
    const t = new Date(entry.timestamp).getTime();
    return Number.isNaN(t) ? undefined : t;
}
async function buildSessionInfo(filePath, signal, fileStats) {
    try {
        const stats = fileStats ?? await stat(filePath);
        let header = null;
        let messageCount = 0;
        let firstMessage = "";
        const allMessages = [];
        let name;
        let lastActivityTime;
        const rl = createInterface({
            input: createReadStream(filePath, {
                encoding: "utf8",
                signal
            }),
            crlfDelay: Infinity
        });
        for await (const line of rl){
            const entry = parseSessionEntryLine(line);
            if (!entry) continue;
            if (!header) {
                if (entry.type !== "session") return null;
                header = entry;
                continue;
            }
            if (entry.type === "session_info") {
                name = entry.name?.trim() || undefined;
            }
            if (entry.type !== "message") continue;
            messageCount++;
            const activityTime = getMessageActivityTime(entry);
            if (typeof activityTime === "number") {
                lastActivityTime = Math.max(lastActivityTime ?? 0, activityTime);
            }
            const message = entry.message;
            if (!isMessageWithContent(message)) continue;
            if (message.role !== "user" && message.role !== "assistant") continue;
            const textContent = extractTextContent(message);
            if (!textContent) continue;
            allMessages.push(textContent);
            if (!firstMessage && message.role === "user") {
                firstMessage = textContent;
            }
        }
        if (!header) return null;
        const cwd = typeof header.cwd === "string" ? header.cwd : "";
        const parentSessionPath = header.parentSession;
        const headerTime = typeof header.timestamp === "string" ? new Date(header.timestamp).getTime() : NaN;
        const modified = typeof lastActivityTime === "number" && lastActivityTime > 0 ? new Date(lastActivityTime) : !Number.isNaN(headerTime) ? new Date(headerTime) : stats.mtime;
        return {
            path: filePath,
            id: header.id,
            cwd,
            name,
            parentSessionPath,
            created: new Date(header.timestamp),
            modified,
            messageCount,
            firstMessage: firstMessage || "(no messages)",
            allMessagesText: allMessages.join(" ")
        };
    } catch  {
        signal?.throwIfAborted();
        return null;
    }
}
const MAX_CONCURRENT_SESSION_INFO_LOADS = 10;
const MAX_CONCURRENT_SESSION_DISCOVERY_LOADS = 64;
const CURRENT_SESSION_LIST_PUBLISH_INTERVAL = 10;
const ALL_SESSION_LIST_PUBLISH_INTERVAL = 100;
async function mapWithConcurrency(items, limit, map, signal) {
    const results = new Array(items.length);
    let nextIndex = 0;
    const worker = async ()=>{
        while(nextIndex < items.length){
            signal?.throwIfAborted();
            const index = nextIndex++;
            results[index] = await map(items[index], index);
        }
    };
    await Promise.all(Array.from({
        length: Math.min(limit, items.length)
    }, ()=>worker()));
    return results;
}
function sortSessionInfos(sessions) {
    return sessions.sort((a, b)=>b.modified.getTime() - a.modified.getTime());
}
function buildSessionInfosWithConcurrency(files, onLoaded, signal) {
    return mapWithConcurrency(files, MAX_CONCURRENT_SESSION_INFO_LOADS, async (file, index)=>{
        const info = await buildSessionInfo(file.path, signal, file.stats);
        onLoaded(info, index);
        return info;
    }, signal);
}
async function listSessionsFromDir(dir, onProgress, signal) {
    signal?.throwIfAborted();
    if (!existsSync(dir)) return [];
    try {
        const dirEntries = await readdir(dir);
        const files = dirEntries.filter((file)=>file.endsWith(".jsonl")).sort((a, b)=>b.localeCompare(a)).map((file)=>({
                path: join(dir, file)
            }));
        const total = files.length;
        const partialSessions = [];
        let loaded = 0;
        const results = await buildSessionInfosWithConcurrency(files, (info)=>{
            loaded++;
            if (info) partialSessions.push(info);
            const publishPartial = loaded === 1 || loaded % CURRENT_SESSION_LIST_PUBLISH_INTERVAL === 0 || loaded === files.length;
            onProgress?.(loaded, total, publishPartial ? sortSessionInfos([
                ...partialSessions
            ]) : undefined);
        }, signal);
        return results.filter((info)=>info !== null);
    } catch  {
        signal?.throwIfAborted();
        return [];
    }
}
export class SessionManager {
    sessionId = "";
    sessionFile;
    sessionDir;
    cwd;
    persist;
    flushed = false;
    fileEntries = [];
    byId = new Map();
    labelsById = new Map();
    labelTimestampsById = new Map();
    leafId = null;
    constructor(cwd, sessionDir, sessionFile, persist, newSessionOptions, preloadedFileEntries){
        this.cwd = resolvePath(cwd);
        this.sessionDir = normalizePath(sessionDir);
        this.persist = persist;
        if (persist && this.sessionDir && !existsSync(this.sessionDir)) {
            mkdirSync(this.sessionDir, {
                recursive: true
            });
        }
        if (sessionFile) {
            this._setSessionFile(sessionFile, preloadedFileEntries);
        } else if (preloadedFileEntries?.length) {
            this._loadEntries(preloadedFileEntries, newSessionOptions);
        } else {
            this.newSession(newSessionOptions);
        }
    }
    setSessionFile(sessionFile) {
        this._setSessionFile(sessionFile);
    }
    _setSessionFile(sessionFile, preloadedFileEntries) {
        this.sessionFile = resolvePath(sessionFile);
        if (existsSync(this.sessionFile)) {
            const entries = preloadedFileEntries ?? loadEntriesFromFile(this.sessionFile);
            if (entries.length === 0) {
                const explicitPath = this.sessionFile;
                if (statSync(explicitPath).size > 0) {
                    throw new Error(`Session file is not a valid ${APP_NAME} session: ${explicitPath}`);
                }
                this.newSession();
                this.sessionFile = explicitPath;
                this._rewriteFile();
                this.flushed = true;
                return;
            }
            this._loadEntries(entries);
            this.flushed = true;
        } else {
            const explicitPath = this.sessionFile;
            this.newSession();
            this.sessionFile = explicitPath;
        }
    }
    newSession(options) {
        if (options?.id !== undefined) {
            assertValidSessionId(options.id);
        }
        this.sessionId = options?.id ?? createSessionId();
        const timestamp = new Date().toISOString();
        const header = {
            type: "session",
            version: CURRENT_SESSION_VERSION,
            id: this.sessionId,
            timestamp,
            cwd: this.cwd,
            parentSession: options?.parentSession
        };
        this.fileEntries = [
            header
        ];
        this.byId.clear();
        this.labelsById.clear();
        this.labelTimestampsById.clear();
        this.leafId = null;
        this.flushed = false;
        if (this.persist) {
            const fileTimestamp = timestamp.replace(/[:.]/g, "-");
            this.sessionFile = join(this.getSessionDir(), `${fileTimestamp}_${this.sessionId}.jsonl`);
        }
        return this.sessionFile;
    }
    _loadEntries(entries, options) {
        const header = entries.find((e)=>e.type === "session");
        if (header) {
            this.fileEntries = entries;
            this.sessionId = header.id;
            if (migrateToCurrentVersion(this.fileEntries)) {
                this._rewriteFile();
            }
        } else {
            this.newSession(options);
            this.fileEntries = this.fileEntries.concat(entries);
        }
        this._buildIndex();
    }
    _buildIndex() {
        this.byId.clear();
        this.labelsById.clear();
        this.labelTimestampsById.clear();
        this.leafId = null;
        for (const entry of this.fileEntries){
            if (entry.type === "session") continue;
            this.byId.set(entry.id, entry);
            this.leafId = entry.id;
            if (entry.type === "label") {
                if (entry.label) {
                    this.labelsById.set(entry.targetId, entry.label);
                    this.labelTimestampsById.set(entry.targetId, entry.timestamp);
                } else {
                    this.labelsById.delete(entry.targetId);
                    this.labelTimestampsById.delete(entry.targetId);
                }
            }
        }
    }
    _rewriteFile() {
        if (!this.persist || !this.sessionFile) return;
        const fd = openSync(this.sessionFile, "w");
        try {
            for (const entry of this.fileEntries){
                writeFileSync(fd, `${JSON.stringify(entry)}\n`);
            }
        } finally{
            closeSync(fd);
        }
    }
    isPersisted() {
        return this.persist;
    }
    getCwd() {
        return this.cwd;
    }
    getSessionDir() {
        return this.sessionDir;
    }
    usesDefaultSessionDir() {
        return this.sessionDir === getDefaultSessionDirPath(this.cwd);
    }
    getSessionId() {
        return this.sessionId;
    }
    getSessionFile() {
        return this.sessionFile;
    }
    _hasConversation() {
        return this.fileEntries.some((e)=>e.type === "message" && (e.message.role === "user" || e.message.role === "assistant"));
    }
    _persist(entry) {
        if (!this.persist || !this.sessionFile) return;
        if (!this.flushed) {
            if (!this._hasConversation()) return;
            const fd = openSync(this.sessionFile, "wx");
            try {
                for (const e of this.fileEntries){
                    writeFileSync(fd, `${JSON.stringify(e)}\n`);
                }
            } finally{
                closeSync(fd);
            }
            this.flushed = true;
        } else {
            appendFileSync(this.sessionFile, `${JSON.stringify(entry)}\n`);
        }
    }
    _appendEntry(entry) {
        this.fileEntries.push(entry);
        this.byId.set(entry.id, entry);
        this.leafId = entry.id;
        this._persist(entry);
    }
    appendMessage(message) {
        const entry = {
            type: "message",
            id: generateId(this.byId),
            parentId: this.leafId,
            timestamp: new Date().toISOString(),
            message
        };
        this._appendEntry(entry);
        return entry.id;
    }
    appendThinkingLevelChange(thinkingLevel) {
        const entry = {
            type: "thinking_level_change",
            id: generateId(this.byId),
            parentId: this.leafId,
            timestamp: new Date().toISOString(),
            thinkingLevel
        };
        this._appendEntry(entry);
        return entry.id;
    }
    appendModelChange(provider, modelId) {
        const entry = {
            type: "model_change",
            id: generateId(this.byId),
            parentId: this.leafId,
            timestamp: new Date().toISOString(),
            provider,
            modelId
        };
        this._appendEntry(entry);
        return entry.id;
    }
    appendUsage(kind, provider, model, usage, note) {
        const entry = {
            type: "usage",
            id: generateId(this.byId),
            parentId: this.leafId,
            timestamp: new Date().toISOString(),
            kind,
            provider,
            model,
            usage,
            ...note ? {
                note
            } : {}
        };
        this._appendEntry(entry);
        return entry;
    }
    appendCompaction(summary, firstKeptEntryId, tokensBefore, details, fromHook, usage) {
        const timestamp = new Date().toISOString();
        const systemMessage = getCurrentSystemMessage(this.buildSessionProjection().messages);
        const id = generateId(this.byId);
        const entry = {
            type: "compaction",
            id,
            parentId: this.leafId,
            timestamp,
            summary,
            firstKeptEntryId: firstKeptEntryId ?? id,
            tokensBefore,
            details,
            usage,
            fromHook,
            ...systemMessage ? {
                systemMessage: {
                    ...systemMessage,
                    timestamp: new Date(timestamp).getTime()
                }
            } : {}
        };
        this._appendEntry(entry);
        return entry.id;
    }
    appendCustomEntry(customType, data) {
        const entry = {
            type: "custom",
            customType,
            data,
            id: generateId(this.byId),
            parentId: this.leafId,
            timestamp: new Date().toISOString()
        };
        this._appendEntry(entry);
        return entry.id;
    }
    appendSessionInfo(name) {
        const sanitizedName = name.replace(/[\r\n]+/g, " ").trim();
        const entry = {
            type: "session_info",
            id: generateId(this.byId),
            parentId: this.leafId,
            timestamp: new Date().toISOString(),
            name: sanitizedName
        };
        this._appendEntry(entry);
        return entry.id;
    }
    getSessionName() {
        for(let i = this.fileEntries.length - 1; i >= 0; i--){
            const entry = this.fileEntries[i];
            if (entry.type === "session_info") {
                return entry.name?.trim() || undefined;
            }
        }
        return undefined;
    }
    appendCustomMessageEntry(customType, content, display, details) {
        const entry = {
            type: "custom_message",
            customType,
            content,
            display,
            details,
            id: generateId(this.byId),
            parentId: this.leafId,
            timestamp: new Date().toISOString()
        };
        this._appendEntry(entry);
        return entry.id;
    }
    appendContextEdit(targetId, replacement) {
        if (replacement !== null && (typeof replacement !== "object" || !("content" in replacement) || typeof replacement.content !== "string" && !Array.isArray(replacement.content))) {
            throw new Error("Context edit replacement must be null or contain string/array content");
        }
        const target = this.byId.get(targetId);
        if (!target) throw new Error(`Entry ${targetId} not found`);
        if (!this.getBranch().some((entry)=>entry.id === targetId)) {
            throw new Error(`Entry ${targetId} is not on the active branch`);
        }
        const editable = target.type === "custom_message" || target.type === "message" && (target.message.role === "user" || target.message.role === "assistant" || target.message.role === "toolResult");
        if (!editable) throw new Error(`Entry ${targetId} does not contribute editable model content`);
        const targetRole = target.type === "message" ? target.message.role : "custom";
        const normalizedReplacement = replacement !== null && (targetRole === "assistant" || targetRole === "toolResult") && typeof replacement.content === "string" ? {
            content: [
                {
                    type: "text",
                    text: replacement.content
                }
            ]
        } : replacement;
        const entry = {
            type: "context_edit",
            id: generateId(this.byId),
            parentId: this.leafId,
            timestamp: new Date().toISOString(),
            targetId,
            replacement: normalizedReplacement
        };
        this._appendEntry(entry);
        return entry.id;
    }
    getLeafId() {
        return this.leafId;
    }
    getLeafEntry() {
        return this.leafId ? this.byId.get(this.leafId) : undefined;
    }
    getEntry(id) {
        return this.byId.get(id);
    }
    getChildren(parentId) {
        const children = [];
        for (const entry of this.byId.values()){
            if (entry.parentId === parentId) {
                children.push(entry);
            }
        }
        return children;
    }
    getLabel(id) {
        return this.labelsById.get(id);
    }
    appendLabelChange(targetId, label) {
        if (!this.byId.has(targetId)) {
            throw new Error(`Entry ${targetId} not found`);
        }
        const entry = {
            type: "label",
            id: generateId(this.byId),
            parentId: this.leafId,
            timestamp: new Date().toISOString(),
            targetId,
            label
        };
        this._appendEntry(entry);
        if (label) {
            this.labelsById.set(targetId, label);
            this.labelTimestampsById.set(targetId, entry.timestamp);
        } else {
            this.labelsById.delete(targetId);
            this.labelTimestampsById.delete(targetId);
        }
        return entry.id;
    }
    getBranch(fromId) {
        const path = [];
        const startId = fromId ?? this.leafId;
        let current = startId ? this.byId.get(startId) : undefined;
        while(current){
            path.push(current);
            current = current.parentId ? this.byId.get(current.parentId) : undefined;
        }
        path.reverse();
        return path;
    }
    buildContextEntries() {
        return buildContextEntries(this.getEntries(), this.leafId, this.byId);
    }
    buildSessionProjection() {
        return buildSessionProjection(this.getEntries(), this.leafId, this.byId);
    }
    buildSessionContext() {
        const { messages, thinkingLevel, model } = this.buildSessionProjection();
        return {
            messages,
            thinkingLevel,
            model
        };
    }
    getHeader() {
        const h = this.fileEntries.find((e)=>e.type === "session");
        return h ? h : null;
    }
    getEntryCount() {
        return this.byId.size;
    }
    getEntries() {
        return this.fileEntries.filter((e)=>e.type !== "session");
    }
    getTree() {
        const entries = this.getEntries();
        const nodeMap = new Map();
        const roots = [];
        for (const entry of entries){
            const label = this.labelsById.get(entry.id);
            const labelTimestamp = this.labelTimestampsById.get(entry.id);
            nodeMap.set(entry.id, {
                entry,
                children: [],
                label,
                labelTimestamp
            });
        }
        for (const entry of entries){
            const node = nodeMap.get(entry.id);
            if (entry.parentId === null || entry.parentId === entry.id) {
                roots.push(node);
            } else {
                const parent = nodeMap.get(entry.parentId);
                if (parent) {
                    parent.children.push(node);
                } else {
                    roots.push(node);
                }
            }
        }
        const stack = [
            ...roots
        ];
        while(stack.length > 0){
            const node = stack.pop();
            node.children.sort((a, b)=>new Date(a.entry.timestamp).getTime() - new Date(b.entry.timestamp).getTime());
            stack.push(...node.children);
        }
        return roots;
    }
    branch(branchFromId) {
        if (!this.byId.has(branchFromId)) {
            throw new Error(`Entry ${branchFromId} not found`);
        }
        this.leafId = branchFromId;
    }
    resetLeaf() {
        this.leafId = null;
    }
    branchWithSummary(branchFromId, summary, details, fromHook, usage) {
        if (branchFromId !== null && !this.byId.has(branchFromId)) {
            throw new Error(`Entry ${branchFromId} not found`);
        }
        const fromId = this.leafId ?? "root";
        this.leafId = branchFromId;
        const entry = {
            type: "branch_summary",
            id: generateId(this.byId),
            parentId: branchFromId,
            timestamp: new Date().toISOString(),
            fromId,
            summary,
            details,
            usage,
            fromHook
        };
        this._appendEntry(entry);
        return entry.id;
    }
    createBranchedSession(leafId) {
        const previousSessionFile = this.sessionFile;
        const path = this.getBranch(leafId);
        if (path.length === 0) {
            throw new Error(`Entry ${leafId} not found`);
        }
        const pathWithoutLabels = [];
        const replacementByLabelId = new Map();
        const pendingLabelIds = [];
        let pathParentId = null;
        for (const entry of path){
            if (entry.type === "label") {
                pendingLabelIds.push(entry.id);
                continue;
            }
            for (const labelId of pendingLabelIds){
                replacementByLabelId.set(labelId, entry.id);
            }
            pendingLabelIds.length = 0;
            pathWithoutLabels.push(entry.type === "compaction" ? {
                ...entry,
                parentId: pathParentId,
                firstKeptEntryId: entry.firstKeptEntryId === entry.id ? entry.id : replacementByLabelId.get(entry.firstKeptEntryId) ?? entry.firstKeptEntryId
            } : {
                ...entry,
                parentId: pathParentId
            });
            pathParentId = entry.id;
        }
        const newSessionId = createSessionId();
        const timestamp = new Date().toISOString();
        const fileTimestamp = timestamp.replace(/[:.]/g, "-");
        const newSessionFile = join(this.getSessionDir(), `${fileTimestamp}_${newSessionId}.jsonl`);
        const header = {
            type: "session",
            version: CURRENT_SESSION_VERSION,
            id: newSessionId,
            timestamp,
            cwd: this.cwd,
            parentSession: this.persist ? previousSessionFile : undefined
        };
        const pathEntryIds = new Set(pathWithoutLabels.map((e)=>e.id));
        const labelsToWrite = [];
        for (const [targetId, label] of this.labelsById){
            if (pathEntryIds.has(targetId)) {
                labelsToWrite.push({
                    targetId,
                    label,
                    timestamp: this.labelTimestampsById.get(targetId)
                });
            }
        }
        if (this.persist) {
            const lastEntryId = pathWithoutLabels[pathWithoutLabels.length - 1]?.id || null;
            let parentId = lastEntryId;
            const labelEntries = [];
            for (const { targetId, label, timestamp: labelTimestamp } of labelsToWrite){
                const labelEntry = {
                    type: "label",
                    id: generateId(new Set(pathEntryIds)),
                    parentId,
                    timestamp: labelTimestamp,
                    targetId,
                    label
                };
                pathEntryIds.add(labelEntry.id);
                labelEntries.push(labelEntry);
                parentId = labelEntry.id;
            }
            this.fileEntries = [
                header,
                ...pathWithoutLabels,
                ...labelEntries
            ];
            this.sessionId = newSessionId;
            this.sessionFile = newSessionFile;
            this._buildIndex();
            if (this._hasConversation()) {
                this._rewriteFile();
                this.flushed = true;
            } else {
                this.flushed = false;
            }
            return newSessionFile;
        }
        const labelEntries = [];
        let parentId = pathWithoutLabels[pathWithoutLabels.length - 1]?.id || null;
        for (const { targetId, label, timestamp: labelTimestamp } of labelsToWrite){
            const labelEntry = {
                type: "label",
                id: generateId(new Set([
                    ...pathEntryIds,
                    ...labelEntries.map((e)=>e.id)
                ])),
                parentId,
                timestamp: labelTimestamp,
                targetId,
                label
            };
            labelEntries.push(labelEntry);
            parentId = labelEntry.id;
        }
        this.fileEntries = [
            header,
            ...pathWithoutLabels,
            ...labelEntries
        ];
        this.sessionId = newSessionId;
        this._buildIndex();
        return undefined;
    }
    static create(cwd, sessionDir, options) {
        const dir = sessionDir ? normalizePath(sessionDir) : getDefaultSessionDir(cwd);
        return new SessionManager(cwd, dir, undefined, true, options);
    }
    static open(path, sessionDir, cwdOverride) {
        const resolvedPath = resolvePath(path);
        let header = null;
        let preloadedFileEntries;
        if (cwdOverride === undefined && existsSync(resolvedPath)) {
            try {
                header = readSessionHeader(resolvedPath);
            } catch (error) {
                if (!(error instanceof SessionHeaderScanLimitError)) throw error;
                preloadedFileEntries = loadEntriesFromFile(resolvedPath);
                const firstEntry = preloadedFileEntries[0];
                header = firstEntry?.type === "session" ? firstEntry : null;
            }
        }
        const cwd = cwdOverride ?? (header ? getSessionHeaderCwd(header) : undefined) ?? process.cwd();
        const dir = sessionDir ? normalizePath(sessionDir) : resolve(resolvedPath, "..");
        return new SessionManager(cwd, dir, resolvedPath, true, undefined, preloadedFileEntries);
    }
    static continueRecent(cwd, sessionDir) {
        const dir = sessionDir ? normalizePath(sessionDir) : getDefaultSessionDir(cwd);
        const filterCwd = sessionDir !== undefined && dir !== getDefaultSessionDirPath(cwd);
        const mostRecent = findMostRecentSession(dir, filterCwd ? cwd : undefined);
        if (mostRecent) {
            return new SessionManager(cwd, dir, mostRecent, true);
        }
        return new SessionManager(cwd, dir, undefined, true);
    }
    static inMemory(cwd = process.cwd(), options, entries) {
        return new SessionManager(cwd, "", undefined, false, options, entries);
    }
    static forkFrom(sourcePath, targetCwd, sessionDir, options) {
        const resolvedSourcePath = resolvePath(sourcePath);
        const resolvedTargetCwd = resolvePath(targetCwd);
        const sourceEntries = loadEntriesFromFile(resolvedSourcePath);
        if (sourceEntries.length === 0) {
            throw new Error(`Cannot fork: source session file is empty or invalid: ${resolvedSourcePath}`);
        }
        const sourceHeader = sourceEntries.find((e)=>e.type === "session");
        if (!sourceHeader) {
            throw new Error(`Cannot fork: source session has no header: ${resolvedSourcePath}`);
        }
        const dir = sessionDir ? normalizePath(sessionDir) : getDefaultSessionDir(resolvedTargetCwd);
        if (!existsSync(dir)) {
            mkdirSync(dir, {
                recursive: true
            });
        }
        if (options?.id !== undefined) {
            assertValidSessionId(options.id);
        }
        const newSessionId = options?.id ?? createSessionId();
        const timestamp = new Date().toISOString();
        const fileTimestamp = timestamp.replace(/[:.]/g, "-");
        const newSessionFile = join(dir, `${fileTimestamp}_${newSessionId}.jsonl`);
        const newHeader = {
            type: "session",
            version: CURRENT_SESSION_VERSION,
            id: newSessionId,
            timestamp,
            cwd: resolvedTargetCwd,
            parentSession: resolvedSourcePath
        };
        writeFileSync(newSessionFile, `${JSON.stringify(newHeader)}\n`, {
            flag: "wx"
        });
        for (const entry of sourceEntries){
            if (entry.type !== "session") {
                appendFileSync(newSessionFile, `${JSON.stringify(entry)}\n`);
            }
        }
        return new SessionManager(resolvedTargetCwd, dir, newSessionFile, true);
    }
    static findById(cwd, id, sessionDir) {
        const dir = sessionDir ? normalizePath(sessionDir) : getDefaultSessionDir(cwd);
        const filterCwd = sessionDir !== undefined && dir !== getDefaultSessionDirPath(cwd);
        const resolvedCwd = resolvePath(cwd);
        try {
            for (const file of readdirSync(dir)){
                if (!file.endsWith(".jsonl")) continue;
                const path = join(dir, file);
                const header = readSessionHeaderForDiscovery(path);
                if (header?.id !== id) continue;
                if (filterCwd && !sessionCwdMatches(getSessionHeaderCwd(header), resolvedCwd)) continue;
                return path;
            }
        } catch  {}
        return undefined;
    }
    static async list(cwd, sessionDir, onProgress, signal) {
        const dir = sessionDir ? normalizePath(sessionDir) : getDefaultSessionDir(cwd);
        const filterCwd = sessionDir !== undefined && dir !== getDefaultSessionDirPath(cwd);
        const resolvedCwd = resolvePath(cwd);
        const includeSession = (session)=>!filterCwd || sessionCwdMatches(session.cwd, resolvedCwd);
        const progress = onProgress ? (loaded, total, partialSessions)=>onProgress(loaded, total, partialSessions?.filter(includeSession)) : undefined;
        const sessions = (await listSessionsFromDir(dir, progress, signal)).filter(includeSession);
        return sortSessionInfos(sessions);
    }
    static async listAll(sessionDirOrOnProgress, onProgressOrSignal, signal) {
        const customSessionDir = typeof sessionDirOrOnProgress === "string" ? normalizePath(sessionDirOrOnProgress) : undefined;
        const progress = typeof sessionDirOrOnProgress === "function" ? sessionDirOrOnProgress : typeof onProgressOrSignal === "function" ? onProgressOrSignal : undefined;
        const abortSignal = typeof sessionDirOrOnProgress === "string" || typeof onProgressOrSignal === "function" ? signal : onProgressOrSignal ?? signal;
        abortSignal?.throwIfAborted();
        if (customSessionDir) {
            return sortSessionInfos(await listSessionsFromDir(customSessionDir, progress, abortSignal));
        }
        const sessionsDir = getSessionsDir();
        try {
            if (!existsSync(sessionsDir)) return [];
            const entries = await readdir(sessionsDir, {
                withFileTypes: true
            });
            const dirs = entries.filter((entry)=>entry.isDirectory() || entry.isSymbolicLink()).map((entry)=>join(sessionsDir, entry.name));
            const dirFiles = await mapWithConcurrency(dirs, MAX_CONCURRENT_SESSION_DISCOVERY_LOADS, async (dir)=>{
                try {
                    return (await readdir(dir)).filter((file)=>file.endsWith(".jsonl")).map((file)=>join(dir, file));
                } catch  {
                    return [];
                }
            }, abortSignal);
            const allFiles = dirFiles.flat();
            const candidates = await mapWithConcurrency(allFiles, MAX_CONCURRENT_SESSION_DISCOVERY_LOADS, async (path)=>{
                try {
                    return {
                        path,
                        stats: await stat(path)
                    };
                } catch  {
                    return {
                        path
                    };
                }
            }, abortSignal);
            candidates.sort((a, b)=>(b.stats?.mtimeMs ?? Number.NEGATIVE_INFINITY) - (a.stats?.mtimeMs ?? Number.NEGATIVE_INFINITY) || basename(b.path).localeCompare(basename(a.path)));
            const totalFiles = candidates.length;
            let loaded = 0;
            let firstCandidateLoaded = false;
            const partialSessions = [];
            const results = await buildSessionInfosWithConcurrency(candidates, (info, index)=>{
                loaded++;
                if (index === 0) firstCandidateLoaded = true;
                if (info) partialSessions.push(info);
                const publishPartial = firstCandidateLoaded && (index === 0 || loaded % ALL_SESSION_LIST_PUBLISH_INTERVAL === 0 || loaded === totalFiles);
                progress?.(loaded, totalFiles, publishPartial ? sortSessionInfos([
                    ...partialSessions
                ]) : undefined);
            }, abortSignal);
            return sortSessionInfos(results.filter((info)=>info !== null));
        } catch  {
            abortSignal?.throwIfAborted();
            return [];
        }
    }
}
