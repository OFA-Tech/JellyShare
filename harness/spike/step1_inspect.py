#!/usr/bin/env python3
"""Spike Step 1: show how the merged series looks through the API (survivor, per-folder ids)."""
import jf

uid = jf.user_id("alice")
allser = jf.call("alice", "GET", f"/Items?userId={uid}&Recursive=true&IncludeItemTypes=Series&Fields=Path,ProviderIds")
print("grouped Series list:", [(s["Name"], s["Id"], s["Path"]) for s in allser["Items"]])
ep = jf.call("alice", "GET", f"/Items?userId={uid}&Recursive=true&IncludeItemTypes=Episode&Fields=Path,SeriesId")["Items"]
for e in ep:
    print("episode", e["Name"], "SeriesId", e.get("SeriesId"), "SeasonId", e.get("SeasonId"), e["Path"])
for i in sorted({e["SeriesId"] for e in ep}):
    s = jf.call("alice", "GET", f"/Items/{i}?userId={uid}")
    print("series", i, s["Name"], s.get("Path"), "ChildCount", s.get("ChildCount"))
    print("  seasons", [(x["IndexNumber"], x["Id"]) for x in jf.call("alice", "GET", f"/Shows/{i}/Seasons?userId={uid}")["Items"]])
    print("  episodes", [(x["ParentIndexNumber"], x["IndexNumber"]) for x in jf.call("alice", "GET", f"/Shows/{i}/Episodes?userId={uid}")["Items"]])
