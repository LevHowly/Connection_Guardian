# Connection Guardian

**Dalamud custom repository URL:**

```text
https://raw.githubusercontent.com/LevHowly/Connection_Guardian/main/pluginmaster.json
```

In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**, paste this URL,
tick the checkbox, click **Save**, then install **Connection Guardian** from `/xlplugins`.
No download, build or script is needed. It works on **Linux (XIVLauncher / Wine)** only.

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

It also reads the kernel's `TCP_INFO` 4×/s and records everything in a **session log** (see
[Logs](#logs)): outages, when each direction recovered, how connections ended, the Linux network
state, and the game's login/logout events.

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
If you are still logged out, the session log shows whether the server closed the connection or the
game did.

## For testers: what to do and what to send back

Thanks for helping! The goal is one zip file that answers every question, so there is no back and forth.

1. **Install:** add the repository URL at the top of this page (`/xlsettings` → *Experimental* →
   *Custom Plugin Repositories*, tick it, *Save*), then install *Connection Guardian* in `/xlplugins`.
2. **Check it runs:** `/cguard` opens the window. The first line should be green ("Active ...") and,
   once you are logged in, the game connections should show *Tuned: yes*. If the line is red, just
   send the logs (step 5); the reason is in there.
3. **Play normally.** Everything is logged automatically. If something happens (freeze, NPC windows
   not opening, "connection lost", logout), add a note right away: type it in the window's note field
   or use `/cguard note what happened`. Notes are timestamped, so a few words are enough.
4. **Keep playing until you have had a few drops**, ideally including one that logged you out.
   Nothing to do during a drop: the plugin records it on its own. Reloading or restarting the game is
   fine; every session gets its own log file. *(Optional, only if you are on Linux and comfortable with a
   terminal: `scripts/drop_internet.sh` from this repo cuts the internet on demand; see [Testing](#testing).)*
5. **Send the logs:** click **Create zip to share** in the `/cguard` window. It creates
   `ConnectionGuardian-logs-<date>.zip` and opens its folder. Send that file. It contains every
   session log and the script log; home directory paths are replaced with `~`.

## Logs

One file per game session, in `~/.xlcore/pluginConfigs/ConnectionGuardian/logs/session-<date>.log`
(the last 30 are kept). The window has buttons to open the current file, the folder, and to create the
zip. Each file has:

- a **header**: plugin, Dalamud, distro, kernel, Wine build, the kernel's TCP settings, plugin settings;
- **events**, tagged by category:
  `CONN` connection found or state change, `TUNE` settings applied and read back,
  `OUTAGE` no ACK from the server for 2 s, `RECOVER` each direction working again,
  `SUMMARY` one line per outage with all timings, `CLOSE` who ended a connection (server / reset or
  timeout / game), `SILENCE` server sent nothing for 5 s, `NET` default route, interface state and Wi-Fi
  link, `GAME` login/logout (with the logout code)/zone change, `SCRIPT` lines from `drop_internet.sh`,
  `NOTE` your notes, `OTHER` the game's TCP connections outside the monitored ports;
- **SAMPLE** lines with the kernel's TCP state of every game connection: every 10 s normally, and
  every 250 ms during an outage and for 30 s after it. The legend is in the header.

What to look for: the `SUMMARY` line of each outage gives "PC->server back" (fixed by the plugin) and
"server->PC back" (the server's own retry timer); a `CLOSE` line followed by `GAME ... LOGGED OUT`
says which side gave up.

## Build and install from source

```sh
dotnet build -c Release          # needs the .NET 10 SDK
```

In game, open `/xlsettings` → *Experimental* → *Dev Plugin Locations*, add the DLL path written as a
Wine path, e.g. `Z:\home\<you>\Connection_Guardian\bin\Release\ConnectionGuardian.dll`, then enable
*Connection Guardian* in `/xlplugins` → *Dev Tools*. Open the window with `/cguard`;
`/cguard note <text>` adds a note to the log.

**Publishing a release:** bump `<Version>` in `ConnectionGuardian.csproj`, run
`scripts/package-release.sh` (builds `build/release/latest.zip` and updates `pluginmaster.json`), commit,
push, and create a GitHub release `v<version>` with `latest.zip` attached.

## Testing

**For real, in game:** `scripts/drop_internet.sh`

```sh
scripts/drop_internet.sh 20            # nmcli networking off for 20 s (no sudo)
scripts/drop_internet.sh -m wifi 20    # Wi-Fi radio off for 20 s (no sudo)
scripts/drop_internet.sh -m game 20    # sudo nft: silently drop only FFXIV traffic for 20 s
```

The network is always restored, even on Ctrl+C. The script logs the game connections before,
during (every 5 s) and for 60 s after the outage (`-w <seconds>` to change). Its timeline goes to
`drop_internet.log` in the plugin's log folder, and the plugin copies it into the session log and
times each recovery from the moment the network was restored.
NetworkManager needs ~5 s to reconnect Wi-Fi after `nm`/`wifi` modes, so the real outage is longer
than requested. `-m game` is the most realistic, because packets just vanish, and it is exact to
the second. It needs `nft` and a kernel whose modules are installed: after a kernel update, reboot
first. Watch `/cguard` while it runs.

**Offline, no root, does not touch your network:** `scripts/simulate_outage.py`

```sh
scripts/simulate_outage.py                    # outages 5,10,20,30 s, stock vs tuned
scripts/simulate_outage.py --outages 15,45
```

It creates three throw-away network namespaces (PC ↔ router ↔ server) in a user namespace. The router
silently drops packets during the outage. Needs `unshare`, `nsenter`, `ip`, and a kernel with
`netkit` (6.7+). Only the Python standard library is used.
