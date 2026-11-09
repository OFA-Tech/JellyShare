#!/usr/bin/env python3
"""Spike-only (media types, 2026-11): can Jellyfin 12.4 build artists/albums/books from link files?

Writes hand-made `.strm` (+ optional `.nfo`) layouts into a fresh scratch folder under
harness/media/alice (= /media/<folder> in alice), adds it as an extra path of alice's Music or
Audiobooks library, scans, prints what Jellyfin built, plays one item, then removes the path again.
The `.strm` URLs point at the spike file server (rangeserver.py) sharing alice's network namespace:
  docker run -d --name jellyshare-spike-http --network container:jellyshare-alice \
    -v "$PWD/harness/spike:/spike" -v "$PWD/harness/media:/srv:ro" python:3.13-alpine \
    python /spike/rangeserver.py /srv 8900 /spike/logs/requests.jsonl
No keys or tokens are ever written to disk (header auth only, see jf.py).

  python music_strm.py music <variant> <fresh-folder-name> [--keep]
  python music_strm.py books <variant> <fresh-folder-name> [--keep]
  python music_strm.py dump music|books
  python music_strm.py streams Music <folder>   (after a --keep run: static/universal/transcode fetches)
  python music_strm.py setmeta Music <folder>   (after a --keep run: write artist/album/track fields
                                                 through the item API, as a plugin could, then rescan)
Variants are the keys of MUSIC_VARIANTS / BOOK_VARIANTS below.
"""
import json
import pathlib
import subprocess
import sys
import time
import urllib.parse

from jf import call, token, user_id

MEDIA = pathlib.Path(__file__).resolve().parent.parent / "media"
SRV = "http://127.0.0.1:8900/"

# (artist, album, NN, title, bob source file)
MUSIC_TRACKS = [
    ("Artist A", "Album One", 3, "Song Three", "bob/Music/Artist A/Album One/03 Song Three.mp3"),
    ("Artist B", "Album Two", 1, "First Light", "bob/Music/Artist B/Album Two/01 First Light.mp3"),
    ("Artist B", "Album Two", 2, "Second Wind", "bob/Music/Artist B/Album Two/02 Second Wind.flac"),
]
BOOK_TRACKS = [
    ("Author X", "Book Y", 1, "Chapter One", "bob/Audiobooks/Author X/Book Y/01 Chapter One.mp3"),
    ("Author X", "Book Y", 2, "Chapter Two", "bob/Audiobooks/Author X/Book Y/02 Chapter Two.mp3"),
]


def album_nfo(artist, album):
    return (f"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<album>\n"
            f"  <title>{album}</title>\n  <artist>{artist}</artist>\n  <albumartist>{artist}</albumartist>\n"
            "</album>\n")


def artist_nfo(artist):
    return (f"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<artist>\n"
            f"  <name>{artist}</name>\n</artist>\n")


def track_nfo(artist, album, nn, title):
    return (f"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<song>\n"
            f"  <title>{title}</title>\n  <artist>{artist}</artist>\n  <albumartist>{artist}</albumartist>\n"
            f"  <album>{album}</album>\n  <track>{nn}</track>\n  <disc>1</disc>\n</song>\n")


# variant -> (file name pattern, write album.nfo/artist.nfo, write per-track nfo)
MUSIC_VARIANTS = {
    "plain": ("{nn:02d} {title}", False, False),
    "dash": ("{nn:02d} - {title}", False, False),
    "nfo": ("{nn:02d} {title}", True, False),
    "tracknfo": ("{nn:02d} {title}", True, True),
}
BOOK_VARIANTS = {
    "plain": ("{nn:02d} {title}", False, False),
}


def write_layout(root, tracks, variant):
    pattern, folder_nfo, per_track = variant
    for artist, album, nn, title, src in tracks:
        folder = root / artist / album
        folder.mkdir(parents=True, exist_ok=True)
        stem = pattern.format(nn=nn, title=title)
        (folder / f"{stem}.strm").write_text(SRV + urllib.parse.quote(src), encoding="utf-8")
        if per_track:
            (folder / f"{stem}.nfo").write_text(track_nfo(artist, album, nn, title), encoding="utf-8")
        if folder_nfo:
            (folder / "album.nfo").write_text(album_nfo(artist, album), encoding="utf-8")
            (root / artist / "artist.nfo").write_text(artist_nfo(artist), encoding="utf-8")
    (root / ".jellyshare").write_text("", encoding="utf-8")


