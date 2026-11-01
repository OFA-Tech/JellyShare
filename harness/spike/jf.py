#!/usr/bin/env python3
"""Spike-only Jellyfin API helper. Header auth only, never ?api_key=.

  python jf.py alice GET "/Items?Recursive=true&IncludeItemTypes=Series"
  python jf.py alice POST /Library/Refresh
  python jf.py alice POST /Items/<id>/PlaybackInfo '{"UserId": "..."}'
"""
import json
import os
import sys
import urllib.error
import urllib.request

ROOTS = {"alice": "http://localhost:8101", "bob": "http://localhost:8102/jellyfin"}
# Re-authenticating with the same DeviceId logs out that device's previous token, so concurrent
# spike processes (e.g. the WebSocket listener) use their own SPIKE_DEVICE.
_DEV = os.environ.get("SPIKE_DEVICE", "spike")
AUTH = f'MediaBrowser Client="spike", Device="{_DEV}", DeviceId="{_DEV}", Version="1"'
_tokens = {}


def call(server, method, path, body=None, raw=False, headers=None):
    root = ROOTS[server]
    hdrs = {"Authorization": AUTH + (f', Token="{token(server)}"' if path != "/Users/AuthenticateByName" else "")}
    hdrs.update(headers or {})
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        hdrs["Content-Type"] = "application/json"
    req = urllib.request.Request(root + path, data=data, method=method, headers=hdrs)
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            content = resp.read()
            if raw:
                return resp.status, dict(resp.headers.items()), content
            return json.loads(content) if content else None
    except urllib.error.HTTPError as err:
        if raw:
            return err.code, dict(err.headers.items()), err.read()
        raise SystemExit(f"{method} {path.split('?')[0]} -> HTTP {err.code}: {err.read()[:300]!r}")


def token(server):
    if server not in _tokens:
        res = call(server, "POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})
        _tokens[server] = res["AccessToken"]
        _tokens[server + ":user"] = res["User"]["Id"]
    return _tokens[server]


def user_id(server):
    token(server)
    return _tokens[server + ":user"]


if __name__ == "__main__":
    srv, method, path = sys.argv[1:4]
    body = json.loads(sys.argv[4]) if len(sys.argv) > 4 else None
    print(json.dumps(call(srv, method, path, body), indent=1))
