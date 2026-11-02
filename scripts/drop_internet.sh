#!/usr/bin/env bash
# Cut the internet for a few seconds, then bring it back, to test Connection Guardian in game.
#
# Usage: scripts/drop_internet.sh [-m nm|wifi|game] [seconds]      (default: -m nm 10)
#
#   nm    nmcli networking off/on: everything goes offline, as when the router drops (no sudo)
#   wifi  nmcli radio wifi off/on: only Wi-Fi is switched off (no sudo)
#   game  sudo nft: silently drop only FFXIV server traffic (ports 54992-55551) for the given time.
#         This is the most faithful "packets just vanish" outage, and the rest of your internet
#         (Discord, browser...) keeps working. Asks for your sudo password.
#
# The network is always restored, including on Ctrl+C. Watch /cguard in game while this runs.
set -euo pipefail

mode=nm
while getopts "m:h" opt; do
    case "$opt" in
        m) mode="$OPTARG" ;;
        *) sed -n '2,13p' "$0"; exit 0 ;;
    esac
done
shift $((OPTIND - 1))
seconds="${1:-10}"
[[ "$seconds" =~ ^[0-9]+([.][0-9]+)?$ ]] || { echo "seconds must be a number" >&2; exit 2; }

ports="54992-55551"
table="connection_guardian_test"
restored=0

ts() { date +%H:%M:%S.%3N; }
log() { echo "[$(ts)] $*"; }

game_connections() {
    ss -Htn state established "( dport >= :54992 and dport <= :55551 )" 2>/dev/null \
        | awk '{print "    " $3 " -> " $4}' || true
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
        *) echo "unknown mode: $mode (use nm, wifi or game)" >&2; exit 2 ;;
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
    log "network restored"
}
trap restore EXIT
trap 'echo; exit 130' INT TERM

if [[ $mode == game ]]; then
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
fi

echo "Game connections before:"
game_connections | grep . || echo "    (none - are you logged in?)"

log "cutting network (mode: $mode) for ${seconds}s"
if ! cut; then
    echo "Failed to cut the network; nothing was changed." >&2
    restored=1
    exit 1
fi
sleep "$seconds"
restore
start=$(date +%s.%N)

if [[ $mode != game ]]; then
    log "waiting for NetworkManager to reconnect..."
    nm-online -q -t 60 || log "NetworkManager did not report online within 60s"
    until ping -c1 -W1 1.1.1.1 >/dev/null 2>&1; do
        if (($(date +%s) - ${start%.*} > 60)); then log "still no internet after 60s"; break; fi
        sleep 0.2
    done
    log "internet reachable again $(awk -v s="$start" -v e="$(date +%s.%N)" "BEGIN{printf \"%.1f\", e-s}")s after restore"
fi

sleep 2
echo "Game connections after:"
game_connections | grep . || echo "    (none - the game connection did not survive)"
