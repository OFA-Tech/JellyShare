#!/usr/bin/env python3
"""Spike-only HTTP file server with Range support that logs every request's headers.

Usage: rangeserver.py <root_dir> <port> <log_file>
"""
import http.server
import json
import os
import sys
import time
import urllib.parse

ROOT, PORT, LOG = sys.argv[1], int(sys.argv[2]), sys.argv[3]


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_request_headers(self, status, rng):
        entry = {"t": time.strftime("%H:%M:%S"), "method": self.command, "path": self.path,
                 "status": status, "range_served": rng, "client": self.client_address[0],
                 "headers": dict(self.headers.items())}
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry) + "\n")

    def do_HEAD(self):
        self.serve(head=True)

    def do_GET(self):
        self.serve(head=False)

    def serve(self, head):
        rel = urllib.parse.unquote(urllib.parse.urlsplit(self.path).path).lstrip("/")
        path = os.path.realpath(os.path.join(ROOT, rel))
        if not path.startswith(os.path.realpath(ROOT)) or not os.path.isfile(path):
            self.log_request_headers(404, None)
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        size = os.path.getsize(path)
        start, end, status = 0, size - 1, 200
        rh = self.headers.get("Range")
        if rh and rh.startswith("bytes="):
            a, _, b = rh[6:].split(",")[0].partition("-")
            if a:
                start = int(a)
                end = int(b) if b else size - 1
            else:
                start = size - int(b)
            end = min(end, size - 1)
            if start >= size:
                self.log_request_headers(416, rh)
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{size}")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            status = 206
        self.log_request_headers(status, f"{start}-{end}")
        self.send_response(status)
        self.send_header("Content-Type", "video/x-matroska")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        if status == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        if head:
            return
        with open(path, "rb") as f:
            f.seek(start)
            left = end - start + 1
            try:
                while left > 0:
                    chunk = f.read(min(65536, left))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    left -= len(chunk)
            except (BrokenPipeError, ConnectionResetError):
                pass

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    http.server.ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
