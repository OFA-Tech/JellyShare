#!/usr/bin/env python3
"""Provision the two harness Jellyfin servers (alice, bob). Standard library only.

For each server: finish the startup wizard (admin/admin), create the "Movies"
and "Shows" libraries over /media/Movies and /media/Shows, name the server after
itself (alice/bob), set bob's BaseUrl to /jellyfin, restart, and wait until the server answers again. Safe to re-run.

Credentials are the throwaway harness ones and are only ever sent in the
request body / Authorization header, never in a URL.
"""
import json
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

ADMIN_USER = "admin"
ADMIN_PASSWORD = "admin"
CLIENT_AUTH = 'MediaBrowser Client="jellyshare-harness", Device="setup", DeviceId="jellyshare-harness-setup", Version="1.0"'

SERVERS = [
    {"name": "alice", "port": 8101, "base_url": "", "container": "jellyshare-alice"},
    {"name": "bob", "port": 8102, "base_url": "/jellyfin", "container": "jellyshare-bob"},
]
LIBRARIES = [
    ("Movies", "movies", "/media/Movies", ["Movie"]),
    ("Shows", "tvshows", "/media/Shows", ["Series", "Season", "Episode"]),
]


class ApiError(Exception):
    pass


def request(method, url, body=None, token=None, timeout=30):
    data = None if body is None else json.dumps(body).encode()
    headers = {"Authorization": f'{CLIENT_AUTH}, Token="{token}"' if token else CLIENT_AUTH}
    if data is not None:
        headers["Content-Type"] = "application/json"
    deadline = time.time() + 120
    while True:
        req = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read()
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as err:
            # 503 = server still starting (or restarting); anything else is a real error.
            if err.code != 503 or time.time() > deadline:
                detail = err.read().decode(errors="replace")[:300]
                raise ApiError(f"{method} {url.split('?')[0]} -> HTTP {err.code}: {detail}") from None
            time.sleep(2)
        except (urllib.error.URLError, OSError):
            # Connection refused/reset while the server (re)starts.
            if time.time() > deadline:
                raise
            time.sleep(2)


def public_info(root):
    """Return /System/Info/Public from `root`, or None if nothing answers there."""
    try:
        return request("GET", root + "/System/Info/Public", timeout=5)
    except (ApiError, urllib.error.URLError, OSError, ValueError):
        return None


def wait_for(roots, seconds=240):
    """Wait until any of `roots` answers; return (root, info)."""
    deadline = time.time() + seconds
    while time.time() < deadline:
        for root in roots:
            info = public_info(root)
            if info:
                return root, info
        time.sleep(2)
    sys.exit(f"timed out waiting for {roots}")


def run_wizard(root):
    request("GET", root + "/Startup/Configuration")
    request("POST", root + "/Startup/Configuration", {
        "UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
    request("GET", root + "/Startup/User")
    request("POST", root + "/Startup/User", {"Name": ADMIN_USER, "Password": ADMIN_PASSWORD})
    request("POST", root + "/Startup/RemoteAccess", {"EnableRemoteAccess": True})
    request("POST", root + "/Startup/Complete")


def authenticate(root):
    result = request("POST", root + "/Users/AuthenticateByName", {"Username": ADMIN_USER, "Pw": ADMIN_PASSWORD})
    return result["AccessToken"]


def ensure_libraries(root, token):
    existing = {f["Name"] for f in request("GET", root + "/Library/VirtualFolders", token=token)}
    for name, ctype, path, types in LIBRARIES:
        if name in existing:
            continue
        query = urllib.parse.urlencode({"name": name, "collectionType": ctype, "refreshLibrary": "true"})
        request("POST", f"{root}/Library/VirtualFolders?{query}",
                {"LibraryOptions": {
                    "PathInfos": [{"Path": path}],
                    # No online metadata/images: titles keep only the provider IDs written in
                    # their folder names, so "Home Video" stays ID-less and runs are offline-safe.
                    "TypeOptions": [{"Type": t, "MetadataFetchers": [], "ImageFetchers": []} for t in types],
                }}, token=token)
        print(f"  created library {name} -> {path}")


def ensure_server_name(root, token, name):
    config = request("GET", root + "/System/Configuration", token=token)
    if config.get("ServerName") != name:
        config["ServerName"] = name
        request("POST", root + "/System/Configuration", config, token=token)
        print(f"  ServerName set to {name!r}")


def ensure_base_url(root, token, base_url):
    """Set the server BaseUrl; return True when it changed (needs a restart)."""
    network = request("GET", root + "/System/Configuration/network", token=token)
    if network.get("BaseUrl", "") == base_url:
        return False
    network["BaseUrl"] = base_url
    request("POST", root + "/System/Configuration/network", network, token=token)
    print(f"  BaseUrl set to {base_url!r}")
    return True


def provision(server):
    name, port, base_url = server["name"], server["port"], server["base_url"]
    host = f"http://localhost:{port}"
    print(f"{name}: waiting for {host}")
    # Fresh servers have no BaseUrl yet; a provisioned server answers under it.
    root, info = wait_for([host + base_url, host])
    if not info.get("StartupWizardCompleted"):
        print(f"{name}: running startup wizard")
        run_wizard(root)
    token = authenticate(root)
    ensure_server_name(root, token, name)
    ensure_libraries(root, token)
    changed = ensure_base_url(root, token, base_url)
    root = host + base_url
    if changed:
        # After a BaseUrl change the running server answers 404 on both the old and the
        # new root until it is restarted, so /System/Restart is unreachable: restart the container.
        print(f"{name}: restarting container {server['container']}")
        subprocess.run(["docker", "restart", server["container"]], check=True, capture_output=True)
        time.sleep(5)
        root, _ = wait_for([root])
    token = authenticate(root)
    request("POST", root + "/Library/Refresh", token=token)
    plugins = request("GET", root + "/Plugins", token=token)
    jellyshare = [p for p in plugins if p.get("Name") == "JellyShare"]
    status = jellyshare[0]["Status"] if jellyshare else "NOT FOUND"
    print(f"{name}: ready at {root}  JellyShare plugin status: {status}")


if __name__ == "__main__":
    try:
        for srv in SERVERS:
            provision(srv)
    except ApiError as exc:
        sys.exit(str(exc))