def library(name):
    return next(f for f in call("alice", "GET", "/Library/VirtualFolders") if f["Name"] == name)


def scan():
    call("alice", "POST", "/Library/Refresh")
    time.sleep(3)
    while True:
        tasks = call("alice", "GET", "/ScheduledTasks")
        if all(t["State"] == "Idle" for t in tasks if t["Key"] == "RefreshLibrary"):
            return
        time.sleep(2)


def items(lib_id, types, extra=""):
    q = (f"/Items?Recursive=true&ParentId={lib_id}&IncludeItemTypes={types}&userId={user_id('alice')}"
         f"&Fields=Path,ProviderIds,ParentId&SortBy=SortName{extra}")
    return call("alice", "GET", q)["Items"]


def dump(kind):
    lib = library("Music" if kind == "music" else "Audiobooks")
    lid, uid = lib["ItemId"], user_id("alice")
    out = {"library_paths": lib["Locations"]}
    if kind == "music":
        out["artists"] = [(a["Name"], a["Id"][:8]) for a in
                          call("alice", "GET", f"/Artists?ParentId={lid}&userId={uid}")["Items"]]
        out["album_artists"] = [(a["Name"], a["Id"][:8]) for a in
                                call("alice", "GET", f"/Artists/AlbumArtists?ParentId={lid}&userId={uid}")["Items"]]
        out["albums"] = [(a["Name"], a.get("AlbumArtist"), a["Id"][:8], a.get("Path"), a.get("ProviderIds"))
                         for a in items(lid, "MusicAlbum")]
        out["folders_artists"] = [(a["Name"], a.get("Path")) for a in items(lid, "MusicArtist")]
    types = "Audio" if kind == "music" else "AudioBook,Audio"
    out["tracks"] = [{k: t.get(k) for k in ("Name", "Type", "IndexNumber", "ParentIndexNumber", "Album", "AlbumId",
                                            "AlbumArtist", "Artists", "Path", "RunTimeTicks", "ProviderIds")}
                     for t in items(lid, types)]
    for t in out["tracks"]:
        t["AlbumId"] = (t["AlbumId"] or "")[:8]
    if kind == "books":
        out["folders"] = [(f["Name"], f["Type"], f.get("Path")) for f in items(lid, "Folder")]
    return out


def play(item):
    info = call("alice", "POST", f"/Items/{item['Id']}/PlaybackInfo", {"UserId": user_id("alice")})
    src = info["MediaSources"][0]
    summary = {k: src.get(k) for k in ("Protocol", "IsRemote", "Container", "RunTimeTicks",
                                       "SupportsDirectPlay", "SupportsDirectStream", "SupportsTranscoding")}
    summary["streams"] = [(s["Type"], s.get("Codec")) for s in src.get("MediaStreams", [])]
    status, headers, body = call("alice", "GET", f"/Audio/{item['Id']}/stream?static=true", raw=True)
    summary["stream_status"], summary["stream_bytes"] = status, len(body)
    # ffprobe inside alice; the token is passed as a process argument only, never written to disk.
    probe = subprocess.run(
        ["docker", "exec", "jellyshare-alice", "/usr/lib/jellyfin-ffmpeg/ffprobe", "-v", "error",
         "-headers", f'Authorization: MediaBrowser Client="spike", Device="spike", DeviceId="spike", Version="1", Token="{token("alice")}"\r\n',
         "-show_entries", "stream=codec_name:format=duration", "-of", "compact",
         f"http://127.0.0.1:8096/Audio/{item['Id']}/stream?static=true"],
        capture_output=True, text=True)
    summary["ffprobe"] = (probe.stdout or probe.stderr).strip()
    return summary


