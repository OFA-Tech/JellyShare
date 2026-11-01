#!/usr/bin/env python3
"""End-to-end suite for JellyShare against the running two-server harness (alice, bob). Standard library only.

    py harness/e2e.py             # run against the already running harness, prints one line per check
    py harness/e2e.py --deploy    # first docker cp the Release JellyShare.*.dll into both containers and restart them

Needs the harness from the README (docker compose up, setup.py run, media generated) and a Release build.
Prints one PASS/FAIL line per check and `ALL PASS` at the end; exit code 1 when any check failed.

What it does (and undoes): pairs the servers both ways through the plugin's own admin endpoints (an existing
working pairing is kept, otherwise it is recreated; share tokens only go to harness/.secrets/), runs a sync on
both, then checks merge/dedupe, the loop guard, Home Video, relay access rules, PlaybackInfo, playback + ffprobe,
the provider stream limit (429), the provider bitrate cap and the local-file fallback. Everything it changes is put
back afterwards even when a check fails: share limits, the media file it deletes (copied back from
harness/media/.base.mkv) and the library state (refresh + wait until the placeholder is gone again).

Auth is by header only (Authorization: MediaBrowser ... Token="..."; X-JellyShare-Token). Tokens are never
printed. Harness admin credentials are the throwaway admin/admin. Timing-sensitive provider checks run from inside
the Docker network (docker exec curl in the bob container), because Docker Desktop's published-port forwarder
buffers responses.
"""
import argparse
import collections
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import threading
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
SECRETS = HERE / ".secrets"
MEDIA = HERE / "media"
BASE_MKV = MEDIA / ".base.mkv"
RELEASE_DIR = HERE.parent / "Source" / "Infrastructure" / "Platform" / "Jellyfin" / "bin" / "Release" / "net10.0"
PLUGIN_DLL_NAME = "JellyShare.Jellyfin.dll"
# Every assembly the plugin ships (build.yaml artifacts): the entry DLL plus the Domain and Application layers.
PLUGIN_DLLS = (PLUGIN_DLL_NAME, "JellyShare.Application.dll", "JellyShare.Domain.dll",
               "JellyShare.Data.Peers.dll", "JellyShare.Data.Files.dll", "JellyShare.Media.dll")
PLUGIN_DLL_FALLBACK = "/config/plugins/JellyShare_0.0.0.1/" + PLUGIN_DLL_NAME
FFPROBE = "/usr/lib/jellyfin-ffmpeg/ffprobe"
CLIENT = 'MediaBrowser Client="jellyshare-harness", Device="e2e", DeviceId="jellyshare-harness-e2e", Version="1.0"'

SERVERS = {
    "alice": {"url": "http://127.0.0.1:8101", "internal": "http://alice:8096", "container": "jellyshare-alice"},
    "bob": {"url": "http://127.0.0.1:8102/jellyfin", "internal": "http://bob:8096/jellyfin", "container": "jellyshare-bob"},
}
OTHER = {"alice": "bob", "bob": "alice"}

BBB, SINTEL, TEARS = "10378", "45745", "133701"
EXPECTED_TMDB = {BBB, SINTEL, TEARS}
# Which titles each server owns as a real file (the rest are peer placeholders); see harness/make-media.sh.
LOCAL_MOVIES = {"alice": {BBB, SINTEL}, "bob": {SINTEL, TEARS}}
EXPECTED_PLACEHOLDERS = {"alice": 3, "bob": 1}  # Tears of Steel + S01E02 + S02E01 on alice; Big Buck Bunny on bob
ALL_EPISODES = {(1, 1), (1, 2), (2, 1)}
LOCAL_EPISODES = {"alice": {(1, 1)}, "bob": {(1, 1), (1, 2), (2, 1)}}
MEDIA_FILES = [
    "alice/Movies/Big Buck Bunny (2008) [tmdbid-10378]/Big Buck Bunny (2008).mkv",
    "alice/Movies/Sintel (2010) [tmdbid-45745]/Sintel (2010).mkv",
    "alice/Movies/Home Video/clip.mkv",
    "alice/Shows/Pioneer One [tvdbid-153021]/Season 01/Pioneer One S01E01.mkv",
    "bob/Movies/Sintel (2010) [tmdbid-45745]/Sintel (2010).mkv",
    "bob/Movies/Tears of Steel (2012) [tmdbid-133701]/Tears of Steel (2012).mkv",
    "bob/Shows/Pioneer One [tvdbid-153021]/Season 01/Pioneer One S01E01.mkv",
    "bob/Shows/Pioneer One [tvdbid-153021]/Season 01/Pioneer One S01E02.mkv",
    "bob/Shows/Pioneer One [tvdbid-153021]/Season 02/Pioneer One S02E01.mkv",
]
PROFILE = {
    "MaxStreamingBitrate": 120000000,
    "DirectPlayProfiles": [{"Container": "mkv,mp4,webm", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac"}],
    "TranscodingProfiles": [{"Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac",
                             "Protocol": "hls", "Context": "Streaming", "MaxAudioChannels": "2"}],
}

_failed = []


class ApiError(Exception):
    pass


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail else ""), flush=True)
    if not ok:
        _failed.append(name)
    return ok


def note(text):
    print("     " + text, flush=True)


