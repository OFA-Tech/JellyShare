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

echo "media ready in ${MEDIA}"