def run(kind, variant_name, folder, keep):
    variants, tracks, libname = ((MUSIC_VARIANTS, MUSIC_TRACKS, "Music") if kind == "music"
                                 else (BOOK_VARIANTS, BOOK_TRACKS, "Audiobooks"))
    root = MEDIA / "alice" / folder
    if root.exists():
        sys.exit(f"{root} exists: use a fresh folder name (reused paths bring back old DB items)")
    write_layout(root, tracks, variants[variant_name])
    path = f"/media/{folder}"
    call("alice", "POST", "/Library/VirtualFolders/Paths?refreshLibrary=false",
         {"Name": libname, "PathInfo": {"Path": path}})
    scan()
    result = dump(kind)
    mine = [t for t in items(library(libname)["ItemId"], "Audio,AudioBook") if (t.get("Path") or "").startswith(path)]
    result["play"] = play(mine[0]) if mine else "no item from the scratch folder"
    result["after_play"] = [{k: t.get(k) for k in ("Name", "RunTimeTicks", "Artists", "AlbumArtist", "IndexNumber")}
                            for t in items(library(libname)["ItemId"], "Audio,AudioBook")
                            if (t.get("Path") or "").startswith(path)]
    print(json.dumps(result, indent=1))
    if not keep:
        call("alice", "DELETE", f"/Library/VirtualFolders/Paths?name={libname}&path={urllib.parse.quote(path)}&refreshLibrary=false")
        scan()


def scratch_items(libname, folder):
    return [t for t in items(library(libname)["ItemId"], "Audio,AudioBook")
            if (t.get("Path") or "").startswith(f"/media/{folder}/")]


def streams(libname, folder):
    """Every way a client can fetch audio for the first scratch item: what comes back."""
    item = scratch_items(libname, folder)[0]
    iid, uid = item["Id"], user_id("alice")
    out = {}
    for name, path in [
        ("static", f"/Audio/{iid}/stream?static=true"),
        ("universal", f"/Audio/{iid}/universal?UserId={uid}&Container=mp3,flac&TranscodingContainer=mp3"
                      "&TranscodingProtocol=http&AudioCodec=mp3&MaxStreamingBitrate=140000000"),
        ("transcode_mp3", f"/Audio/{iid}/stream.mp3?audioCodec=mp3&audioBitRate=128000"),
    ]:
        status, headers, body = call("alice", "GET", path, raw=True)
        out[name] = {"status": status, "bytes": len(body), "type": headers.get("Content-Type"),
                     "head": body[:60].decode("latin-1")}
    return out


def set_meta(libname, folder):
    """What a plugin could do through ILibraryManager: write artist/album/track fields on the items."""
    for t in scratch_items(libname, folder):
        rel = t["Path"].split(f"/media/{folder}/", 1)[1]
        artist, album, fname = rel.split("/")
        nn, title = fname.rsplit(".", 1)[0].split(" ", 1)
        dto = call("alice", "GET", f"/Items/{t['Id']}?userId={user_id('alice')}")
        dto.update({"Name": title.lstrip("- "), "Album": album, "IndexNumber": int(nn), "ParentIndexNumber": 1,
                    "ArtistItems": [{"Name": artist}], "AlbumArtists": [{"Name": artist}], "AlbumArtist": artist})
        call("alice", "POST", f"/Items/{t['Id']}", dto)
    # The album takes its artists from its songs on its own refresh.
    for album_id in {t["ParentId"] for t in scratch_items(libname, folder)}:
        call("alice", "POST", f"/Items/{album_id}/Refresh?metadataRefreshMode=FullRefresh&replaceAllMetadata=false")


if __name__ == "__main__":
    if sys.argv[1] == "dump":
        print(json.dumps(dump(sys.argv[2]), indent=1))
    elif sys.argv[1] == "streams":
        print(json.dumps(streams(sys.argv[2], sys.argv[3]), indent=1))
    elif sys.argv[1] == "setmeta":
        set_meta(sys.argv[2], sys.argv[3])
        scan()
        print(json.dumps(dump("music" if sys.argv[2] == "Music" else "books"), indent=1))
    else:
        run(sys.argv[1], sys.argv[2], sys.argv[3], "--keep" in sys.argv)