def http(method, url, body=None, headers=None, timeout=60, raw=False):
    """One HTTP call. Returns parsed JSON (None for an empty body); raw=True returns (status, headers, bytes) for any status."""
    hdrs = dict(headers or {})
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        hdrs["Content-Type"] = "application/json"
    deadline = time.time() + 60
    while True:
        req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                content = resp.read()
                if raw:
                    return resp.status, dict(resp.headers.items()), content
                return json.loads(content) if content else None
        except urllib.error.HTTPError as err:
            content = err.read()
            if raw:
                return err.code, dict(err.headers.items()), content
            if err.code == 503 and time.time() < deadline:
                time.sleep(2)
                continue
            # Status and reason only: some of these requests carry or return tokens, so no response body in the message.
            raise ApiError(f"{method} {url.split('?')[0]} -> HTTP {err.code} {err.reason}") from None
        except (urllib.error.URLError, ConnectionError, TimeoutError) as err:
            # A POST that timed out may have been processed: retry it only when the connection was refused (never sent).
            refused = isinstance(getattr(err, "reason", err), ConnectionRefusedError) or isinstance(err, ConnectionRefusedError)
            if time.time() < deadline and not raw and (method != "POST" or refused):
                time.sleep(2)
                continue
            raise ApiError(f"{method} {url.split('?')[0]} -> {type(err).__name__}") from None


def read_n(url, headers, n, timeout=90):
    """GET `url`, read at most `n` bytes, close. Returns (status, headers, bytes) for any HTTP status."""
    req = urllib.request.Request(url, headers=headers)
    try:
        resp = urllib.request.urlopen(req, timeout=timeout)
    except urllib.error.HTTPError as err:
        return err.code, dict(err.headers.items()), err.read()
    buf = bytearray()
    try:
        while len(buf) < n:
            chunk = resp.read(min(65536, n - len(buf)))
            if not chunk:
                break
            buf += chunk
        return resp.status, dict(resp.headers.items()), bytes(buf)
    finally:
        resp.close()


def sh(*args, input=None, timeout=120):
    return subprocess.run(args, input=input, capture_output=True, timeout=timeout)


def curl_in(container, url, token=None, headers=(), args=(), timeout=90):
    """curl from inside a container. Token and URL travel on stdin (curl -K -), never on the command line."""
    cfg = f'url = "{url}"\n'
    if token:
        cfg += f'header = "X-JellyShare-Token: {token}"\n'
    for h in headers:
        cfg += f'header = "{h}"\n'
    cmd = ["docker", "exec", "-i", container, "curl", "-s", "-K", "-", "-D", "-", "-o", "/dev/null",
           "-w", "\nE2E_CODE=%{http_code} E2E_SIZE=%{size_download}\n", *args]
    res = subprocess.run(cmd, input=cfg, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    m = re.search(r"E2E_CODE=(\d+) E2E_SIZE=(\d+)", res.stdout)
    if not m:
        raise ApiError(f"curl in {container} failed: {res.stderr.strip()[:200]}")
    hdrs = {}
    for line in res.stdout.splitlines():
        if ":" in line and not line.startswith("HTTP/") and not line.startswith("E2E_"):
            k, v = line.split(":", 1)
            hdrs[k.strip().lower()] = v.strip()
    return int(m.group(1)), int(m.group(2)), hdrs


def ffprobe(container, data, *args):
    """Run ffprobe in `container` on `data` (written to a temp file there, so the size is known) and return stdout."""
    tmp = f"/tmp/e2e-{os.getpid()}.bin"
    sh("docker", "exec", "-i", container, "sh", "-c", f"cat > {tmp}", input=data)
    try:
        res = sh("docker", "exec", container, FFPROBE, "-v", "error", *args, tmp)
        return res.stdout.decode(errors="replace")
    finally:
        sh("docker", "exec", container, "rm", "-f", tmp)


def wait_until(fn, timeout=120, interval=3, tick=None, tick_every=45):
    start = time.time()
    next_tick = start
    while True:
        value = fn()
        if value:
            return value
        now = time.time()
        if now - start >= timeout:
            return None
        if tick and now >= next_tick:
            tick()
            next_tick = now + tick_every
        time.sleep(interval)


def under(path, roots):
    p = (path or "").replace("\\", "/").rstrip("/")
    return any(p == r.rstrip("/") or p.startswith(r.rstrip("/") + "/") for r in roots)


def is_strm(item):
    return (item.get("Path") or "").lower().endswith(".strm")


def tmdb(item):
    return (item.get("ProviderIds") or {}).get("Tmdb")


def episode_key(item):
    return (item.get("ParentIndexNumber"), item.get("IndexNumber"))


class Server:
    def __init__(self, name):
        self.name = name
        self.url = SERVERS[name]["url"]
        self.internal = SERVERS[name]["internal"]
        self.container = SERVERS[name]["container"]
        self.token = None
        self.user = None
        self.libs = {}

    def login(self):
        res = http("POST", self.url + "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"},
                   headers={"Authorization": CLIENT})
        self.token = res["AccessToken"]
        self.user = res["User"]["Id"]

    @property
    def auth(self):
        return {"Authorization": f'{CLIENT}, Token="{self.token}"'}

    def api(self, method, path, body=None, timeout=60, raw=False, headers=None):
        return http(method, self.url + path, body, headers={**self.auth, **(headers or {})}, timeout=timeout, raw=raw)

    def status(self):
        st = self.api("GET", "/JellyShare/v1/admin/status")
        self.libs = {l["Name"]: l["Id"] for l in st["Libraries"]}
        return st

    def items(self, types, parent=None, extra=None):
        q = {"Recursive": "true", "IncludeItemTypes": types, "Fields": "Path,ProviderIds", "userId": self.user}
        if parent:
            q["ParentId"] = parent
        q.update(extra or {})
        return self.api("GET", "/Items?" + urllib.parse.urlencode(q))["Items"]

    def movies(self):
        return self.items("Movie", self.libs["Movies"])

    def series(self):
        return self.items("Series", self.libs["Shows"])

    def episodes(self):
        return self.items("Episode", self.libs["Shows"])

    def refresh(self):
        self.api("POST", "/Library/Refresh", raw=True)

    def sync(self, timeout=300):
        """Starts a sync (POST answers 202 at once) and waits until the status no longer shows it running."""
        before = (self.status().get("LastSync") or {}).get("FinishedUtc")
        started = self.api("POST", "/JellyShare/v1/admin/sync")
        if not (started or {}).get("Started") and not (started or {}).get("AlreadyRunning"):
            raise RuntimeError(f"sync not started: {started}")
        deadline = time.time() + timeout
        while time.time() < deadline:
            st = self.status()
            last = st.get("LastSync") or {}
            if not st.get("SyncRunning") and (last.get("FinishedUtc") != before or st.get("SyncError")):
                if st.get("SyncError"):
                    raise RuntimeError("sync failed: " + st["SyncError"])
                return last
            time.sleep(1)
        raise TimeoutError("sync did not finish in time")

    def active_streams(self, share_name):
        return next((s["ActiveStreams"] for s in self.status()["Shares"] if s["Name"] == share_name), 0)

    def wait_scan_idle(self, timeout=180):
        def idle():
            tasks = self.api("GET", "/ScheduledTasks")
            scan = next((t for t in tasks if t.get("Key") == "RefreshLibrary"), None)
            return scan is None or scan["State"] == "Idle"
        time.sleep(3)
        return wait_until(idle, timeout, 2)


