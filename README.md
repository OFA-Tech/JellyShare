# JellyShare

A Jellyfin 12 plugin that lets several Jellyfin servers share movies and shows with each other.
Design: [`docs/specs/2026-10-31-jellyfin-federation-plugin-design.md`](docs/specs/2026-10-31-jellyfin-federation-plugin-design.md).
Implementation plan: [`docs/plans/2026-10-31-jellyfin-federation-plugin.md`](docs/plans/2026-10-31-jellyfin-federation-plugin.md).
Verified API names and plan corrections: [`docs/spike-results.md`](docs/spike-results.md).

Status: skeleton. The plugin currently exposes the anonymous `GET /JellyShare/v1/ping` endpoint only.

## Security notes

- Placeholder `.strm` files contain only a loopback relay URL (`http://127.0.0.1:{port}{BaseUrl}/JellyShare/v1/play/{key}`),
  never a token. Peer tokens are sent only in the `X-JellyShare-Token` header and are never logged.
- The relay answers only requests from loopback that carry **no** proxy header (`X-Forwarded-For`, `X-Forwarded-Host`,
  `X-Forwarded-Proto`, `X-Forwarded-Port`, `X-Real-IP`, `Forwarded`, `Via`, `X-Original-*`, `CF-Connecting-IP`,
  `True-Client-IP`); everything else gets 403. Jellyfin also hides the relay URL from clients.
- **Reverse proxy on the same host as Jellyfin:** it must add `X-Forwarded-For` to the requests it forwards, otherwise
  it looks like a local caller and the relay could be reached from outside through it. Most proxies do this by
  default (Nginx Proxy Manager, Caddy, Traefik). Plain nginx needs:

  ```nginx
  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
  ```

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
