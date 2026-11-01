# Reaching a durable device from another machine

PiSharp-only and experimental (`PISHARP_DURABLE001`; decisions [0008](decisions/0008-durable-network-host-and-web.md) and
[0010](decisions/0010-durable-application-contracts.md), M6). A device is `pisharp durable serve` on one computer. Whoever holds its token
can run commands as the user who started it, in its start directories, with that user's model credentials. Keep both the listener and
the token close.

## The default: loopback

`serve` listens on `127.0.0.1:4780` (`host` and `port` in the data directory's `device.json`; `--host` and `--port` change them, `--save`
keeps the change). Nothing off the machine can connect. `serve` prints a warning when the host is anything else, and
`pisharp durable doctor` reports the `config` check as `warn` with a pointer to this page.

To reach a device from elsewhere, keep it on loopback and bring the connection to it: an SSH tunnel (simplest), or a reverse proxy that
terminates TLS. Bind it beyond loopback only on a network you trust completely: plain HTTP sends the token in clear text on every request.

## Token custody

- The token lives in the data directory's `token` file, created readable by its owner only. `doctor` fails a weak token and, on Unix,
  warns when others can read the file (`doctor --repair` makes it owner-only). Doctor never prints it.
- `pisharp durable token` prints it on purpose; `pisharp durable token --rotate` replaces it, and every client must then get the new one.
- On the device itself, CLI commands read the token file. Against another device (`--device <url>`), the token comes from the
  `PISHARP_DEVICE_TOKEN` environment variable or `--token-file <path>`. It is never an argument and never part of the URL: the CLI
  refuses both, so it stays out of shell history, process listings and proxy logs.
- The web hub keeps device tokens encrypted with Data Protection (DPAPI on Windows) and adds them to proxied requests itself; the browser
  never sees them.
- Service definitions (below) hold no token; the device reads its token file. Backups leave it out unless `--include-secrets` asks for it.

## SSH tunnel

On the computer you work from, forward a local port to the device's loopback listener:

```bash
ssh -N -L 14780:127.0.0.1:4780 you@device-host
```

Copy the token once into a file only you can read, then point the CLI at the tunnel:

```bash
ssh you@device-host pisharp durable token > ~/.pi/device-host.token && chmod 600 ~/.pi/device-host.token
```

```bash
pisharp durable attach --device http://127.0.0.1:14780/ --token-file ~/.pi/device-host.token
```

A local port other than 4780 avoids a clash with a device on your own computer. In the web hub, add a device with the URL
`http://127.0.0.1:14780/` and paste the token in the Devices page. When the tunnel drops, clients see the device as offline and
reconnect when it is back; `attach` prints `[disconnected: ...]` and `[reconnected]` and never resends a message on its own.

## Reverse proxy with TLS

The device stays on `127.0.0.1:4780`; the proxy terminates TLS on the public name and forwards to it. The device does not need forwarded
headers: it builds no absolute URLs and decides nothing by client address, and the bearer token is its only authority. The proxy must:

- pass `Authorization` (or `X-DPI-Token`) and `Idempotency-Key` through unchanged, and keep `Authorization` out of its access logs;
- not buffer `text/event-stream` responses, and allow idle reads of well over 45 s: the device sends a `: ping` after 15 s of silence and
  clients treat 45 s without bytes as a dead connection;
- accept request bodies of at least 1 MiB (the device refuses JSON bodies over 256 KB itself) and allow long requests: a waiting send or
  a source check can take minutes, and the hub's own proxy allows 330 s.

Caddy, which obtains and renews the certificate itself:

```
device.example.com {
    reverse_proxy 127.0.0.1:4780 {
        flush_interval -1
    }
}
```

nginx, with a certificate you manage:

```
server {
    listen 443 ssl;
    server_name device.example.com;
    ssl_certificate     /etc/ssl/device.example.com.crt;
    ssl_certificate_key /etc/ssl/device.example.com.key;
    client_max_body_size 1m;
    location / {
        proxy_pass http://127.0.0.1:4780;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_buffering off;
        proxy_read_timeout 1h;
        proxy_send_timeout 1h;
    }
}
```

Clients then use `--device https://device.example.com/` with `PISHARP_DEVICE_TOKEN` or `--token-file`. Put nothing else on that name: the
token is the only check between the internet and a shell on the device, so rotate it if it may have leaked, and consider limiting the
proxy to known client addresses or a VPN.

## Running as a service

`pisharp durable service systemd|launchd|windows [--out <dir>] [--data-dir <dir>] [--exec <path>]` prints, or writes into `--out`, a
definition that runs `durable serve --data-dir <dir>` as you, and the commands that would install it. It installs nothing, never
overwrites a file and holds no token. The device keeps the listener from `device.json`, so a service is as local as `serve` is.

| Manager | Definition | Starts | Stops |
| --- | --- | --- | --- |
| systemd (user unit) | `pisharp-durable.service` | at login; `loginctl enable-linger` keeps it after logout | `systemctl --user stop` sends SIGTERM; the device shuts down as on Ctrl+C |
| launchd (LaunchAgent) | `dev.pisharp.durable.plist` | at login; restarted after a failure | `launchctl bootout` sends SIGTERM |
| Windows | `pisharp-durable-task.ps1` registers a scheduled task | at logon; restarted every minute after a failure | `Stop-ScheduledTask` ends the process |

systemd keeps the device's output in the journal (`journalctl --user -u pisharp-durable`). The launchd and Windows definitions run
`serve --log <data>/logs/device.log`: the device writes its own output and its server's warnings there, each line with a UTC timestamp,
and rotates the file at 10 MiB, keeping three older files (`device.log.1` to `.3`). launchd's own `<data>/logs/launchd.log` only gets what
the runtime prints outside that log, such as a crash. `serve --log <file>` works the same way when you start the device by hand. Backups
leave `logs/` out.

Windows uses a per-user scheduled task rather than a Windows service: the console binary does not speak the service control protocol, and
the device needs your profile (Pi settings, model credentials, start directories), which a service account would not have. Ending the
process is not a graceful stop, but durable state is committed transaction by transaction, so the next start resumes running work as it
does after a crash.

## Checking it

- `pisharp durable doctor` reads the data directory without writing to it: the lock, free space, catalog and session stores, schedules,
  trigger credentials, budget reservations, blobs, retention, failing sources, fetches left in progress, the listener and the token.
  `--probe` also calls the running device's `/v1/device`; `--verify-blobs` hashes every blob; `--repair` (device stopped) migrates the
  catalog and collects unreferenced blobs. `--json` gives the same checks as data; the exit code is 1 when a check fails.
- `pisharp durable backup create --out <dir>` copies a stopped device's data with SQLite's backup API and a manifest of sizes and hashes;
  `backup verify <dir>` checks it; `backup restore <dir> --data-dir <empty dir>` verifies, then restores. A backup taken without
  `--include-secrets` restores without a token; `serve` creates a new one.
