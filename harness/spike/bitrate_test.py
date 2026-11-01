#!/usr/bin/env python3
"""Spike (review fix): can a .strm source be transcoded at the client's max bitrate?

  bitrate_test.py <itemId> <outdir>
A: PlaybackInfo as a browser would send it (mkv/h264/aac direct-play allowed, 1 Mbps cap).
B: same, but EnableDirectPlay=false and EnableDirectStream=false in the request (what a
   request-side filter would set when MaxStreamingBitrate < source bitrate).
For B, fetch the TranscodingUrl (HLS) segments and save them so ffprobe can measure bitrate.
"""
import os
import re
import sys

import jf

item, outdir = sys.argv[1], sys.argv[2]
os.makedirs(outdir, exist_ok=True)
uid = jf.user_id("alice")
PROFILE = {
    "MaxStreamingBitrate": 1000000,
    "DirectPlayProfiles": [{"Container": "mkv,mp4", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac"}],
    "TranscodingProfiles": [{"Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac",
                             "Protocol": "hls", "Context": "Streaming", "MaxAudioChannels": "2"}],
}


def pbi(extra):
    body = {"UserId": uid, "MaxStreamingBitrate": 1000000, "DeviceProfile": PROFILE, **extra}
    ms = jf.call("alice", "POST", f"/Items/{item}/PlaybackInfo?userId={uid}", body)["MediaSources"][0]
    url = ms.get("TranscodingUrl") or ""
    print({k: ms.get(k) for k in ("SupportsDirectPlay", "SupportsDirectStream", "SupportsTranscoding", "Bitrate")})
    print("  TranscodingUrl:", re.sub(r"ApiKey=[^&]+", "ApiKey=REDACTED", url)[:400] or None)
    return url


print("== A: default request")
pbi({})
print("== B: EnableDirectPlay=false, EnableDirectStream=false")
url = pbi({"EnableDirectPlay": False, "EnableDirectStream": False})
if url:
    path = re.sub(r"ApiKey=[^&]+&?", "", url)  # header auth instead of ApiKey in URL
    path = path[path.index("/videos/"):] if "/videos/" in path else path
    s, _, m = jf.call("alice", "GET", path, raw=True)
    variant = [l for l in m.decode().splitlines() if l and not l.startswith("#")][0]
    base = path.split("master.m3u8")[0]
    s, _, v = jf.call("alice", "GET", base + variant, raw=True)
    segs = [l for l in v.decode().splitlines() if l and not l.startswith("#")]
    total = 0
    with open(os.path.join(outdir, "capped.ts"), "wb") as f:
        for seg in segs[:4]:
            s, _, d = jf.call("alice", "GET", base + seg, raw=True)
            print("  segment", seg.split("?")[0], "HTTP", s, len(d))
            f.write(d)
            total += len(d)
    print("  4 segments bytes:", total)
    sid = re.search(r"PlaySessionId=([^&]+)", url).group(1)
    jf.call("alice", "DELETE", f"/Videos/ActiveEncodings?deviceId=spike&playSessionId={sid}", raw=True)
