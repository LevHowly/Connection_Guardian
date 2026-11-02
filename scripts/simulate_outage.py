#!/usr/bin/env python3
"""Offline proof that the Connection Guardian socket tuning lets a TCP connection survive a short
outage, without root and without touching your real network.

Three throw-away network namespaces (your PC <-> router <-> "game server") are created inside an
unprivileged user namespace. A game-like TCP connection sends a heartbeat both ways every 0.2s.
The router then silently drops everything for N seconds (what an ISP / Wi-Fi hiccup looks like to
TCP) and we measure how long each direction needs to resume once the network is back.

Profiles:
  default  stock Linux TCP on both ends
  tuned    your PC applies exactly what the plugin applies to the game sockets
           (TCP_RTO_MAX_MS + TCP_USER_TIMEOUT); the server stays stock, like Square Enix's

Usage: simulate_outage.py [--outages 5,10,20,30] [--rto-max-ms 1000] [--user-timeout-ms 300000]
"""
import argparse
import os
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time

PORT = 55025
CLIENT_IP = "10.9.1.2"
SERVER_IP = "10.9.2.2"
HEARTBEAT = 0.2
GAP = 1.0
TCP_USER_TIMEOUT = 18
TCP_RTO_MAX_MS = 44
MSG = struct.Struct("!dd")  # sender timestamp, when the sender first heard its peer again after a gap


def sh(*cmd):
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode:
        sys.exit(f"command failed: {' '.join(cmd)}\n{result.stderr.strip()}")
    return result


def in_ns(pid, *cmd):
    return sh("nsenter", "-t", str(pid), "-n", *cmd)


def recv_exact(sock, size):
    data = b""
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("peer closed")
        data += chunk
    return data


def new_state():
    return {"last_rx": time.monotonic(), "resumed_at": 0.0, "rx_log": [], "peer_resumed_at": 0.0}


def pump(sock, state, stop):
    """Send a heartbeat every HEARTBEAT seconds and record when the peer is heard again after a gap."""
    def sender():
        while not stop.is_set():
            try:
                sock.sendall(MSG.pack(time.monotonic(), state["resumed_at"]))
            except OSError as exc:
                state["error"] = repr(exc)
                return
            time.sleep(HEARTBEAT)

    threading.Thread(target=sender, daemon=True).start()
    while not stop.is_set():
        try:
            _, peer_resumed_at = MSG.unpack(recv_exact(sock, MSG.size))
        except (OSError, ConnectionError) as exc:
            state["error"] = repr(exc)
            return
        now = time.monotonic()
        if now - state["last_rx"] > GAP:
            state["resumed_at"] = now
        state["rx_log"].append(now)
        state["last_rx"] = now
        state["peer_resumed_at"] = peer_resumed_at


def run_server():
    while True:
        try:
            srv = socket.create_server((SERVER_IP, PORT), reuse_port=True)
            break
        except OSError:
            time.sleep(0.1)
    while True:
        conn, _ = srv.accept()
        threading.Thread(target=pump, args=(conn, new_state(), threading.Event()), daemon=True).start()


def set_outage(router_pid, active):
    policy = "blackhole" if active else "forward"
    for dev in ("rc", "rs"):
        in_ns(router_pid, "ip", "link", "set", dev, "type", "netkit", policy)


def one_outage(router_pid, outage, tuned, args):
    conn = socket.create_connection((SERVER_IP, PORT), timeout=5)
    conn.settimeout(None)
    if tuned:
        conn.setsockopt(socket.IPPROTO_TCP, TCP_RTO_MAX_MS, args.rto_max_ms)
        conn.setsockopt(socket.IPPROTO_TCP, TCP_USER_TIMEOUT, args.user_timeout_ms)
    state = new_state()
    stop = threading.Event()
    threading.Thread(target=pump, args=(conn, state, stop), daemon=True).start()
    time.sleep(3)

    set_outage(router_pid, True)
    time.sleep(outage)
    set_outage(router_pid, False)
    up = time.monotonic()

    s2c = c2s = None
    while time.monotonic() - up < args.give_up and (s2c is None or c2s is None) and "error" not in state:
        if s2c is None and state["resumed_at"] > up:
            s2c = state["resumed_at"] - up
        if c2s is None and state["peer_resumed_at"] > up:
            c2s = state["peer_resumed_at"] - up
        time.sleep(0.05)
    stop.set()
    conn.close()
    return s2c, c2s, state.get("error")


