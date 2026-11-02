#!/usr/bin/env python3
"""Spike: log alice's WebSocket messages (LibraryChanged is fed by ILibraryManager.ItemAdded/
ItemUpdated/ItemRemoved via LibraryChangedNotifier). Header auth; stdlib-only minimal client.

  ws_listen.py <seconds> <logfile>
"""
import base64
import json
import os
import socket
import struct
import sys
import time

import jf

secs, logf = float(sys.argv[1]), sys.argv[2]
tok = jf.token("alice")
s = socket.create_connection(("localhost", 8101))
key = base64.b64encode(os.urandom(16)).decode()
s.sendall((f"GET /socket?deviceId=spike HTTP/1.1\r\nHost: localhost:8101\r\nUpgrade: websocket\r\n"
           f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n"
           f"Authorization: {jf.AUTH}, Token=\"{tok}\"\r\n\r\n").encode())
buf = b""
while b"\r\n\r\n" not in buf:
    buf += s.recv(4096)
head, buf = buf.split(b"\r\n\r\n", 1)
assert b" 101 " in head.split(b"\r\n")[0], head


def recv_exact(n):
    global buf
    while len(buf) < n:
        chunk = s.recv(65536)
        if not chunk:
            raise EOFError
        buf += chunk
    out, buf = buf[:n], buf[n:]
    return out


def send(obj):
    data = json.dumps(obj).encode()
    mask = os.urandom(4)
    hdr = bytes([0x81]) + (bytes([0x80 | len(data)]) if len(data) < 126 else bytes([0x80 | 126]) + struct.pack(">H", len(data)))
    s.sendall(hdr + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))


s.settimeout(1.0)
end = time.time() + secs
last_ka = 0
with open(logf, "a", encoding="utf-8") as out:
    while time.time() < end:
        if time.time() - last_ka > 20:
            send({"MessageType": "KeepAlive"})
            last_ka = time.time()
        try:
            b1, b2 = recv_exact(2)
        except socket.timeout:
            continue
        n = b2 & 0x7F
        if n == 126:
            n = struct.unpack(">H", recv_exact(2))[0]
        elif n == 127:
            n = struct.unpack(">Q", recv_exact(8))[0]
        s.settimeout(None)
        payload = recv_exact(n)
        s.settimeout(1.0)
        if b1 & 0x0F == 1:
            msg = json.loads(payload)
            if msg.get("MessageType") not in ("KeepAlive", "ForceKeepAlive"):
                out.write(json.dumps({"t": time.strftime("%H:%M:%S"), **msg}) + "\n")
                out.flush()
