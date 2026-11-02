#!/usr/bin/env bash
# Cut the internet for a few seconds, then bring it back, to test Connection Guardian in game.
#
# Usage: scripts/drop_internet.sh [-m game|nm|wifi] [-w watch_seconds] [seconds]
#        (default: -m nm -w 60 10)
#
#   game  sudo nft: silently drop only FFXIV server traffic (ports 54992-55551). Most realistic:
#         packets just vanish like during a real internet hiccup, the outage lasts exactly the given
#         time, and the rest of your internet (Discord, browser...) keeps working. Needs sudo.
#   nm    nmcli networking off/on: everything goes offline (no sudo). Less realistic: Linux sees the
#         network disappear and behaves differently than when packets are silently lost, and Wi-Fi
#         needs ~5 s to reconnect, so the outage is longer than requested.
#   wifi  nmcli radio wifi off/on: like nm, but only Wi-Fi (no sudo).
#
# After restoring, the game connections are watched for watch_seconds (state, retransmit timer,
# last data received). Everything is printed and appended to the Connection Guardian log folder,
# where the plugin picks it up and adds it to its session log.
#
# The network is always restored, including on Ctrl+C.
set -euo pipefail

mode=nm
watch=60
while getopts "m:w:h" opt; do
    case "$opt" in
        m) mode="$OPTARG" ;;
        w) watch="$OPTARG" ;;
        *) sed -n '2,20p' "$0"; exit 0 ;;
    esac
done
shift $((OPTIND - 1))
seconds="${1:-10}"
[[ "$seconds" =~ ^[0-9]+([.][0-9]+)?$ ]] || { echo "seconds must be a number" >&2; exit 2; }
[[ "$watch" =~ ^[0-9]+$ ]] || { echo "watch_seconds must be a whole number" >&2; exit 2; }
[[ $mode =~ ^(game|nm|wifi)$ ]] || { echo "unknown mode: $mode (use game, nm or wifi)" >&2; exit 2; }

ports="54992-55551"
table="connection_guardian_test"
restored=0

# Same folder as the plugin's session logs, so the plugin merges this log into its own.
# Looks in XIVLauncher.Core's native and Flatpak locations; override with CG_LOG_DIR.
log_dir="${CG_LOG_DIR:-}"
if [[ -z "$log_dir" ]]; then
    for xl in "$HOME/.xlcore" "$HOME/.var/app/dev.goats.xivlauncher/data/xlcore"; do
        if [[ -d "$xl/pluginConfigs" ]]; then
            log_dir="$xl/pluginConfigs/ConnectionGuardian/logs"
            break
        fi
    done
fi
if [[ -z "$log_dir" ]]; then
    log_dir="$(cd "$(dirname "$0")/.." && pwd)/logs"
    echo "XIVLauncher folder not found; logging to $log_dir (send this file too, or set CG_LOG_DIR)." >&2
fi
mkdir -p "$log_dir"
log_file="$log_dir/drop_internet.log"

# "<date time.ms> <EVENT> <message>"; the plugin recognises the CUT and RESTORED events.
log() {
    local event="$1"; shift
    local line
    line="$(date '+%Y-%m-%d %H:%M:%S.%3N') $event $*"
    echo "$line"
    echo "$line" >> "$log_file"
}

game_connections() {
    # Without root, ss only shows part of TCP_INFO; game mode has sudo anyway.
    local ss=(ss)
    [[ $mode == game ]] && ss=(sudo ss)
    "${ss[@]}" -Htin state established "( dport >= :54992 and dport <= :55551 )" 2>/dev/null | awk '{
        info = ""
        for (i = 5; i <= NF; i++)
            if ($i ~ /^(rto|backoff|rtt|lastsnd|lastrcv|lastack|unacked|retrans|lost):/) info = info " " $i
        print $3 " -> " $4 " sendq=" $2 " recvq=" $1 info
    }' || true
}

log_connections() {
    local label="$1" lines
    lines="$(game_connections)"
    if [[ -z "$lines" ]]; then
        log CONN "$label: no game connection"
    else
        while IFS= read -r line; do log CONN "$label: $line"; done <<< "$lines"
    fi
}

cut() {
    case "$mode" in
        nm) nmcli networking off ;;
        wifi) nmcli radio wifi off ;;
        game)
            sudo nft -f - <<EOF
table inet $table {
    chain out { type filter hook output priority -10; policy accept; tcp dport $ports drop; }
    chain in { type filter hook input priority -10; policy accept; tcp sport $ports drop; }
}
EOF
            ;;
    esac
}

restore() {
    [[ $restored == 1 ]] && return
    restored=1
    case "$mode" in
        nm) nmcli networking on ;;
        wifi) nmcli radio wifi on ;;
        game) sudo nft delete table inet "$table" 2>/dev/null || true ;;
    esac
    log RESTORED "network restored (mode $mode)"
}
trap restore EXIT
trap 'echo; log ABORT "interrupted by user"; exit 130' INT TERM

if [[ $mode == game ]]; then
    command -v nft >/dev/null || { echo "nft is not installed; install nftables or use '-m nm'." >&2; exit 1; }
    if [[ ! -d /lib/modules/$(uname -r) ]]; then
        echo "Your kernel was updated but not rebooted: running $(uname -r), but /lib/modules only has" \
             "$(ls /lib/modules | tr '\n' ' ')." >&2
        echo "The nf_tables module cannot be loaded until you reboot. Use '-m nm' or reboot first." >&2
        restored=1
        exit 1
    fi
    sudo -v  # ask for the password now, not in the middle of the outage
    sudo nft delete table inet "$table" 2>/dev/null || true
    if ! sudo nft list tables >/dev/null; then
        echo "nft does not work on this system, cannot drop game traffic. Use '-m nm' instead." >&2
        restored=1
        exit 1
    fi
else
    command -v nmcli >/dev/null || { echo "nmcli (NetworkManager) not found; use '-m game'." >&2; exit 1; }
fi

echo "Logging to $log_file"
log START "test: mode $mode, outage ${seconds}s, watch ${watch}s, kernel $(uname -r)"
log_connections "before"

log CUT "cutting network (mode $mode) for ${seconds}s"
if ! cut; then
    log ERROR "failed to cut the network; nothing was changed"
    restored=1
    exit 1
fi

end_ms=$(( $(date +%s%3N) + $(awk -v s="$seconds" 'BEGIN { printf "%d", s * 1000 }') ))
while :; do
    left=$(( end_ms - $(date +%s%3N) ))
    (( left <= 0 )) && break
    sleep "$(awk -v ms=$(( left < 5000 ? left : 5000 )) 'BEGIN { printf "%.3f", ms / 1000 }')"
    left=$(( end_ms - $(date +%s%3N) ))
    (( left > 0 )) && log_connections "during outage, $(( (left + 999) / 1000 ))s left"
done
restore
start=$(date +%s.%N)

elapsed() { awk -v s="$start" -v e="$(date +%s.%N)" 'BEGIN { printf "%.1f", e - s }'; }

if [[ $mode != game ]]; then
    nm-online -q -t 60 || log NET "NetworkManager did not report online within 60s"
    until ping -c1 -W1 1.1.1.1 >/dev/null 2>&1; do
        if (( $(date +%s) - ${start%.*} > 60 )); then log NET "still no internet after 60s"; break; fi
        sleep 0.2
    done
    log NET "internet reachable again $(elapsed)s after restore"
fi

for (( i = 0; i < watch; i += 2 )); do
    log_connections "+$(elapsed)s after restore"
    sleep 2
done
log END "test finished"
