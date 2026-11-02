# Connection Guardian

A Dalamud plugin for FFXIV on **Linux (XIVLauncher / Wine)**. It helps the game survive short internet
outages instead of freezing and then logging you out a minute later. It also shows what happens to the
game's connections, packet by packet.

## Why you get logged out

The game talks to the server over TCP. When the internet drops, every packet is lost, and Linux
retransmits with *exponential backoff*: it waits 0.2 s, 0.4 s, 0.8 s, … up to 120 s between attempts.
After a 20–30 s outage the next retry may be more than 10 s away. The internet is already back, but
your PC stays silent until that timer fires. In the meantime the game sees no traffic and the server
gives up on you.

## What the plugin does

On every game server connection (TCP ports 54992–55551) the plugin sets:

| Option | Value | Effect |
|---|---|---|
| `TCP_RTO_MAX_MS` | 1000 ms | Never wait more than 1 s between retries, so your PC resumes sending within ~1 s of the internet coming back. Needs Linux 6.15+. |
| `TCP_USER_TIMEOUT` | 300 s | Required with the above: with fast retries the kernel would otherwise kill the connection after ~14 s, because it gives up after 15 retries. |

It also reads the kernel's `TCP_INFO` 4×/s and logs:
- outages: no ACK from the server for 2 s
- how long each outage lasted
- how a connection ended: closed by the server, reset or timed out, or closed by the game itself

Events go to the `/cguard` window, to chat (optional), and to
`~/.xlcore/pluginConfigs/ConnectionGuardian/events.log`. That log tells you who gave up on the
connection when you are logged out.

### How it works under Wine

Wine does not pass these Linux-only options through `setsockopt`, so the plugin makes the Linux
system calls itself:
- It finds the game's own Linux pid, then a `syscall; ret` instruction inside the process's libc.
- It calls that instruction from a tiny stub.
- The stub is placed where Wine's syscall emulation does not trap it.

It then lists `/proc/self/fd` to find the game's sockets. The tuning is only applied when this
works; otherwise the window shows why it is disabled. The tuning is removed again when the plugin
is unloaded.

## Measured effect

`scripts/simulate_outage.py` measures this offline (see below). It reports the seconds from "internet
back" until data flows again. The server is stock Linux in both profiles.

| outage | stock: PC → server | with plugin: PC → server | server → PC (both) |
|---|---|---|---|
| 5 s | 1.6 s | 0.8 s | 1.6 s |
| 10 s | 3.2 s | 0.9 s | 3.3 s |
| 20 s | 6.6 s | 0.1 s | 6.6 s |
| 30 s | 22.7 s | 0.3 s | 22.7 s |
| 45 s | 8.1 s | 0.7 s | 7.7 s |

**Limitation:** the plugin can only change *your* side. Your PC → server traffic recovers within ~1 s,
so the server hears from you again almost immediately, which is what stops it from timing you out.
Server → PC traffic still follows the server's own retry schedule, so after a long outage the game
can still look frozen for a few seconds (up to ~20 s after a 30 s outage) before catching up.
If you are still logged out, `events.log` shows whether the server closed the connection or the
game did.

## Build and install

```sh
dotnet build -c Release
```

In game, open `/xlsettings` → *Experimental* → *Dev Plugin Locations*, add

```
Z:\home\astark\dev\Connection_Guardian\bin\Release\ConnectionGuardian.dll
```

and then enable *Connection Guardian* in `/xlplugins` → *Dev Tools*. Open the window with `/cguard`.

## Testing

**For real, in game:** `scripts/drop_internet.sh`

```sh
scripts/drop_internet.sh 20            # nmcli networking off for 20 s (no sudo)
scripts/drop_internet.sh -m wifi 20    # Wi-Fi radio off for 20 s (no sudo)
scripts/drop_internet.sh -m game 20    # sudo nft: silently drop only FFXIV traffic for 20 s
```

The network is always restored, even on Ctrl+C. It prints the game connections before and after.
NetworkManager needs ~5 s to reconnect Wi-Fi after `nm`/`wifi` modes, so the real outage is longer
than requested. `-m game` is the most realistic, because packets just vanish, and it is exact to
the second. Watch `/cguard` while it runs.

**Offline, no root, does not touch your network:** `scripts/simulate_outage.py`

```sh
scripts/simulate_outage.py                    # outages 5,10,20,30 s, stock vs tuned
scripts/simulate_outage.py --outages 15,45
```

It creates three throw-away network namespaces (PC ↔ router ↔ server) in a user namespace. The router
silently drops packets during the outage. Needs `unshare`, `nsenter`, `ip`, and a kernel with
`netkit` (6.7+). Only the Python standard library is used.
