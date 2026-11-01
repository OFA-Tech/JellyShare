#!/usr/bin/env python3
"""Task 9 fix recheck on bob: lazy item endpoints and the stored device profile.

    py harness/relay_gate_extra.py

1. /Users/{u}/Items/{id}/SpecialFeatures and /LocalTrailers for the placeholder: no relay URL (usually empty lists).
2. Stores a device profile capped at 1 Mbps for this script's device (POST /Sessions/Capabilities/Full), then
   POSTs PlaybackInfo with no DeviceProfile in the body: expect no direct stream and a TranscodingUrl.
Header auth only, never prints tokens.
"""
import json
import re

import relay_gate as g


def main():
    item = g.find_placeholder()
    u = g.uid("bob")
    for name in ("SpecialFeatures", "LocalTrailers"):
        s, _, raw = g.call("bob", "GET", f"/Users/{u}/Items/{item}/{name}", raw=True)
        body = json.loads(raw)
        g.check(f"{name}: HTTP 200, no relay URL", s == 200 and not g.leaks(raw.decode()), f"{len(body)} items")

    caps = {"PlayableMediaTypes": ["Video"], "SupportsMediaControl": False,
            "DeviceProfile": g.PROFILE | {"MaxStreamingBitrate": 1000000}}
    s, _, _ = g.call("bob", "POST", "/Sessions/Capabilities/Full", caps, raw=True)
    print("  stored capabilities: HTTP", s)
    ms = g.pbi(item, {"UserId": u})["MediaSources"][0]
    print("  POST without DeviceProfile:", g.summary(ms))
    url = ms.get("TranscodingUrl") or ""
    print("  TranscodingUrl:", re.sub(r"(ApiKey|api_key)=[^&]+", r"\1=REDACTED", url)[:220])
    g.check("stored 1 Mbps profile -> no direct stream, TranscodingUrl",
            ms["SupportsDirectStream"] is False and "VideoBitrate=808000" in url)
    caps["DeviceProfile"] = g.PROFILE
    g.call("bob", "POST", "/Sessions/Capabilities/Full", caps, raw=True)
    print("FAILED:", g._failed if g._failed else "none")


if __name__ == "__main__":
    main()