def fmt(value, give_up):
    return f"{value:5.1f}s" if value is not None else f">{give_up:.0f}s"


def run_client(args):
    sh("ip", "link", "set", "lo", "up")
    router = subprocess.Popen(["unshare", "-n", "sleep", "infinity"])
    server = subprocess.Popen(["unshare", "-n", sys.executable, os.path.abspath(__file__), "--role", "server"])
    try:
        time.sleep(0.5)
        r = router.pid
        in_ns(r, "ip", "link", "set", "lo", "up")
        in_ns(r, "ip", "link", "add", "rc", "type", "netkit", "mode", "l3", "peer", "name", "c0")
        in_ns(r, "ip", "link", "add", "rs", "type", "netkit", "mode", "l3", "peer", "name", "s0")
        in_ns(r, "ip", "link", "set", "c0", "netns", str(os.getpid()))
        in_ns(r, "ip", "link", "set", "s0", "netns", str(server.pid))
        in_ns(r, "ip", "addr", "add", "10.9.1.1/24", "dev", "rc")
        in_ns(r, "ip", "addr", "add", "10.9.2.1/24", "dev", "rs")
        in_ns(r, "ip", "link", "set", "rc", "up")
        in_ns(r, "ip", "link", "set", "rs", "up")
        in_ns(r, "sysctl", "-qw", "net.ipv4.ip_forward=1")

        sh("ip", "addr", "add", f"{CLIENT_IP}/24", "dev", "c0")
        sh("ip", "link", "set", "c0", "up")
        sh("ip", "route", "add", "default", "dev", "c0")

        in_ns(server.pid, "ip", "link", "set", "lo", "up")
        in_ns(server.pid, "ip", "addr", "add", f"{SERVER_IP}/24", "dev", "s0")
        in_ns(server.pid, "ip", "link", "set", "s0", "up")
        in_ns(server.pid, "ip", "route", "add", "default", "dev", "s0")
        time.sleep(0.5)

        outages = [float(x) for x in args.outages.split(",")]
        print("Seconds from 'network is back' until data flows again (heartbeat every 0.2s):")
        print(f"{'profile':8} {'outage':>6}  {'your PC -> server':>17}  {'server -> your PC':>17}")
        for name in ("default", "tuned"):
            for outage in outages:
                s2c, c2s, err = one_outage(r, outage, name == "tuned", args)
                extra = f"  connection died: {err}" if err else ""
                print(f"{name:8} {outage:5.0f}s  {fmt(c2s, args.give_up):>17}  {fmt(s2c, args.give_up):>17}{extra}",
                      flush=True)
    finally:
        router.kill()
        server.kill()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--role", choices=["outer", "client", "server"], default="outer")
    parser.add_argument("--outages", default="5,10,20,30", help="comma separated outage durations in seconds")
    parser.add_argument("--rto-max-ms", type=int, default=1000)
    parser.add_argument("--user-timeout-ms", type=int, default=300000)
    parser.add_argument("--give-up", type=float, default=90.0, help="stop waiting after this many seconds")
    args = parser.parse_args()

    if args.role == "server":
        run_server()
    elif args.role == "client":
        run_client(args)
    else:
        for tool in ("unshare", "nsenter", "ip", "sysctl"):
            if not shutil.which(tool):
                sys.exit(f"missing required tool: {tool}")
        os.execvp("unshare", ["unshare", "-rn", sys.executable, os.path.abspath(__file__), "--role", "client"]
                  + sys.argv[1:])


if __name__ == "__main__":
    main()
