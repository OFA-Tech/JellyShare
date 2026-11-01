#!/usr/bin/env python3
"""Task 12 harness helpers: provider bitrate cap and stream-slot hold.

    py harness/capped_check.py items                      # alice's movies/episodes with bitrate
    py harness/capped_check.py share --max-bitrate 2000000 --max-streams 1
    py harness/capped_check.py share --clear              # MaxBitrate null, MaxStreams 2
    py harness/capped_check.py fetch <itemId> <out> [--seconds N] [--start S] [--range R] [--via-relay URL]
    py harness/capped_check.py status <itemId>            # one request, prints status + headers only

Talks to alice directly with the token in harness/.secrets/alice-share-bob.token (never printed).
"""
import argparse
import json
import pathlib
import sys
import time
import urllib.error
import urllib.request

PLUGIN_ID = "8a55d113-5072-47cf-9827-f3c49f1c3971"
ALICE = "http://localhost:8101"
CLIENT = 'MediaBrowser Client="jellyshare-harness", Device="capped_check", DeviceId="jellyshare-harness-capped", Version="1.0"'
SECRETS = pathlib.Path(__file__).resolve().parent / ".secrets"


def call(method, url, body=None, token=None):
    headers = {"Authorization": f'{CLIENT}, Token="{token}"' if token else CLIENT}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    with urllib.request.urlopen(urllib.request.Request(url, data=data, method=method, headers=headers), timeout=30) as r:
        raw = r.read()
        return json.loads(raw) if raw else None


def admin():
    return call("POST", ALICE + "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})["AccessToken"]


def share_token():
    return (SECRETS / "alice-share-bob.token").read_text().strip()


def cmd_items(_):
    token = admin()
    items = call("GET", ALICE + "/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path,MediaSources", token=token)["Items"]
    for i in items:
        ms = (i.get("MediaSources") or [{}])[0]
        print(f"{i['Id']} {i['Type']:8} bitrate={ms.get('Bitrate')} runtime={i.get('RunTimeTicks')} {i['Name']!r}")


def cmd_share(args):
    token = admin()
    cfg = call("GET", f"{ALICE}/Plugins/{PLUGIN_ID}/Configuration", token=token)
    for s in cfg.get("Shares", []):
        if s.get("Name") == "bob":
            if args.clear:
                s["MaxBitrate"], s["MaxStreams"] = None, 2
            else:
                if args.max_bitrate is not None:
                    s["MaxBitrate"] = args.max_bitrate
                if args.max_streams is not None:
                    s["MaxStreams"] = args.max_streams
            print(f"share bob: MaxStreams={s.get('MaxStreams')} MaxBitrate={s.get('MaxBitrate')}")
    call("POST", f"{ALICE}/Plugins/{PLUGIN_ID}/Configuration", cfg, token=token)


def open_stream(item, start=None, rng=None, base=None):
    url = (base or ALICE + "/JellyShare/v1/stream/") + item
    if start is not None:
        url += "?start=" + start
    req = urllib.request.Request(url)
    if base is None:
        req.add_header("X-JellyShare-Token", share_token())
    if rng:
        req.add_header("Range", rng)
    try:
        return urllib.request.urlopen(req, timeout=60)
    except urllib.error.HTTPError as err:
        return err


def show(resp):
    keep = ("Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Retry-After", "Transfer-Encoding")
    print("status", resp.status, {k: v for k, v in resp.headers.items() if k in keep})


def cmd_fetch(args):
    resp = open_stream(args.item, args.start, args.range, args.via_relay)
    show(resp)
    if resp.status >= 300:
        print(resp.read()[:200].decode(errors="replace"))
        return
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
    took = time.time() - began
    print(f"read {total} bytes in {took:.1f}s ({total * 8 / max(took, 0.001) / 1e6:.2f} Mbit/s wire)")
    resp.close()


def cmd_status(args):
    resp = open_stream(args.item, args.start)
    show(resp)
    resp.close()


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("items").set_defaults(fn=cmd_items)
    sp = sub.add_parser("share")
    sp.add_argument("--max-bitrate", type=int)
    sp.add_argument("--max-streams", type=int)
    sp.add_argument("--clear", action="store_true")
    sp.set_defaults(fn=cmd_share)
    fp = sub.add_parser("fetch")
    fp.add_argument("item")
    fp.add_argument("out")
    fp.add_argument("--seconds", type=float, default=20)
    fp.add_argument("--start")
    fp.add_argument("--range")
    fp.add_argument("--slow", type=float, default=0, help="sleep between 64 KiB reads (keeps a long request open)")
    fp.add_argument("--via-relay", help="relay base URL ending in /, e.g. http://localhost:8102/jellyfin/JellyShare/v1/relay/<peer>/")
    fp.set_defaults(fn=cmd_fetch)
    st = sub.add_parser("status")
    st.add_argument("item")
    st.add_argument("--start")
    st.set_defaults(fn=cmd_status)
    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
