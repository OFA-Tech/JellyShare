#!/usr/bin/env python3
"""Create (or rotate) a JellyShare share on a harness server, without an admin endpoint.

    py harness/add_share.py alice bob                  # share all libraries of alice with "bob"
    py harness/add_share.py alice bob --libraries Movies

Generates a token (same format as ShareTokenService: js1_ + base64url(32 random bytes)),
stores ONLY its SHA-256 hash in the plugin configuration (GET/POST /Plugins/{id}/Configuration)
and writes the plaintext token to harness/.secrets/<server>-share-<peer>.token (gitignored).
The token is never printed. An existing share with the same peer name is replaced (rotated).
Standard library only; harness credentials are the throwaway admin/admin.
"""
import argparse
import base64
import hashlib
import json
import pathlib
import secrets
import sys
import urllib.error
import urllib.request
import uuid

PLUGIN_ID = "8a55d113-5072-47cf-9827-f3c49f1c3971"
ROOTS = {"alice": "http://localhost:8101", "bob": "http://localhost:8102/jellyfin"}
CLIENT = 'MediaBrowser Client="jellyshare-harness", Device="add_share", DeviceId="jellyshare-harness-add-share", Version="1.0"'
SECRETS = pathlib.Path(__file__).resolve().parent / ".secrets"


def call(method, url, body=None, token=None):
    headers = {"Authorization": f'{CLIENT}, Token="{token}"' if token else CLIENT}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as err:
        sys.exit(f"{method} {url} -> HTTP {err.code}: {err.read().decode(errors='replace')[:300]}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("server", choices=sorted(ROOTS), help="the providing server")
    ap.add_argument("peer", help="display name of the peer the share is for")
    ap.add_argument("--libraries", default="", help="comma separated library names (default: all)")
    ap.add_argument("--max-streams", type=int, default=2)
    args = ap.parse_args()

    root = ROOTS[args.server]
    auth = call("POST", root + "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})
    token = auth["AccessToken"]

    folders = call("GET", root + "/Library/VirtualFolders", token=token)
    wanted = {n.strip() for n in args.libraries.split(",") if n.strip()}
    chosen = [f for f in folders if not wanted or f["Name"] in wanted]
    if not chosen:
        sys.exit("no matching libraries")

    plaintext = "js1_" + base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("=")
    token_hash = hashlib.sha256(plaintext.encode()).hexdigest()

    SECRETS.mkdir(exist_ok=True)
    token_file = SECRETS / f"{args.server}-share-{args.peer}.token"
    token_file.write_text(plaintext)

    cfg = call("GET", f"{root}/Plugins/{PLUGIN_ID}/Configuration", token=token)
    shares = [s for s in cfg.get("Shares", []) if s.get("Name") != args.peer]
    shares.append({
        "Id": str(uuid.uuid4()),
        "Name": args.peer,
        "TokenHash": token_hash,
        "Enabled": True,
        "LibraryIds": [f["ItemId"] for f in chosen],
        "MaxStreams": args.max_streams,
        "MaxBitrate": None,
    })
    cfg["Shares"] = shares
    call("POST", f"{root}/Plugins/{PLUGIN_ID}/Configuration", cfg, token=token)
    print(f"share for '{args.peer}' on {args.server}: libraries={[f['Name'] for f in chosen]}, token in {token_file}")


if __name__ == "__main__":
    main()