ALICE, BOB = Server("alice"), Server("bob")
S = {"alice": ALICE, "bob": BOB}


def ping(server):
    status, _, body = http("GET", server.url + "/JellyShare/v1/ping", raw=True, timeout=10)
    return status == 200 and json.loads(body).get("serverName") == server.name


def file_hash(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def plugin_dll_path(server):
    """Where the plugin DLL lives in the container (the plugin folder name carries the version), else the harness default."""
    res = sh("docker", "exec", server.container, "find", "/config/plugins", "-name", PLUGIN_DLL_NAME)
    found = sorted(line.strip() for line in res.stdout.decode().splitlines() if line.strip())
    return found[0] if found else PLUGIN_DLL_FALLBACK


def plugin_dir(server):
    """The container's plugin folder (holds every JellyShare.*.dll)."""
    return plugin_dll_path(server).rsplit("/", 1)[0]


def container_hash(server, name=PLUGIN_DLL_NAME):
    res = sh("docker", "exec", server.container, "sha256sum", plugin_dir(server) + "/" + name)
    return res.stdout.decode().split()[0] if res.returncode == 0 and res.stdout else None


def deploy_and_check(deploy):
    print("== build")
    missing = [n for n in PLUGIN_DLLS if not (RELEASE_DIR / n).exists()]
    if missing:
        check("Release DLLs exist (run: dotnet build -c Release)", False, ", ".join(missing))
        return False
    want = {n: file_hash(RELEASE_DIR / n) for n in PLUGIN_DLLS}
    if deploy:
        for s in S.values():
            target = plugin_dir(s)
            copied = all(sh("docker", "cp", str(RELEASE_DIR / n), f"{s.container}:{target}/{n}").returncode == 0
                         for n in PLUGIN_DLLS)
            check(f"{s.name}: JellyShare DLLs copied into the container", copied)
            sh("docker", "restart", s.container)
        for s in S.values():
            wait_until(lambda s=s: _safe_ping(s), 180, 3)
    ok = True
    for s in S.values():
        stale = [n for n in PLUGIN_DLLS if container_hash(s, n) != want[n]]
        ok &= check(f"{s.name} runs the current Release build", not stale,
                    "" if not stale else "differs: " + ", ".join(stale) + " (run with --deploy: docker cp + restart)")
        ok &= check(f"{s.name} ping", _safe_ping(s))
    return ok


def _safe_ping(server):
    try:
        return ping(server)
    except (ApiError, OSError, ValueError):
        return False


def restore_media():
    for rel in MEDIA_FILES:
        target = MEDIA / rel
        if not target.exists():
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(BASE_MKV, target)
            note(f"restored missing media file {rel}")


def catalog(provider, consumer, token=None):
    tok = token if token is not None else (SECRETS / f"{provider}-share-{consumer}.token").read_text().strip()
    status, _, body = http("GET", S[provider].url + "/JellyShare/v1/catalog", headers={"X-JellyShare-Token": tok}, raw=True)
    return status, (json.loads(body) if status == 200 else None)


def ensure_pairing(provider, consumer):
    P, C = S[provider], S[consumer]
    tokfile = SECRETS / f"{provider}-share-{consumer}.token"
    pst, cst = P.status(), C.status()
    wanted_libs = {l["Id"] for l in pst["Libraries"] if l["CollectionType"] in ("movies", "tvshows")}
    share = next((s for s in pst["Shares"] if s["Name"] == consumer), None)
    peer = next((p for p in cst["Peers"] if p["Name"] == provider), None)
    good = False
    if share and share["Enabled"] and wanted_libs <= set(share["LibraryIds"]) and tokfile.exists() \
            and peer and peer["Enabled"] and peer["HasToken"] and peer["BaseUrl"].rstrip("/") == P.internal:
        status, _ = catalog(provider, consumer)
        test = C.api("POST", "/JellyShare/v1/admin/peers/test", {"Url": peer["BaseUrl"], "PeerId": peer["Id"]})
        good = status == 200 and test.get("Ok") is True
    how = "kept existing"
    if not good:
        how = "created"
        for s in [s for s in pst["Shares"] if s["Name"] == consumer]:
            P.api("DELETE", f"/JellyShare/v1/admin/shares/{s['Id']}", raw=True)
        for p in [p for p in cst["Peers"] if p["Name"] == provider]:
            C.api("DELETE", f"/JellyShare/v1/admin/peers/{p['Id']}", raw=True)
        created = P.api("POST", "/JellyShare/v1/admin/shares", {
            "Name": consumer, "LibraryIds": sorted(wanted_libs), "MaxStreams": 2, "MaxBitrate": None})
        SECRETS.mkdir(exist_ok=True)
        tokfile.write_text(created["Token"])
        peer = C.api("POST", "/JellyShare/v1/admin/peers", {
            "Name": provider, "BaseUrl": P.internal, "Token": created["Token"], "Enabled": True})
        del created
        test = C.api("POST", "/JellyShare/v1/admin/peers/test", {"Url": peer["BaseUrl"], "PeerId": peer["Id"]})
        good = test.get("Ok") is True
    check(f"pairing {provider} -> {consumer}: {consumer} consumes {provider} ({how}, connection test ok, "
          f"{test.get('Titles')} titles)", good, test.get("Error") or "")


def ensure_attached(server):
    st = server.status()
    for typ, key in (("Movies", "Movies"), ("Shows", "Shows")):
        if not st[key]["Attached"]:
            server.api("POST", "/JellyShare/v1/admin/libraries/attach", {"LibraryId": server.libs[typ], "Type": typ})
            server.wait_scan_idle()
            note(f"{server.name}: attached {typ} placeholder folder to library {typ}")
    st = server.status()
    check(f"{server.name}: placeholder folders attached to Movies and Shows, series grouping on",
          st["Movies"]["Attached"] and st["Shows"]["Attached"] and st["Shows"]["AutomaticSeriesGrouping"] is True)
    return st


def movie_problems(name, movies):
    problems = []
    counts = collections.Counter(tmdb(m) for m in movies if tmdb(m))
    seen = set(counts)
    if seen != EXPECTED_TMDB:
        problems.append(f"tmdb ids {sorted(seen)} != {sorted(EXPECTED_TMDB)}")
    problems += [f"tmdb {k} x{v}" for k, v in counts.items() if v > 1]
    local = {tmdb(m) for m in movies if tmdb(m) and not is_strm(m)}
    if local != LOCAL_MOVIES[name]:
        problems.append(f"local titles {sorted(local)} != {sorted(LOCAL_MOVIES[name])}")
    nameless = sorted(m["Name"] for m in movies if not tmdb(m))
    expected = ["Home Video"] if name == "alice" else []
    if nameless != expected:
        problems.append(f"movies without tmdb id {nameless} != {expected}")
    return problems


def series_problems(server, series, episodes):
    name = server.name
    problems = []
    pioneer = [s for s in series if (s.get("ProviderIds") or {}).get("Tvdb") == "153021"]
    if len(series) != 1 or len(pioneer) != 1:
        problems.append(f"{len(series)} series ({len(pioneer)} Pioneer One), expected exactly 1")
    keys = [episode_key(e) for e in episodes]
    if sorted(keys) != sorted(ALL_EPISODES):
        problems.append(f"episodes {sorted(keys)} != {sorted(ALL_EPISODES)}")
    if len(pioneer) == 1:
        # What a client shows under the one series. An episode's own SeriesId may name the owner's hidden physical
        # series (Jellyfin merges the folders into one entry), so the merged view is the one that has to be complete.
        shown = server.api("GET", f"/Shows/{pioneer[0]['Id']}/Episodes?" + urllib.parse.urlencode({"userId": server.user}))["Items"]
        shown_keys = sorted(episode_key(e) for e in shown)
        if shown_keys != sorted(ALL_EPISODES):
            problems.append(f"series page lists episodes {shown_keys}")
        seasons = server.api("GET", f"/Shows/{pioneer[0]['Id']}/Seasons?" + urllib.parse.urlencode({"userId": server.user}))["Items"]
        if sorted(s.get("IndexNumber") for s in seasons) != [1, 2]:
            problems.append(f"series page lists seasons {[s.get('IndexNumber') for s in seasons]}")
    local = {episode_key(e) for e in episodes if not is_strm(e)}
    if local != LOCAL_EPISODES[name]:
        problems.append(f"local episodes {sorted(local)} != {sorted(LOCAL_EPISODES[name])}")
    return problems


def state_problems(server):
    return movie_problems(server.name, server.movies()) + series_problems(server, server.series(), server.episodes())


def settle():
    """Run a sync on both servers, then wait (library refresh every 45 s) until both libraries show the expected merged state."""
    for s in (ALICE, BOB):
        report = s.sync()
        check(f"{s.name}: sync finished without peer errors", not report.get("PeerErrors"),
              f"offered {report.get('Offered')}, written {report.get('Written')}, deleted {report.get('Deleted')}, "
              f"skipped local {report.get('SkippedLocal')}, unmatched {report.get('Unmatched')}")
    for s in (ALICE, BOB):
        s.refresh()
    ok = wait_until(lambda: not state_problems(ALICE) and not state_problems(BOB), 120, 3,
                    tick=lambda: [s.refresh() for s in (ALICE, BOB)])
    return bool(ok)


def check_library_content():
    print("== merge and dedupe")
    for s in (ALICE, BOB):
        movies = s.movies()
        dups = [f"{k} x{v}" for k, v in collections.Counter(tmdb(m) for m in movies if tmdb(m)).items() if v > 1]
        check(f"{s.name}: no duplicate TMDB ids in the Movies library", not dups,
              f"{len(movies)} movies" if not dups else ", ".join(dups))
        problems = movie_problems(s.name, movies)
        check(f"{s.name}: Movies = own files + peer placeholders only", not problems, "; ".join(problems))
        problems = series_problems(s, s.series(), s.episodes())
        check(f"{s.name}: Pioneer One is one series with S01E01, S01E02, S02E01", not problems, "; ".join(problems))

    print("== Home Video")
    hits = BOB.items("Movie,Video,Series,Episode,Folder,BoxSet", extra={"searchTerm": "Home Video"})
    bob_paths = [i.get("Path") or "" for i in BOB.items("Movie,Video,Episode,Folder")]
    st = BOB.status()
    roots = [st["Movies"]["Path"], st["Shows"]["Path"]]
    find = sh("docker", "exec", BOB.container, "find", *roots, "-ipath", "*home*", "-o", "-name", "clip*")
    found = find.stdout.decode().strip()
    check("Home Video is not on bob (no item, no placeholder file)",
          find.returncode == 0 and bool(bob_paths) and not hits and not any("clip.mkv" in p or "Home Video" in p for p in bob_paths) and not found,
          f"{len(hits)} search hits, {len(bob_paths)} bob items searched, find exit {find.returncode} '{found}'")
    unmatched = (st.get("LastSync") or {}).get("Unmatched")
    check("bob's sync counted the ID-less title as unmatched, not placeholdered", unmatched == 1, f"Unmatched={unmatched}")
    check("Home Video is still a normal local movie on alice",
          any(m["Name"] == "Home Video" and not is_strm(m) for m in ALICE.movies()))


def catalog_keys(cat):
    """Titles in a catalog as comparable keys: ('movie', tmdb), ('movie-no-id', name), ('episode', season, episode)."""
    keys = set()
    for i in cat["items"]:
        if i["type"] == "Movie":
            keys.add(("movie", i["providerIds"]["Tmdb"]) if i["providerIds"].get("Tmdb") else ("movie-no-id", i["name"]))
        else:
            keys.add(("episode", i["seasonNumber"], i["episodeNumber"]))
    return keys


def expected_catalog_keys(provider):
    """A provider shares only what it owns as files; its peer placeholders must never be offered back (loop guard)."""
    keys = {("movie", t) for t in LOCAL_MOVIES[provider]} | {("episode", s, e) for s, e in LOCAL_EPISODES[provider]}
    if provider == "alice":
        keys.add(("movie-no-id", "Home Video"))
    return keys


def check_loop_guard():
    print("== loop guard (catalogs never contain placeholders)")
    roots = []
    for s in (ALICE, BOB):
        st = s.status()
        roots += [st["Movies"]["Path"], st["Shows"]["Path"]]
    for provider, consumer in (("alice", "bob"), ("bob", "alice")):
        P = S[provider]
        status, cat = catalog(provider, consumer)
        if not check(f"{provider}'s catalog for {consumer} is readable", status == 200, f"HTTP {status}"):
            continue
        got = catalog_keys(cat)
        want = expected_catalog_keys(provider)
        check(f"{provider}'s catalog is exactly {provider}'s own files ({len(want)} titles)", got == want,
              "" if got == want else f"missing {sorted(want - got, key=str)}, unexpected {sorted(got - want, key=str)}")
        ids = [i["id"] for i in cat["items"]]
        resolved = P.api("GET", "/Items?" + urllib.parse.urlencode({"Ids": ",".join(ids), "Fields": "Path,ProviderIds", "userId": P.user}))["Items"] if ids else []
        bad = [i["Path"] for i in resolved if under(i.get("Path"), roots) or is_strm(i)]
        check(f"{provider}'s catalog: every item is a real file outside both servers' placeholder folders",
              bool(ids) and len(resolved) == len(ids) and not bad, f"{len(ids)} items resolved to {len(resolved)}, {len(bad)} under placeholder folders")
        placeholders = P.items("Movie,Episode")
        placeholder_ids = {i["Id"] for i in placeholders if under(i.get("Path"), roots) or is_strm(i)}
        check(f"{provider}'s catalog: contains none of {provider}'s own placeholder items",
              len(placeholder_ids) >= EXPECTED_PLACEHOLDERS[provider] and not placeholder_ids & set(ids),
              f"{len(placeholder_ids)} placeholders on {provider} (expected at least {EXPECTED_PLACEHOLDERS[provider]})")
        movie_keys = {i["providerIds"].get("Tmdb") for i in cat["items"] if i["type"] == "Movie" and i["providerIds"].get("Tmdb")}
        episode_keys = {(i["seasonNumber"], i["episodeNumber"]) for i in cat["items"] if i["type"] == "Episode"}
        leaked = [tmdb(i) for i in placeholders if i["Type"] == "Movie" and is_strm(i) and tmdb(i) in movie_keys]
        leaked += [episode_key(i) for i in placeholders if i["Type"] == "Episode" and is_strm(i) and episode_key(i) in episode_keys]
        check(f"{provider}'s catalog: no title that {provider} only has as a placeholder", not leaked, str(leaked))
    status, _ = catalog("alice", "bob", token="js1_" + "A" * 43)
    check("catalog with a wrong token -> 401", status == 401, f"HTTP {status}")
    status, _, _ = http("GET", ALICE.url + "/JellyShare/v1/catalog", raw=True)
    check("catalog without a token -> 401", status == 401, f"HTTP {status}")


def placeholder(server, tmdb_id):
    for m in server.movies():
        if tmdb(m) == tmdb_id and is_strm(m):
            return m
    return None


def check_relay_and_playbackinfo():
    print("== relay access and PlaybackInfo")
    item = placeholder(BOB, BBB)
    if not check("bob has the Big Buck Bunny placeholder", item is not None):
        return
    key = "movie%3Atmdb%3A" + BBB
    status, _, _ = http("GET", BOB.url + "/JellyShare/v1/play/" + key, raw=True, timeout=30)
    check("relay from the host (non-loopback) -> 403", status == 403, f"HTTP {status}")
    relay = BOB.internal.replace("bob:8096", "127.0.0.1:8096") + "/JellyShare/v1/play/" + key
    code, _, _ = curl_in(BOB.container, relay, headers=["X-Forwarded-For: 203.0.113.9"], args=["-r", "0-1023"])
    check("relay from loopback with X-Forwarded-For -> 403", code == 403, f"HTTP {code}")
    code, size, _ = curl_in(BOB.container, relay, args=["-r", "0-1023"])
    check("relay from loopback (what Jellyfin's ffmpeg does) -> 206, 1024 bytes", (code, size) == (206, 1024), f"HTTP {code}, {size} bytes")

    def leaks(blob):
        text = blob if isinstance(blob, str) else json.dumps(blob)
        return "JellyShare/v1/play" in text or "127.0.0.1" in text

    get = BOB.api("GET", f"/Items/{item['Id']}/PlaybackInfo?userId={BOB.user}")
    ms = get["MediaSources"][0]
    check("GET PlaybackInfo of a placeholder: Path hidden, no relay URL, no direct play",
          ms["Path"] == "" and ms["SupportsDirectPlay"] is False and not leaks(get))
    post = BOB.api("POST", f"/Items/{item['Id']}/PlaybackInfo?userId={BOB.user}",
                   {"UserId": BOB.user, "DeviceProfile": PROFILE, "MaxStreamingBitrate": 120000000})
    ms = post["MediaSources"][0]
    check("POST PlaybackInfo of a placeholder: Path hidden, no relay URL, direct stream kept",
          ms["Path"] == "" and ms["SupportsDirectPlay"] is False and ms["SupportsDirectStream"] is True and not leaks(post))
    dto = BOB.api("GET", f"/Items?Ids={item['Id']}&Fields=MediaSources,Path&userId={BOB.user}")
    check("item DTO with Fields=MediaSources has no relay URL", dto["Items"] and "MediaSources" in json.dumps(dto) and not leaks(dto["Items"][0].get("MediaSources")))
    low = BOB.api("POST", f"/Items/{item['Id']}/PlaybackInfo?userId={BOB.user}",
                  {"UserId": BOB.user, "DeviceProfile": {**PROFILE, "MaxStreamingBitrate": 500000}, "MaxStreamingBitrate": 500000})
    ms = low["MediaSources"][0]
    check("client limit below the clip bitrate -> transcode offered, no direct stream", bool(ms.get("TranscodingUrl"))
          and ms["SupportsDirectStream"] is False and not leaks({k: v for k, v in ms.items() if k != "TranscodingUrl"}))


def probe_first_5s(container, data):
    out = ffprobe(container, data, "-read_intervals", "%+5", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0")
    pts = []
    for line in out.split():
        try:
            pts.append(float(line.split(",")[0]))
        except ValueError:
            pass
    info = json.loads(ffprobe(container, data, "-show_entries", "stream=codec_name,width,height", "-of", "json") or "{}")
    streams = [(s.get("codec_name"), s.get("width"), s.get("height")) for s in info.get("streams", [])]
    return pts, streams


def play_and_probe(server, item, label):
    sample = 2 * 1024 * 1024
    status, headers, data = read_n(f"{server.url}/Videos/{item['Id']}/stream?static=true",
                                   {**server.auth, "Range": f"bytes=0-{sample - 1}"}, sample)
    if not check(f"{label}: static stream with header auth returns the first 2 MiB (206)", status == 206 and len(data) == sample,
                 f"HTTP {status}, {len(data)} bytes"):
        return
    with open(BASE_MKV, "rb") as f:
        same = f.read(sample) == data
    check(f"{label}: streamed bytes are identical to the provider's file", same)
    pts, streams = probe_first_5s(server.container, data)
    check(f"{label}: ffprobe of the first 5 s sees h264 1920x1080 + aac",
          ("h264", 1920, 1080) in streams and any(c == "aac" for c, _, _ in streams), str(streams))
    check(f"{label}: first 5 s of video decode as packets (~24 fps)", len(pts) >= 100 and max(pts or [0]) >= 4.5,
          f"{len(pts)} packets, last pts {max(pts or [0]):.2f}s")


def check_playback():
    print("== playback through the relay")
    item = placeholder(BOB, BBB)
    if not check("bob has the Big Buck Bunny placeholder to play", item is not None):
        return
    play_and_probe(BOB, item, "bob plays alice's Big Buck Bunny")


def share_of(server, peer_name):
    share = next(s for s in server.status()["Shares"] if s["Name"] == peer_name)
    share.setdefault("MaxBitrate", None)  # the API omits null properties
    return share


def set_share(server, peer_name, **changes):
    s = share_of(server, peer_name)
    body = {"Name": s["Name"], "LibraryIds": s["LibraryIds"], "MaxStreams": s["MaxStreams"],
            "MaxBitrate": s["MaxBitrate"], "Enabled": s["Enabled"]}
    body.update(changes)
    server.api("PUT", f"/JellyShare/v1/admin/shares/{s['Id']}", body)
    return share_of(server, peer_name)


def wait_no_streams(provider, peer_name, timeout=90):
    return wait_until(lambda: provider.active_streams(peer_name) == 0, timeout, 2) is True


def check_limit_429():
    print("== provider stream limit")
    token = (SECRETS / "alice-share-bob.token").read_text().strip()
    alice_bbb = next(m for m in ALICE.movies() if tmdb(m) == BBB and not is_strm(m))["Id"]
    alice_sintel = next(m for m in ALICE.movies() if tmdb(m) == SINTEL and not is_strm(m))["Id"]
    check("alice has no active stream before the limit test", wait_no_streams(ALICE, "bob"))
    set_share(ALICE, "bob", MaxStreams=1, MaxBitrate=None)
    base = f"{ALICE.internal}/JellyShare/v1/stream/"
    first = {}

    def hold_first():
        try:
            first["res"] = curl_in(BOB.container, base + alice_bbb, token, args=["--limit-rate", "200k", "-m", "14"], timeout=60)
        except Exception as exc:  # reported by the check below
            first["err"] = type(exc).__name__

    t = threading.Thread(target=hold_first)
    t.start()
    try:
        held = wait_until(lambda: ALICE.active_streams("bob") == 1, 20, 0.5)
        if check("MaxStreams=1: the first title holds its slot (alice reports 1 active stream)", bool(held)):
            code, _, hdrs = curl_in(BOB.container, base + alice_sintel, token, args=["-r", "0-1023"])
            check("MaxStreams=1: second title while the first plays -> 429 with Retry-After: 30",
                  code == 429 and hdrs.get("retry-after") == "30", f"HTTP {code}, Retry-After {hdrs.get('retry-after')}")
            relay = BOB.internal.replace("bob:8096", "127.0.0.1:8096") + "/JellyShare/v1/play/movie%3Atmdb%3A" + SINTEL
            code, _, _ = curl_in(BOB.container, relay, args=["-r", "0-1023"])
            check("MaxStreams=1: the same second title through bob's relay -> 503 (no peer has a free slot)", code == 503, f"HTTP {code}")
            code, size, _ = curl_in(BOB.container, base + alice_bbb, token, args=["-r", "5000000-5001023"])
            check("MaxStreams=1: another Range of the playing title is still allowed (one slot per playback)",
                  (code, size) == (206, 1024), f"HTTP {code}, {size} bytes")
    finally:
        t.join()
        set_share(ALICE, "bob", MaxStreams=2, MaxBitrate=None)
    res = first.get("res")
    check("MaxStreams=1: the first title streamed (200) until the client stopped", res is not None and res[0] == 200 and res[1] > 500_000,
          f"{res[0]} {res[1]} bytes" if res else first.get("err", "no result"))
    check("slot is released after the playback ends (active streams back to 0)", wait_no_streams(ALICE, "bob", 90))
    code, size, _ = curl_in(BOB.container, base + alice_sintel, token, args=["-r", "0-1023"])
    check("after the slot is free the second title streams (206)", (code, size) == (206, 1024), f"HTTP {code}, {size} bytes")


def check_capped_stream():
    print("== provider bitrate cap")
    cap = 500_000
    check("alice has no active stream before the cap test", wait_no_streams(ALICE, "bob", 90))
    set_share(ALICE, "bob", MaxStreams=2, MaxBitrate=cap)
    try:
        token = (SECRETS / "alice-share-bob.token").read_text().strip()
        alice_bbb = next(m for m in ALICE.movies() if tmdb(m) == BBB and not is_strm(m))["Id"]
        sample = 1_000_000
        status, headers, data = read_n(f"{ALICE.url}/JellyShare/v1/stream/{alice_bbb}", {"X-JellyShare-Token": token}, sample, timeout=90)
        lower = {k.lower(): v for k, v in headers.items()}
        check("capped stream from alice: 200 MPEG-TS without byte ranges",
              status == 200 and lower.get("content-type") == "video/mp2t" and lower.get("accept-ranges") == "none",
              f"HTTP {status}, {lower.get('content-type')}, Accept-Ranges {lower.get('accept-ranges')}")
        rate = _stream_bitrate(BOB.container, data)
        check(f"capped stream from alice: ffprobe bitrate ~ cap ({cap // 1000} kbit/s, clip is 1312 kbit/s)",
              0.7 * cap <= rate <= 1.1 * cap, f"{rate // 1000} kbit/s from {len(data)} bytes")

        item = placeholder(BOB, BBB)
        if item is None:
            check("bob has the Big Buck Bunny placeholder for the capped play", False)
            return
        status, headers, data = read_n(f"{BOB.url}/Videos/{item['Id']}/stream?static=true", BOB.auth, sample, timeout=90)
        rate = _stream_bitrate(BOB.container, data)
        check("bob plays the capped title through the relay: ffprobe bitrate ~ cap",
              status == 200 and 0.7 * cap <= rate <= 1.1 * cap, f"HTTP {status}, {rate // 1000} kbit/s from {len(data)} bytes")
    finally:
        set_share(ALICE, "bob", MaxStreams=2, MaxBitrate=None)


def _stream_bitrate(container, data):
    out = ffprobe(container, data, "-show_entries", "format=duration,bit_rate", "-of", "json")
    try:
        fmt = json.loads(out).get("format", {})
        return int(fmt.get("bit_rate") or 0)
    except (ValueError, AttributeError):
        return 0


def check_fallback():
    print("== local file fallback (delete -> peer placeholder, restore -> placeholder gone)")
    local = next((m for m in BOB.movies() if tmdb(m) == SINTEL and not is_strm(m)), None)
    if not check("bob has its own Sintel file", local is not None):
        return
    rel = local["Path"].replace("\\", "/")
    if not rel.startswith("/media/"):
        check("bob's Sintel lives under /media", False, rel)
        return
    host = MEDIA / "bob" / rel[len("/media/"):]
    deleted = False
    try:
        host.unlink()
        deleted = True
        BOB.refresh()
        ph = wait_until(lambda: placeholder(BOB, SINTEL), 120, 3, tick=BOB.refresh, tick_every=40)
        check("local file deleted + library refresh -> bob gets a placeholder from alice within 120 s", ph is not None)
        if ph is not None:
            play_and_probe(BOB, ph, "bob plays alice's Sintel through the placeholder")
        shutil.copyfile(BASE_MKV, host)
        deleted = False
        BOB.refresh()
        ok = wait_until(single_local_sintel, 120, 3, tick=BOB.refresh, tick_every=40)
        sintel = [m for m in BOB.movies() if tmdb(m) == SINTEL]
        check("local file restored + refresh -> placeholder gone, exactly one Sintel (the local file)", bool(ok),
              ", ".join(("placeholder" if is_strm(m) else "local") for m in sintel))
    finally:
        if deleted or not host.exists():
            shutil.copyfile(BASE_MKV, host)
            note("restored bob's Sintel file after a failure")
        if not single_local_sintel():
            BOB.refresh()
            check("cleanup: bob's library is back to exactly one local Sintel",
                  bool(wait_until(single_local_sintel, 120, 3, tick=BOB.refresh, tick_every=40)))


def single_local_sintel():
    sintel = [m for m in BOB.movies() if tmdb(m) == SINTEL]
    return len(sintel) == 1 and not is_strm(sintel[0])


def run():
    ap = argparse.ArgumentParser()
    ap.add_argument("--deploy", action="store_true", help="docker cp the Release JellyShare.*.dll into both containers and restart them first")
    args = ap.parse_args()
    started = time.time()

    if not deploy_and_check(args.deploy):
        return finish(started)
    restore_media()
    for s in (ALICE, BOB):
        s.login()
        s.status()

    print("== pairing")
    ensure_pairing("alice", "bob")
    ensure_pairing("bob", "alice")
    for s in (ALICE, BOB):
        ensure_attached(s)

    originals = {}
    try:
        originals = {"alice": share_of(ALICE, "bob"), "bob": share_of(BOB, "alice")}
        for server, peer in ((ALICE, "bob"), (BOB, "alice")):
            if originals[server.name]["MaxStreams"] != 2 or originals[server.name]["MaxBitrate"] is not None:
                set_share(server, peer, MaxStreams=2, MaxBitrate=None)
                note(f"{server.name}: share limits reset to the defaults (2 streams, no cap); original values are restored at the end")

        print("== sync")
        settled = settle()
        check("both libraries reach the merged state after sync + refresh", settled,
              "; ".join(f"{s.name}: {p}" for s in (ALICE, BOB) for p in state_problems(s)))

        sections = (check_library_content, check_loop_guard, check_relay_and_playbackinfo, check_playback,
                    check_limit_429, check_capped_stream, check_fallback)
        if not settled:
            note("libraries did not reach the merged state: the content checks would only repeat that failure, skipping them")
            sections = ()
        for section in sections:
            try:
                section()
            except Exception as exc:  # a crashing section is a failed check, the others still run
                check(f"{section.__name__} ran to completion", False, f"{type(exc).__name__}: {str(exc)[:200]}")
                traceback.print_exc(limit=3, file=sys.stdout)
    finally:
        print("== restore")
        for server, peer in ((ALICE, "bob"), (BOB, "alice")):
            orig = originals.get(server.name)
            if orig:
                try:
                    now = set_share(server, peer, MaxStreams=orig["MaxStreams"], MaxBitrate=orig["MaxBitrate"], Enabled=orig["Enabled"])
                    check(f"{server.name}: share limits restored", (now["MaxStreams"], now["MaxBitrate"]) == (orig["MaxStreams"], orig["MaxBitrate"]),
                          f"MaxStreams={now['MaxStreams']}, MaxBitrate={now['MaxBitrate']}")
                except Exception as exc:
                    check(f"{server.name}: share limits restored", False, type(exc).__name__)
        restore_media()
        check("media files are in place", all((MEDIA / rel).exists() for rel in MEDIA_FILES))
    return finish(started)


def finish(started):
    print(f"-- {time.time() - started:.0f} s")
    if _failed:
        print(f"FAILED: {len(_failed)} check(s)")
        for name in _failed:
            print("  - " + name)
        return 1
    print("ALL PASS")
    return 0


if __name__ == "__main__":
    sys.exit(run())
