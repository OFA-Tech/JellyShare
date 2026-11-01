# JellyShare

A Jellyfin 12 plugin that lets several Jellyfin servers share movies and shows with each other.
Design: [`docs/specs/2026-10-31-jellyfin-federation-plugin-design.md`](docs/specs/2026-10-31-jellyfin-federation-plugin-design.md).
Implementation plan: [`docs/plans/2026-10-31-jellyfin-federation-plugin.md`](docs/plans/2026-10-31-jellyfin-federation-plugin.md).
Verified API names and plan corrections: [`docs/spike-results.md`](docs/spike-results.md).

Status: skeleton. The plugin currently exposes the anonymous `GET /JellyShare/v1/ping` endpoint only.

## Security notes

- Placeholder `.strm` files contain only a loopback relay URL (`http://127.0.0.1:{port}{BaseUrl}/JellyShare/v1/play/{key}`),
  never a token. Peer tokens are sent only in the `X-JellyShare-Token` header and are never logged.
- The standard Jellyfin plugin configuration API (`/Plugins/{id}/Configuration`) returns the whole plugin configuration to
  administrators, including the peer tokens you received and the hashes of the invitations you created. The JellyShare
  admin page never receives them (its own `/JellyShare/v1/admin/...` endpoints are admin-only and return neither), but
  anyone with an administrator login or API key on your server can read them through the standard API.
- The relay answers only requests from loopback that carry **no** proxy header (`X-Forwarded-For`, `X-Forwarded-Host`,
  `X-Forwarded-Proto`, `X-Forwarded-Port`, `X-Real-IP`, `Forwarded`, `Via`, `X-Original-*`, `CF-Connecting-IP`,
  `True-Client-IP`); everything else gets 403. Jellyfin also hides the relay URL from clients.
- **Reverse proxy on the same host as Jellyfin:** it must add `X-Forwarded-For` to the requests it forwards, otherwise
  it looks like a local caller and the relay could be reached from outside through it. Most proxies do this by
  default (Nginx Proxy Manager, Caddy, Traefik). Plain nginx needs:

  ```nginx
  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
  ```

## Stream limits and the bitrate cap

- **Streams at once (per share):** a stream is one (share, title) session. It stays counted while any response for it
  is still being sent, and for 30 s after the last one ends. Extra titles get `429` with `Retry-After: 30`; a request
  for a file that turns out to be unreadable (`503`) gives its new slot back at once.
- **Max bitrate (per share):** when a title's bitrate is above the cap, the provider does not send the file. It runs
  a live transcode with Jellyfin's own ffmpeg (Dashboard → Playback → Transcoding: the ffmpeg path, and the hardware
  encoder when hardware encoding is on: NVENC, QSV, VA-API, VideoToolbox or AMF; otherwise software x264 `veryfast`) to
  MPEG-TS: H.264 at cap − 192 kbps (`-maxrate`/`-bufsize` at that rate) and AAC stereo at 192 kbps. If a hardware
  encoder fails before sending any data, the provider retries in software and skips the hardware encoder for 10
  minutes (one warning in the log). A title whose bitrate is unknown is transcoded too (the bitrate is the container
  total, else video + audio streams). If Jellyfin has no ffmpeg path, or the cap is below 292 kbps (no room for
  video after the audio), the provider answers `409`; if ffmpeg fails before sending anything it answers `503`.
  Either way the consumer tries another peer.
- The transcode counts as a stream like any other. Only one transcode runs per stream (share + title): a new request
  for the same title stops the previous transcode and cuts its connection (this is also how a seek restarts it). The
  server runs at most `MaxConcurrentTranscodes` transcodes across all shares (plugin setting, default 4); beyond
  that capped requests get `429` with `Retry-After: 30`. ffmpeg is killed (whole process tree) as soon as the
  consumer disconnects, the request is replaced, or the transcode finishes.
- **Seeking a capped title:** the transcode has no byte ranges (`Accept-Ranges: none`, Range requests get the whole
  stream from the start, status 200). A peer can start at an offset with `?start=<seconds>` (cut to the runtime).
  On the consumer, Jellyfin sees a live MPEG-TS without a duration: the web client plays it through the consumer's
  own HLS transcode. Seeking works within what the consumer has already received (in the harness a 2-minute clip
  at a 500 kbps cap arrived in ~10 s and a seek to 1:30 played); a seek beyond that waits for the stream to catch up
  or, in clients that reopen the URL, restarts it from the beginning. Uncapped titles keep full byte-range seeking.

## Build and test

Requires the .NET 10 SDK.

```powershell
dotnet build -c Release
dotnet test
```

## Two-server harness

Two Jellyfin 12.4 servers in Docker, with the plugin from `src/Jellyfin.Plugin.JellyShare/bin/Release/net10.0/`
mounted as a dev plugin:

| Server | URL | Notes |
| --- | --- | --- |
| alice | `http://localhost:8101` | libraries "Movies" and "Shows" |
| bob | `http://localhost:8102/jellyfin` | same libraries; server BaseUrl is `/jellyfin` |

Admin login on both: `admin` / `admin` (throwaway harness credentials).

1. Build the plugin: `dotnet build -c Release`.
2. Generate test media (needs ffmpeg; there is none on the host, so use the one inside the Jellyfin image):

   ```powershell
   docker run --rm --entrypoint bash -e FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg -v "${PWD}/harness:/harness" jellyfin/jellyfin:12.4 /harness/make-media.sh
   ```

   (On Linux/macOS with a local ffmpeg: `bash harness/make-media.sh`.) Output goes to `harness/media/`.
3. Start the servers: `docker compose -f harness/docker-compose.yml up -d`.
4. Provision them (startup wizard, libraries, server names, bob's BaseUrl, restart; safe to re-run):

   ```powershell
   py harness/setup.py
   ```

   Python 3, standard library only.
5. Check:

   ```powershell
   curl http://localhost:8101/JellyShare/v1/ping
   curl http://localhost:8102/jellyfin/JellyShare/v1/ping
   ```

   Both return `{"protocolVersion":"1.0","serverName":"..."}` without authentication.

6. Federate (each direction needs a share on the provider and a peer on the consumer):

   ```powershell
   py harness/add_share.py alice bob        # alice shares with bob; token -> harness/.secrets/ (never printed)
   py harness/add_peer.py bob alice --sync  # bob consumes alice, placeholder roots added to bob's libraries, sync run
   py harness/list_items.py bob             # movies/series/episodes with paths and provider ids
   ```

   `py harness/add_peer.py bob --sync` re-runs only the sync task. Placeholders appear in the library about
   60 s after a sync (Jellyfin's `LibraryMonitorDelay`).

After rebuilding the plugin, `docker compose -f harness/docker-compose.yml restart` to load the new DLL.
To start over from scratch: `docker compose -f harness/docker-compose.yml down`, delete `harness/config/`, repeat steps 3-4.

The image tag is `jellyfin/jellyfin:12.4` because `12.4.0` does not exist on Docker Hub.
