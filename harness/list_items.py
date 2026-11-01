#!/usr/bin/env python3
"""List the movies, series and episodes of a harness server with paths and provider ids.

    py harness/list_items.py bob
"""
import json
import sys
import urllib.request

ROOTS = {"alice": "http://localhost:8101", "bob": "http://localhost:8102/jellyfin"}
CLIENT = 'MediaBrowser Client="jellyshare-harness", Device="list_items", DeviceId="jellyshare-harness-list-items", Version="1.0"'


def call(method, url, body=None, token=None):
    headers = {"Authorization": f'{CLIENT}, Token="{token}"' if token else CLIENT}
    data = json.dumps(body).encode() if body is not None else None
    if data:
        headers["Content-Type"] = "application/json"
    with urllib.request.urlopen(urllib.request.Request(url, data=data, method=method, headers=headers), timeout=30) as r:
        raw = r.read()
        return json.loads(raw) if raw else None


def main():
    server = sys.argv[1] if len(sys.argv) > 1 else "bob"
    root = ROOTS[server]
    token = call("POST", root + "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})["AccessToken"]
    items = call("GET", root + "/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode,Folder"
                 "&Fields=Path,ProviderIds&SortBy=Type,SortName", token=token)["Items"]
    for i in items:
        if i["Type"] == "Folder" and "/placeholders/" not in (i.get("Path") or ""):
            continue
        extra = ""
        if i["Type"] == "Episode":
            extra = f" S{i.get('ParentIndexNumber')}E{i.get('IndexNumber')} series={i.get('SeriesName')}"
        print(f"{i['Type']:8} {i['Name']!r}{extra} ids={i.get('ProviderIds')} path={i.get('Path')}")


if __name__ == "__main__":
    main()
