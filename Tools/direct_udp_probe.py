#!/usr/bin/env python3
"""Raw UDP reachability probe for the dedicated server.

server mode: echo every datagram back on each port for N seconds.
    direct_udp_probe.py server PORT[,PORT...] SECONDS
client mode: send datagrams at a rate, count echoes, print one result line.
    direct_udp_probe.py client HOST PORT SECONDS RATE KIND
KIND:
    rand  40 random bytes
    zero  40 zero bytes
    text  readable ASCII
    big   1200 random bytes
    awg   the AmneziaWG shape that passes on the same link: 4 junk packets
          of 10-50 bytes, a 12-byte random prefix + WireGuard handshake
          (type 1, 148 bytes), then 12-byte prefix + type 4 transport data
    quic  QUIC v1 look: 1200-byte long-header Initial packets first,
          then short-header packets
"""
import os
import random
import select
import socket
import struct
import sys
import time


def server(ports, secs):
    socks = []
    for p in ports:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.bind(("0.0.0.0", p))
        except OSError as e:
            print("port", p, "bind failed:", e)
            continue
        socks.append(s)
    end = time.time() + secs
    n = 0
    while time.time() < end and socks:
        ready, _, _ = select.select(socks, [], [], 1)
        for s in ready:
            data, addr = s.recvfrom(4096)
            n += 1
            s.sendto(data, addr)
    print("echoed", n)


def awg(i):
    if i < 4:
        return os.urandom(random.randint(10, 50))
    prefix = os.urandom(12)
    if i == 4:
        return prefix + struct.pack("<I", 1) + os.urandom(144)
    return prefix + struct.pack("<I", 4) + os.urandom(4) + struct.pack("<Q", i) + os.urandom(64)


def quic(i, dcid):
    if i < 3:
        head = bytes([0xC3]) + b"\x00\x00\x00\x01" + bytes([8]) + dcid + bytes([8]) + os.urandom(8) + b"\x00"
        return head + os.urandom(1200 - len(head))
    return bytes([0x40 | random.randint(0, 0x3F)]) + dcid + os.urandom(60)


def payload(kind, i, dcid):
    if kind == "zero":
        return bytes(40)
    if kind == "text":
        return (b"BLITZ probe %d " % i).ljust(40, b".")
    if kind == "big":
        return os.urandom(1200)
    if kind == "awg":
        return awg(i)
    if kind == "quic":
        return quic(i, dcid)
    return os.urandom(40)


def client(host, port, secs, rate, kind):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setblocking(False)
    dcid = os.urandom(8)
    t0 = time.time()
    sent = got = 0
    first = last = None
    while time.time() - t0 < secs:
        try:
            s.sendto(payload(kind, sent, dcid), (host, port))
            sent += 1
        except OSError:
            pass
        time.sleep(1.0 / rate)
        while True:
            try:
                s.recv(4096)
            except (BlockingIOError, ConnectionRefusedError):
                break
            got += 1
            now = round(time.time() - t0, 1)
            first = now if first is None else first
            last = now
    share = 100.0 * got / sent if sent else 0.0
    if got == 0:
        verdict = "NONE"
    elif last < secs * 0.7:
        verdict = "CUT"  # replies stopped early: the flow was killed
    elif share >= 90:
        verdict = "PASS"
    else:
        verdict = "LOSSY"  # replies to the end, just a lossy link
    print("udp port=%-5d %-4s rate=%-3g sent=%d got=%d (%.0f%%) first=%s last=%s %s"
          % (port, kind, rate, sent, got, share, first, last, verdict))


if __name__ == "__main__":
    if sys.argv[1] == "server":
        server([int(p) for p in sys.argv[2].split(",")], float(sys.argv[3]))
    else:
        client(sys.argv[2], int(sys.argv[3]), float(sys.argv[4]), float(sys.argv[5]), sys.argv[6])
