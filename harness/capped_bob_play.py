#!/usr/bin/env python3
"""Task 12: play an alice title on bob (static stream of the .strm item through bob's relay) and save it.

    py harness/capped_bob_play.py <name substring> <out> [--seconds N] [--slow S]
Prints status, headers and bytes read; admin token never printed.
"""
import argparse
import json
import time
import urllib.parse
import urllib.request

BOB = "http://localhost:8102/jellyfin"
CLIENT = 'MediaBrowser Client="jellyshare-harness", Device="capped_bob_play", DeviceId="jellyshare-harness-capped-bob", Version="1.0"'


def call(method, url, body=None, token=None):
    headers = {"Authorization": f'{CLIENT}, Token="{token}"' if token else CLIENT}
    data = json.dumps(body).encode() if body is not None else None
    if data:
        headers["Content-Type"] = "application/json"
    with urllib.request.urlopen(urllib.request.Request(url, data=data, method=method, headers=headers), timeout=30) as r:
        raw = r.read()
        return json.loads(raw) if raw else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("name")
    ap.add_argument("out")
    ap.add_argument("--seconds", type=float, default=20)
    ap.add_argument("--slow", type=float, default=0)
    args = ap.parse_args()
    token = call("POST", BOB + "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})["AccessToken"]
    items = call("GET", BOB + "/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path&searchTerm="
                 + urllib.parse.quote(args.name), token=token)["Items"]
    item = next(i for i in items if (i.get("Path") or "").endswith(".strm"))
    print("bob item", item["Id"], repr(item["Name"]))
    req = urllib.request.Request(f"{BOB}/Videos/{item['Id']}/stream?static=true",
                                 headers={"Authorization": f'{CLIENT}, Token="{token}"'})
    resp = urllib.request.urlopen(req, timeout=60)
    keep = ("Content-Type", "Content-Length", "Accept-Ranges", "Transfer-Encoding")
    print("status", resp.status, {k: v for k, v in resp.headers.items() if k in keep})
    began, total = time.time(), 0
    with open(args.out, "wb") as f:
        while time.time() - began < args.seconds:
            chunk = resp.read(65536)
            if not chunk:
                break
            f.write(chunk)
            total += len(chunk)
            if args.slow:
                time.sleep(args.slow)
    print(f"read {total} bytes in {time.time() - began:.1f}s")
    resp.close()


if __name__ == "__main__":
    main()
