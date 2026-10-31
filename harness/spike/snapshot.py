#!/usr/bin/env python3
"""Spike: print every Movie/Series/Episode on alice with LocationType (incl. missing/virtual)."""
import jf

uid = jf.user_id("alice")
r = jf.call("alice", "GET", f"/Items?userId={uid}&Recursive=true&IncludeItemTypes=Movie,Series,Episode,Folder"
            "&Fields=Path,ProviderIds&IsVirtualItem=")
for i in r["Items"]:
    print(f"  {i['Type']:8} {i.get('LocationType')!s:10} {i['Name'][:40]:40} {i.get('Path')}")
print("  total", r["TotalRecordCount"])
