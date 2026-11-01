#!/usr/bin/env python3
"""Make one harness server consume another through JellyShare, and optionally run a sync.

    py harness/add_peer.py bob alice            # bob consumes alice (needs a share made by add_share.py alice bob)
    py harness/add_peer.py bob alice --sync     # ... and run "JellyShare: sync peers", print the sync log line
    py harness/add_peer.py bob --sync           # only run the sync task on bob

What it does on the consumer:
  1. adds/replaces a RemotePeer for the provider (POST /Plugins/{id}/Configuration). The provider URL is the
     compose service name as seen from inside the consumer container (http://alice:8096, http://bob:8096/jellyfin).
     The token is read from harness/.secrets/<provider>-share-<consumer>.token and never printed;
  2. creates the plugin's placeholder roots in the container (docker exec mkdir -p) and adds them as extra paths of
     the consumer's "Movies" / "Shows" libraries (POST /Library/VirtualFolders/Paths, no library refresh);
  3. with --sync: runs the scheduled task and prints the JellyShare sync summary from the container log.
Standard library only; harness credentials are the throwaway admin/admin.
"""
import argparse
import json
import pathlib
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

PLUGIN_ID = "8a55d113-5072-47cf-9827-f3c49f1c3971"
ROOTS = {"alice": "http://localhost:8101", "bob": "http://localhost:8102/jellyfin"}
INTERNAL = {"alice": "http://alice:8096", "bob": "http://bob:8096/jellyfin"}
CLIENT = 'MediaBrowser Client="jellyshare-harness", Device="add_peer", DeviceId="jellyshare-harness-add-peer", Version="1.0"'
SECRETS = pathlib.Path(__file__).resolve().parent / ".secrets"
TASK_NAME = "JellyShare: sync peers"


def call(method, url, body=None, token=None):
    headers = {"Authorization": f'{CLIENT}, Token="{token}"' if token else CLIENT}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as err:
        sys.exit(f"{method} {url.split('?')[0]} -> HTTP {err.code}: {err.read().decode(errors='replace')[:300]}")


def login(root):
    return call("POST", root + "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})["AccessToken"]


def add_peer(consumer, provider, root, token, priority):
    token_file = SECRETS / f"{provider}-share-{consumer}.token"
    if not token_file.exists():
        sys.exit(f"missing {token_file.name}: run add_share.py {provider} {consumer} first")
    peer_token = token_file.read_text().strip()

    cfg = call("GET", f"{root}/Plugins/{PLUGIN_ID}/Configuration", token=token)
    peers = [p for p in cfg.get("Peers", []) if p.get("Name") != provider]
    peers.append({
        "Id": str(uuid.uuid4()),
        "Name": provider,
        "BaseUrl": INTERNAL[provider],
        "Token": peer_token,
        "Enabled": True,
        "Priority": priority,
    })
    cfg["Peers"] = peers
    call("POST", f"{root}/Plugins/{PLUGIN_ID}/Configuration", cfg, token=token)
    print(f"{consumer}: peer '{provider}' -> {INTERNAL[provider]} (token from {token_file.name})")
    return call("GET", f"{root}/Plugins/{PLUGIN_ID}/Configuration", token=token)


def add_library_paths(consumer, root, token, cfg):
    container = f"jellyshare-{consumer}"
    folders = {f["Name"]: f for f in call("GET", root + "/Library/VirtualFolders", token=token)}
    for library, key in (("Movies", "MoviesPlaceholderPath"), ("Shows", "ShowsPlaceholderPath")):
        path = cfg.get(key)
        if not path:
            sys.exit(f"{consumer}: plugin config has no {key}")
        # The marker keeps the root from being skipped as "empty" by scans (the plugin also writes it).
        subprocess.run(["docker", "exec", container, "sh", "-c",
                        f"mkdir -p '{path}' && [ -f '{path}/.jellyshare' ] || touch '{path}/.jellyshare'"], check=True)
        if library not in folders:
            sys.exit(f"{consumer}: no library named {library}")
        if path in folders[library].get("Locations", []):
            print(f"{consumer}: {library} already has {path}")
            continue
        # One library refresh when the path is added (what the dashboard does too): it creates the Folder item
        # for the new root, which the plugin's targeted scans (ILibraryMonitor.ReportFileSystemChanged) resolve to.
        query = urllib.parse.urlencode({"refreshLibrary": "true"})
        call("POST", f"{root}/Library/VirtualFolders/Paths?{query}",
             {"Name": library, "PathInfo": {"Path": path}}, token=token)
        print(f"{consumer}: added {path} to library {library}")
        wait_for_library_scan(root, token)


def wait_for_library_scan(root, token):
    deadline = time.time() + 180
    time.sleep(3)
    while time.time() < deadline:
        tasks = call("GET", root + "/ScheduledTasks", token=token)
        scan = next((t for t in tasks if t.get("Key") == "RefreshLibrary"), None)
        if scan is None or scan["State"] == "Idle":
            return
        time.sleep(2)
    sys.exit("library scan did not finish in time")


def run_sync(consumer, root, token):
    tasks = call("GET", root + "/ScheduledTasks", token=token)
    task = next((t for t in tasks if t["Name"] == TASK_NAME), None)
    if task is None:
        sys.exit(f"{consumer}: task '{TASK_NAME}' not found (plugin not loaded?)")
    since = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime())
    call("POST", f"{root}/ScheduledTasks/Running/{task['Id']}", token=token)
    deadline = time.time() + 180
    while time.time() < deadline:
        time.sleep(2)
        state = call("GET", f"{root}/ScheduledTasks/{task['Id']}", token=token)
        if state["State"] == "Idle" and (state.get("LastExecutionResult") or {}).get("EndTimeUtc", "") >= since:
            result = state["LastExecutionResult"]
            print(f"{consumer}: task {result['Status']} ({result['StartTimeUtc']} .. {result['EndTimeUtc']})")
            if result.get("ErrorMessage"):
                print(f"  error: {result['ErrorMessage']}")
            break
    else:
        sys.exit(f"{consumer}: sync task did not finish in time")
    logs = subprocess.run(["docker", "logs", "--since", "5m", f"jellyshare-{consumer}"],
                          capture_output=True, text=True, encoding="utf-8", errors="replace")
    lines = [l for l in (logs.stdout + logs.stderr).splitlines() if "JellyShare sync" in l]
    for line in lines[-5:]:
        print("  " + line.strip())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("consumer", choices=sorted(ROOTS))
    ap.add_argument("provider", nargs="?", choices=sorted(ROOTS))
    ap.add_argument("--priority", type=int, default=0)
    ap.add_argument("--sync", action="store_true", help="run the sync task afterwards")
    args = ap.parse_args()
    if args.provider == args.consumer:
        sys.exit("consumer and provider must differ")

    root = ROOTS[args.consumer]
    token = login(root)
    if args.provider:
        cfg = add_peer(args.consumer, args.provider, root, token, args.priority)
        add_library_paths(args.consumer, root, token, cfg)
    if args.sync:
        run_sync(args.consumer, root, token)


if __name__ == "__main__":
    main()
