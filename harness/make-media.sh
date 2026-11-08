#!/usr/bin/env bash
# Generates tiny-but-real test media for the two harness servers (alice, bob).
# One 2-minute 1080p H.264/AAC MKV (ffmpeg testsrc2 + sine) is encoded once and
# copied to every title path, so a full run takes seconds.
#
# Needs ffmpeg. There is none on the Windows host, so run it inside the Jellyfin
# image, which ships /usr/lib/jellyfin-ffmpeg/ffmpeg:
#   docker run --rm --entrypoint bash -e FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg \
#     -v "${PWD}/harness:/harness" jellyfin/jellyfin:12.4 /harness/make-media.sh
set -euo pipefail

FFMPEG="${FFMPEG:-ffmpeg}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MEDIA="${HERE}/media"
BASE="${MEDIA}/.base.mkv"

mkdir -p "${MEDIA}"
if [ ! -s "${BASE}" ]; then
  "${FFMPEG}" -hide_banner -loglevel error -y \
    -f lavfi -i "testsrc2=size=1920x1080:rate=24:duration=120" \
    -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=120" \
    -c:v libx264 -preset ultrafast -crf 30 -maxrate 1200k -bufsize 2400k -pix_fmt yuv420p -g 48 \
    -c:a aac -b:a 96k -shortest "${BASE}"
fi

put() {
  local target="${MEDIA}/$1"
  mkdir -p "$(dirname "${target}")"
  cp -f "${BASE}" "${target}"
}

# alice: 2 movies + 1 show episode with provider IDs, 1 movie without IDs
put "alice/Movies/Big Buck Bunny (2008) [tmdbid-10378]/Big Buck Bunny (2008).mkv"
put "alice/Movies/Sintel (2010) [tmdbid-45745]/Sintel (2010).mkv"
put "alice/Movies/Home Video/clip.mkv"
put "alice/Shows/Pioneer One [tvdbid-153021]/Season 01/Pioneer One S01E01.mkv"

# bob: Sintel is a duplicate of alice's; Pioneer One has a different episode set
put "bob/Movies/Sintel (2010) [tmdbid-45745]/Sintel (2010).mkv"
put "bob/Movies/Tears of Steel (2012) [tmdbid-133701]/Tears of Steel (2012).mkv"
put "bob/Shows/Pioneer One [tvdbid-153021]/Season 01/Pioneer One S01E01.mkv"
put "bob/Shows/Pioneer One [tvdbid-153021]/Season 01/Pioneer One S01E02.mkv"
put "bob/Shows/Pioneer One [tvdbid-153021]/Season 02/Pioneer One S02E01.mkv"

# Audio: 20 s sine tones, each encoded with its own tags (mp3 = ID3v2, flac = Vorbis comments).
# audio <rel path> <freq> <artist> <album artist> <album> <title> <track> [musicbrainz recording id]
audio() {
  local target="${MEDIA}/$1"
  [ -s "${target}" ] && return 0
  mkdir -p "$(dirname "${target}")"
  local extra=()
  if [ -n "${8:-}" ]; then extra=(-metadata "MUSICBRAINZ_TRACKID=$8"); fi
  "${FFMPEG}" -hide_banner -loglevel error -y \
    -f lavfi -i "sine=frequency=$2:sample_rate=44100:duration=20" \
    -metadata "artist=$3" -metadata "album_artist=$4" -metadata "album=$5" \
    -metadata "title=$6" -metadata "track=$7" -metadata "disc=1" "${extra[@]}" "${target}"
}

# alice owns part of Album One; bob has its missing track 3 plus Album Two (alice lacks it) and a book.
audio "alice/Music/Artist A/Album One/01 Song One.mp3" 330 "Artist A" "Artist A" "Album One" "Song One" 1
audio "alice/Music/Artist A/Album One/02 Song Two.flac" 370 "Artist A" "Artist A" "Album One" "Song Two" 2 \
  "0b3a6f6e-5c1e-4d2a-9b7a-2f1c3d4e5a01"
audio "bob/Music/Artist A/Album One/03 Song Three.mp3" 415 "Artist A" "Artist A" "Album One" "Song Three" 3
audio "bob/Music/Artist B/Album Two/01 First Light.mp3" 440 "Artist B" "Artist B" "Album Two" "First Light" 1
audio "bob/Music/Artist B/Album Two/02 Second Wind.flac" 494 "Artist B" "Artist B" "Album Two" "Second Wind" 2 \
  "0b3a6f6e-5c1e-4d2a-9b7a-2f1c3d4e5a02"
audio "bob/Audiobooks/Author X/Book Y/01 Chapter One.mp3" 262 "Author X" "Author X" "Book Y" "Chapter One" 1
audio "bob/Audiobooks/Author X/Book Y/02 Chapter Two.mp3" 294 "Author X" "Author X" "Book Y" "Chapter Two" 2
mkdir -p "${MEDIA}/alice/Audiobooks"

echo "media ready in ${MEDIA}"
