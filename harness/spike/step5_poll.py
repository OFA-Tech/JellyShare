#!/usr/bin/env python3
"""Spike Step 5: poll alice until an item (by ProviderIds Tmdb or by name) disappears/changes.

  step5_poll.py <tmdb-id> <seconds> [scan-after-seconds]
Prints elapsed time, presence and LocationType every 5 s.
"""
import sys
import time

import jf

tmdb, secs = sys.argv[1], float(sys.argv[2])
scan_after = float(sys.argv[3]) if len(sys.argv) > 3 else None
uid = jf.user_id("alice")
t0 = time.time()
scanned = False
last = None
while time.time() - t0 < secs:
    items = jf.call("alice", "GET", f"/Items?userId={uid}&Recursive=true&IncludeItemTypes=Movie&Fields=Path,ProviderIds"
                    "&IsMissing=")["Items"]
    hit = [i for i in items if i.get("ProviderIds", {}).get("Tmdb") == tmdb]
    state = "present " + hit[0]["Id"] + " " + str(hit[0].get("LocationType")) if hit else "GONE"
    if state != last:
        print(f"{time.time() - t0:6.1f}s {time.strftime('%H:%M:%S')} {state}", flush=True)
        last = state
    if scan_after is not None and not scanned and time.time() - t0 > scan_after:
        print(f"{time.time() - t0:6.1f}s {time.strftime('%H:%M:%S')} -> POST /Library/Refresh", flush=True)
        jf.call("alice", "POST", "/Library/Refresh")
        scanned = True
    time.sleep(2)
print(f"{time.time() - t0:6.1f}s end: {last}")
