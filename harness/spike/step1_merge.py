#!/usr/bin/env python3
"""Spike Step 1: does a series split across two library folders merge into one?

  python step1_merge.py <variant>    (see VARIANTS)  |  python step1_merge.py clean
Creates the placeholder series under harness/media/alice-placeholders/Shows (mounted at
/placeholders/Shows in alice), scans and prints the Series / Seasons / Episodes the API returns.
"""
import json
import os
import shutil
import sys
import time

import jf

HERE = os.path.dirname(os.path.abspath(__file__))
PH = os.path.join(HERE, "..", "media", "alice-placeholders", "Shows")
URL = "http://127.0.0.1:8900/bob/Shows/Pioneer%20One%20%5Btvdbid-153021%5D/Season%2002/Pioneer%20One%20S02E01.mkv"
NFO_BOTH = """<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow>
  <title>Pioneer One</title>
  <tvdbid>153021</tvdbid>
  <uniqueid type="tvdb" default="true">153021</uniqueid>
</tvshow>
"""
NFO_UNIQUEID = """<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow>
  <title>Pioneer One</title>
  <uniqueid type="tvdb" default="true">153021</uniqueid>
</tvshow>
"""
NFO_TVDBID = """<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow>
  <title>Pioneer One</title>
  <tvdbid>153021</tvdbid>
</tvshow>
"""
# variant -> (series folder name, tvshow.nfo content or None)
VARIANTS = {
    "same-name-id-nfo": ("Pioneer One [tvdbid-153021]", NFO_BOTH),
    "same-name-id": ("Pioneer One [tvdbid-153021]", None),
    "same-name-only": ("Pioneer One", None),
    "diff-name-id": ("Pioneer One (shared) [tvdbid-153021]", None),
    "diff-name-nfo-both": ("Pioneer One (shared)", NFO_BOTH),
    # Unique folder names: a reused path keeps the old item (and its ProviderIds) in the DB.
    "diff-name-nfo-uniqueid": ("Pioneer One (shared u)", NFO_UNIQUEID),
    "diff-name-nfo-tvdbid": ("Pioneer One (shared t)", NFO_TVDBID),
    "diff-name-none": ("Pioneer One (control)", None),
}


def wait_scan():
    time.sleep(3)
    deadline = time.time() + 300
    while time.time() < deadline:
        tasks = jf.call("alice", "GET", "/ScheduledTasks?isHidden=false")
        scan = [t for t in tasks if t.get("Key") == "RefreshLibrary"][0]
        if scan["State"] == "Idle":
            return scan.get("LastExecutionResult", {}).get("Status")
        time.sleep(2)
    raise SystemExit("scan timeout")


def scan():
    jf.call("alice", "POST", "/Library/Refresh")
    return wait_scan()


def clean():
    if os.path.isdir(PH):
        for name in os.listdir(PH):
            p = os.path.join(PH, name)
            if os.path.isdir(p):
                shutil.rmtree(p)
    # An empty library folder is skipped by the scan ("inaccessible or empty"), so its old
    # children are never removed. Keep a marker file so removals are picked up.
    os.makedirs(PH, exist_ok=True)
    with open(os.path.join(PH, ".jellyshare"), "w") as f:
        f.write("marker\n")


def report():
    uid = jf.user_id("alice")
    out = {}
    series = jf.call("alice", "GET", f"/Items?userId={uid}&Recursive=true&IncludeItemTypes=Series"
                     "&Fields=Path,ProviderIds,PresentationUniqueKey")["Items"]
    out["series"] = []
    for s in series:
        seasons = jf.call("alice", "GET", f"/Shows/{s['Id']}/Seasons?userId={uid}&Fields=Path")["Items"]
        eps = jf.call("alice", "GET", f"/Shows/{s['Id']}/Episodes?userId={uid}&Fields=Path")["Items"]
        out["series"].append({
            "Name": s["Name"], "Id": s["Id"], "Path": s.get("Path"), "ProviderIds": s.get("ProviderIds"),
            "Seasons": [(x["Name"], x.get("IndexNumber"), x.get("Path")) for x in seasons],
            "Episodes": [(x["Name"], x.get("ParentIndexNumber"), x.get("IndexNumber"), x.get("Path")) for x in eps],
        })
    raw = jf.call("alice", "GET", "/Items?Recursive=true&IncludeItemTypes=Series&Fields=Path")
    out["series_raw_count_no_user"] = raw["TotalRecordCount"]
    return out


if __name__ == "__main__":
    v = sys.argv[1]
    clean()
    if v != "clean":
        folder, nfo = VARIANTS[v]
        season = os.path.join(PH, folder, "Season 02")
        os.makedirs(season)
        with open(os.path.join(season, "Pioneer One S02E01.strm"), "w", encoding="utf-8", newline="\n") as f:
            f.write(URL + "\n")
        if nfo:
            with open(os.path.join(PH, folder, "tvshow.nfo"), "w", encoding="utf-8", newline="\n") as f:
                f.write(nfo)
    status = scan()
    print(json.dumps({"variant": v, "scan": status, **report()}, indent=1))
