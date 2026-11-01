#!/usr/bin/env python3
"""Task 9 gate: relay playback and the PlaybackInfo filter on bob (consumer of alice).

    py harness/relay_gate.py [outdir]

Checks (prints PASS/FAIL lines, never tokens):
  a) GET and POST /Items/{id}/PlaybackInfo and item DTOs (Fields=MediaSources) hide the relay URL
  b) static stream via Jellyfin -> relay -> alice, Range requests, ffprobe of the first MiBs (in the container)
  d) low MaxStreamingBitrate -> TranscodingUrl, HLS output bitrate measured with ffprobe
  d) relay from the host (docker port mapping, non-loopback) -> 403; loopback + X-Forwarded-For -> 403
  d) alice share MaxStreams=1: a second alice title through bob's relay -> 503 (config restored afterwards)
The web-client check (c) is harness/relay_web_play.js (see its header).
Header auth only; harness credentials are the throwaway admin/admin.
"""
import json
import os
import re
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

PLUGIN_ID = "8a55d113-5072-47cf-9827-f3c49f1c3971"
ROOTS = {"alice": "http://localhost:8101", "bob": "http://localhost:8102/jellyfin"}
CLIENT = 'MediaBrowser Client="jellyshare-harness", Device="relay_gate", DeviceId="jellyshare-harness-relay-gate", Version="1.0"'
RELAY_IN_BOB = "http://127.0.0.1:8096/jellyfin/JellyShare/v1/play/"
BBB_KEY = "movie%3Atmdb%3A10378"
SINTEL_KEY = "movie%3Atmdb%3A45745"
FFPROBE = "/usr/lib/jellyfin-ffmpeg/ffprobe"
PROFILE = {
    "MaxStreamingBitrate": 120000000,
    "DirectPlayProfiles": [{"Container": "mkv,mp4,webm", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac"}],
    "TranscodingProfiles": [{"Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac",
                             "Protocol": "hls", "Context": "Streaming", "MaxAudioChannels": "2"}],
}
_tokens = {}
_failed = []


def call(server, method, path, body=None, headers=None, raw=False):
    hdrs = {"Authorization": CLIENT + (f', Token="{token(server)}"' if path != "/Users/AuthenticateByName" else "")}
    hdrs.update(headers or {})
    data = json.dumps(body).encode() if body is not None else None
    if data is not None:
        hdrs["Content-Type"] = "application/json"
    req = urllib.request.Request(ROOTS[server] + path, data=data, method=method, headers=hdrs)
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            content = resp.read()
            if raw:
                return resp.status, dict(resp.headers.items()), content
            return json.loads(content) if content else None
    except urllib.error.HTTPError as err:
        if raw:
            return err.code, dict(err.headers.items()), err.read()
        raise SystemExit(f"{method} {path.split('?')[0]} -> HTTP {err.code}")


def token(server):
    if server not in _tokens:
        res = call(server, "POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})
        _tokens[server] = res["AccessToken"]
        _tokens[server + ":user"] = res["User"]["Id"]
    return _tokens[server]


def uid(server):
    token(server)
    return _tokens[server + ":user"]


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail else ""))
    if not ok:
        _failed.append(name)


def sh(*args, timeout=120):
    return subprocess.run(args, capture_output=True, text=True, timeout=timeout)


def ffprobe_in_bob(local_file):
    sh("docker", "cp", local_file, "jellyshare-bob:/tmp/" + os.path.basename(local_file))
    res = sh("docker", "exec", "jellyshare-bob", FFPROBE, "-v", "error", "-show_entries",
             "format=duration,bit_rate:stream=codec_name,width,height", "-of", "json",
             "/tmp/" + os.path.basename(local_file))
    return json.loads(res.stdout or "{}")


def find_placeholder():
    items = call("bob", "GET", f"/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,ProviderIds&userId={uid('bob')}")["Items"]
    for it in items:
        if it.get("ProviderIds", {}).get("Tmdb") == "10378" and (it.get("Path") or "").endswith(".strm"):
            return it["Id"]
    raise SystemExit("Big Buck Bunny placeholder not found on bob")


def pbi(item, body, query=""):
    return call("bob", "POST", f"/Items/{item}/PlaybackInfo?userId={uid('bob')}{query}", body)


def summary(ms):
    return {k: ms.get(k) for k in ("Path", "Protocol", "SupportsDirectPlay", "SupportsDirectStream", "SupportsTranscoding")} | {
        "TranscodingUrl": bool(ms.get("TranscodingUrl"))}


def leaks(blob):
    text = blob if isinstance(blob, str) else json.dumps(blob)
    return "JellyShare/v1/play" in text or "127.0.0.1" in text


def gate_a(item):
    print("== (a) PlaybackInfo GET/POST and item DTOs")
    s, _, body = call("bob", "GET", f"/Items/{item}/PlaybackInfo?userId={uid('bob')}", raw=True)
    get = json.loads(body)
    ms = get["MediaSources"][0]
    print("  GET  ", summary(ms))
    check("GET PlaybackInfo hides Path and direct play", s == 200 and ms["Path"] == "" and ms["SupportsDirectPlay"] is False and not leaks(body.decode()))

    post = pbi(item, {"UserId": uid("bob"), "DeviceProfile": PROFILE, "MaxStreamingBitrate": 120000000})
    ms = post["MediaSources"][0]
    print("  POST ", summary(ms))
    check("POST PlaybackInfo hides Path, keeps direct stream", ms["Path"] == "" and ms["SupportsDirectPlay"] is False
          and ms["SupportsDirectStream"] is True and not leaks(post))

    for label, path in (("/Users/{u}/Items/{id}", f"/Users/{uid('bob')}/Items/{item}"),
                        ("/Items/{id}", f"/Items/{item}?userId={uid('bob')}"),
                        ("/Items?Ids=&Fields=MediaSources", f"/Items?Ids={item}&Fields=MediaSources,Path&userId={uid('bob')}"),
                        ("/Items?Recursive&Fields=MediaSources", f"/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources&userId={uid('bob')}")):
        s, _, raw = call("bob", "GET", path, raw=True)
        text = raw.decode()
        sources = re.findall(r'"MediaSources":\[', text)
        check(f"{label} has MediaSources without relay URL", s == 200 and len(sources) > 0 and not leaks(text),
              f"HTTP {s}, {len(sources)} MediaSources arrays")


def gate_b(item, outdir):
    print("== (b) static stream through the relay")
    s, h, first = call("bob", "GET", f"/Videos/{item}/stream?static=true", headers={"Range": "bytes=0-4194303"}, raw=True)
    print("  first 4 MiB:", s, h.get("Content-Range"), len(first))
    check("static stream Range 0-4MiB -> 206", s == 206 and len(first) == 4194304)
    path = os.path.join(outdir, "relay-first4m.mkv")
    with open(path, "wb") as f:
        f.write(first)
    probe = ffprobe_in_bob(path)
    streams = [(x.get("codec_name"), x.get("width"), x.get("height")) for x in probe.get("streams", [])]
    print("  ffprobe:", streams, probe.get("format", {}).get("duration"))
    check("ffprobe sees h264 1920x1080 + aac", ("h264", 1920, 1080) in streams and any(c == "aac" for c, _, _ in streams))

    total = int(h["Content-Range"].split("/")[1])
    seek = total * 3 // 4
    s, h, mid = call("bob", "GET", f"/Videos/{item}/stream?static=true", headers={"Range": f"bytes={seek}-{seek + 1048575}"}, raw=True)
    print("  seek 75%:", s, h.get("Content-Range"), len(mid))
    check("static stream seek (Range at 75%) -> 206", s == 206 and h.get("Content-Range", "").startswith(f"bytes {seek}-") and len(mid) == 1048576)
    s, h, tail = call("bob", "GET", f"/Videos/{item}/stream?static=true", headers={"Range": f"bytes={total - 1000}-"}, raw=True)
    check("open-ended Range at the end -> 206, 1000 bytes", s == 206 and len(tail) == 1000, f"{s} {h.get('Content-Range')}")


def gate_d_bitrate(item, outdir):
    print("== (d) 1 Mbps client -> transcode")
    body = {"UserId": uid("bob"), "DeviceProfile": PROFILE | {"MaxStreamingBitrate": 1000000}, "MaxStreamingBitrate": 1000000}
    ms = pbi(item, body)["MediaSources"][0]
    print("  POST 1 Mbps:", summary(ms))
    url = ms.get("TranscodingUrl") or ""
    print("  TranscodingUrl:", re.sub(r"(ApiKey|api_key)=[^&]+", r"\1=REDACTED", url)[:300])
    check("1 Mbps -> no direct play/stream, TranscodingUrl", ms["SupportsDirectStream"] is False and ms["SupportsDirectPlay"] is False and bool(url))
    if not url:
        return
    path = re.sub(r"(ApiKey|api_key)=[^&]+&?", "", url, flags=re.I)
    path = path[path.lower().index("/videos/"):]
    s, _, master = call("bob", "GET", path, raw=True)
    variant = [l for l in master.decode().splitlines() if l and not l.startswith("#")][0]
    base = path.split("master.m3u8")[0]
    s, _, playlist = call("bob", "GET", base + variant, raw=True)
    segs = [l for l in playlist.decode().splitlines() if l and not l.startswith("#")]
    out = os.path.join(outdir, "relay-capped.ts")
    with open(out, "wb") as f:
        for seg in segs[:4]:
            s, _, d = call("bob", "GET", base + seg, raw=True)
            print("  segment", seg.split("?")[0], "HTTP", s, len(d))
            f.write(d)
    probe = ffprobe_in_bob(out)
    fmt = probe.get("format", {})
    rate = int(fmt.get("bit_rate") or 0)
    print("  ffprobe:", [(x.get("codec_name"), x.get("width"), x.get("height")) for x in probe.get("streams", [])], fmt)
    check("HLS output bitrate <= ~1.1 Mbps", 0 < rate <= 1_100_000, f"{rate} bit/s")
    sid = re.search(r"PlaySessionId=([^&]+)", url).group(1)
    call("bob", "DELETE", f"/Videos/ActiveEncodings?deviceId=jellyshare-harness-relay-gate&playSessionId={sid}", raw=True)


def gate_d_access():
    print("== (d) relay access rules")
    try:
        with urllib.request.urlopen(ROOTS["bob"] + "/JellyShare/v1/play/" + BBB_KEY, timeout=30) as r:
            code = r.status
    except urllib.error.HTTPError as err:
        code = err.code
    check("host -> bob relay via port mapping -> 403", code == 403, f"HTTP {code}")
    fwd = sh("docker", "exec", "jellyshare-bob", "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}",
             "-H", "X-Forwarded-For: 203.0.113.9", "-r", "0-1023", RELAY_IN_BOB + BBB_KEY).stdout
    check("loopback + X-Forwarded-For -> 403", fwd == "403", fwd)
    ok = sh("docker", "exec", "jellyshare-bob", "curl", "-s", "-o", "/dev/null", "-w", "%{http_code} %{size_download}",
            "-H", "Icy-MetaData: 1", "-r", "0-1023", RELAY_IN_BOB + BBB_KEY).stdout
    check("loopback, Range 0-1023 -> 206 1024 bytes (control)", ok == "206 1024", ok)
    unknown = sh("docker", "exec", "jellyshare-bob", "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}",
                 RELAY_IN_BOB + "movie%3Atmdb%3A1").stdout
    check("loopback, unknown key -> 404", unknown == "404", unknown)


def gate_d_slots():
    print("== (d) alice MaxStreams=1, second alice title through bob's relay")
    cfg = call("alice", "GET", f"/Plugins/{PLUGIN_ID}/Configuration")
    original = json.loads(json.dumps(cfg))
    share = next(s for s in cfg["Shares"] if s["Name"] == "bob")
    print("  alice share for bob: MaxStreams", share["MaxStreams"], "-> 1")
    share["MaxStreams"] = 1
    call("alice", "POST", f"/Plugins/{PLUGIN_ID}/Configuration", cfg)
    try:
        print("  waiting 31 s for earlier provider sessions to expire")
        time.sleep(31)
        first = {}

        def play_first():
            # ~2 MB at 200 KB/s: an active playback of Big Buck Bunny for ~10 s
            first["out"] = sh("docker", "exec", "jellyshare-bob", "curl", "-s", "-o", "/dev/null", "--limit-rate", "200k",
                              "-w", "%{http_code} %{size_download}", "-r", "0-2000000", RELAY_IN_BOB + BBB_KEY).stdout

        t = threading.Thread(target=play_first)
        t.start()
        time.sleep(2)
        second = sh("docker", "exec", "jellyshare-bob", "curl", "-s", "-w", " %{http_code}", RELAY_IN_BOB + SINTEL_KEY).stdout
        same = sh("docker", "exec", "jellyshare-bob", "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}",
                  "-r", "5000000-5001023", RELAY_IN_BOB + BBB_KEY).stdout
        t.join()
        print("  first (BBB):", first.get("out"), "| second (Sintel):", second.strip(), "| BBB seek during playback:", same)
        check("first title plays", (first.get("out") or "").startswith("206 2000001"))
        check("second alice title -> 503 from relay", second.strip().endswith("503") and "no peer available" in second)
        check("same title, new Range during playback still allowed (one slot per playback)", same == "206")
    finally:
        call("alice", "POST", f"/Plugins/{PLUGIN_ID}/Configuration", original)
        restored = call("alice", "GET", f"/Plugins/{PLUGIN_ID}/Configuration")
        ms = next(s for s in restored["Shares"] if s["Name"] == "bob")["MaxStreams"]
        check("alice config restored", ms == next(s for s in original["Shares"] if s["Name"] == "bob")["MaxStreams"], f"MaxStreams={ms}")


def main():
    outdir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ.get("TEMP", "/tmp"), "jellyshare-relay-gate")
    os.makedirs(outdir, exist_ok=True)
    item = find_placeholder()
    print("Big Buck Bunny placeholder on bob:", item)
    gate_a(item)
    gate_b(item, outdir)
    gate_d_bitrate(item, outdir)
    gate_d_access()
    gate_d_slots()
    print("FAILED:", _failed if _failed else "none")
    sys.exit(1 if _failed else 0)


if __name__ == "__main__":
    main()
