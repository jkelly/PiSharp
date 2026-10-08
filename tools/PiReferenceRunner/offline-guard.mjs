import http from "node:http";
import https from "node:https";
import net from "node:net";
import dgram from "node:dgram";
import dns from "node:dns";
import childProcess from "node:child_process";
import { syncBuiltinESMExports } from "node:module";

function denied() { throw new Error("Reference capture prohibits network and child-process access"); }
globalThis.fetch = denied;
for (const module of [http, https]) { module.request = denied; module.get = denied; }
net.connect = denied;
net.createConnection = denied;
net.Socket.prototype.connect = denied;
dgram.createSocket = denied;
dns.lookup = denied;
dns.resolve = denied;
for (const name of ["exec", "execFile", "spawn", "fork", "execSync", "execFileSync", "spawnSync"]) childProcess[name] = denied;
syncBuiltinESMExports();
