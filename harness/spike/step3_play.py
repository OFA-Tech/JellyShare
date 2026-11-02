#!/usr/bin/env python3
"""Spike Step 3/4: play a .strm item through alice's API only (no GUI).

  step3_play.py <itemId> <outdir>
PlaybackInfo (default + 1 Mbps cap), static stream, forced transcode via HLS, seek to 90 s.
Writes downloaded segments to <outdir> so ffprobe can run on them in the container.
"""
import json
import os
import re
import sys
import time

import jf

item, outdir = sys.argv[1], sys.argv[2]
os.makedirs(outdir, exist_ok=True)
uid = jf.user_id("alice")


def show_pbi(label, body):
    pbi = jf.call("alice", "POST", f"/Items/{item}/PlaybackInfo?userId={uid}", body)
    print(f"== PlaybackInfo {label}")
    for ms in pbi["MediaSources"]:
        print(json.dumps({k: ms.get(k) for k in (
            "Id", "Path", "Protocol", "IsRemote", "Container", "Size", "Bitrate", "RunTimeTicks",
            "SupportsDirectPlay", "SupportsDirectStream", "SupportsTranscoding", "SupportsProbing",
            "RequiresOpening", "IsInfiniteStream", "TranscodingUrl", "TranscodingSubProtocol",
            "DirectStreamUrl", "TranscodeReasons")}, indent=1))
        print("  MediaStreams:", [(s["Type"], s.get("Codec"), s.get("Width"), s.get("Height")) for s in ms.get("MediaStreams", [])])
    return pbi


# 1. PlaybackInfo with no device profile (what the server tells a client) and with a 1 Mbps cap.
pbi = show_pbi("default", {"UserId": uid, "AutoOpenLiveStream": True})
ms_id = pbi["MediaSources"][0]["Id"]
show_pbi("maxbitrate 1Mbps h264-only profile", {
    "UserId": uid, "MaxStreamingBitrate": 1000000, "AutoOpenLiveStream": True,
    "DeviceProfile": {
        "MaxStreamingBitrate": 1000000,
        "DirectPlayProfiles": [],
        "TranscodingProfiles": [{"Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac",
                                 "Protocol": "hls", "Context": "Streaming"}],
    }})

# 2. Static (direct) stream, first 1 MiB and a range from the middle.
for rng in ("bytes=0-1048575", "bytes=10000000-10999999"):
    status, hdrs, body = jf.call("alice", "GET", f"/Videos/{item}/stream?static=true&mediaSourceId={ms_id}",
                                 raw=True, headers={"Range": rng})
    print(f"== static {rng}: HTTP {status} len={len(body)} Content-Range={hdrs.get('Content-Range')} "
          f"Content-Type={hdrs.get('Content-Type')}")
status, hdrs, body = jf.call("alice", "GET", f"/Videos/{item}/stream?static=true&mediaSourceId={ms_id}", raw=True)
print(f"== static full: HTTP {status} len={len(body)}")
open(os.path.join(outdir, "static.mkv"), "wb").write(body)


# 3. Forced transcode: HLS h264 1 Mbps, first segment and a segment after a 90 s seek.
def hls(label, start_ticks):
    q = (f"mediaSourceId={ms_id}&videoCodec=h264&audioCodec=aac&videoBitRate=900000&audioBitRate=96000"
         f"&maxWidth=640&segmentContainer=ts&playSessionId=spike{label}&deviceId=spike"
         f"&transcodeReasons=ContainerBitrateExceedsLimit")
    if start_ticks:
        q += f"&startTimeTicks={start_ticks}"
    t0 = time.time()
    status, _, m = jf.call("alice", "GET", f"/Videos/{item}/master.m3u8?{q}", raw=True)
    print(f"== HLS {label} master HTTP {status}")
    variant = [l for l in m.decode().splitlines() if l and not l.startswith("#")][0]
    status, _, v = jf.call("alice", "GET", f"/Videos/{item}/{variant}", raw=True)
    segs = [l for l in v.decode().splitlines() if l and not l.startswith("#")]
    print(f"   variant HTTP {status}, {len(segs)} segments listed")
    # With startTimeTicks the playlist still lists the whole file; pick the segment containing 90 s.
    idx = 0
    if start_ticks:
        durs = [float(x) for x in re.findall(r"#EXTINF:([\d.]+)", v.decode())]
        acc = 0.0
        for i, d in enumerate(durs):
            if acc + d > start_ticks / 1e7:
                idx = i
                break
            acc += d
    seg = segs[idx]
    status, h, data = jf.call("alice", "GET", f"/Videos/{item}/{seg}", raw=True)
    print(f"   segment[{idx}] HTTP {status} len={len(data)} after {time.time() - t0:.1f}s")
    open(os.path.join(outdir, f"hls_{label}.ts"), "wb").write(data)
    jf.call("alice", "DELETE", f"/Videos/ActiveEncodings?deviceId=spike&playSessionId=spike{label}", raw=True)


hls("start", 0)
hls("seek90", 90 * 10_000_000)

# 4. Progressive transcode with seek (what older clients use).
status, h, data = jf.call("alice", "GET", f"/Videos/{item}/stream.ts?mediaSourceId={ms_id}&videoCodec=h264&audioCodec=aac"
                          f"&videoBitRate=900000&maxWidth=640&startTimeTicks={90 * 10_000_000}"
                          f"&playSessionId=spikeprog&deviceId=spike", raw=True)
print(f"== progressive transcode from 90 s: HTTP {status} len={len(data)}")
open(os.path.join(outdir, "prog_seek90.ts"), "wb").write(data)
