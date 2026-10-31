# Jellyfin federation plugin — design

Working name: **JellyShare** (rename freely). Status: design, not started. To be built in its own
repository in a separate session; this document is the hand-off.

## Intent

The owner and several friends each run their own Jellyfin 12 server. They want to share movies
and shows with each other so everyone has more to watch, **without copying files and without
giving up their own server**. Shared titles must look and behave like local ones: they appear
inside the normal Movies and Shows libraries, play in every Jellyfin client, and a title that
exists on several servers appears **once**.

## Goals

1. **Many peers.** A server can connect to any number of friends' servers ("peers").
2. **Merged, deduplicated library.** Titles from peers appear in the consumer's existing Movies
   and Shows libraries, visible to **all** users who can see those libraries. A movie, show or
   episode available on several servers has exactly one entry, matched by provider IDs (TMDB,
   IMDb, TVDB), not by file name.
3. **Owner's copy first.** If the consumer owns the title, it plays from local disk. A peer copy
   is used only when the local file is gone (deleted, drive offline). The fallback happens
   automatically at the library level, shortly after the file disappears (seconds to a
   minute), not mid-playback.
4. **Per-episode merge.** A show is one entry; each episode comes from local disk if present,
   otherwise from a peer. Example: owner has S1, a friend has S1–S3 → one show, S1 local,
   S2–S3 from the friend.
5. **Provider limits.** Each server owner controls, per peer, what they give out: which
   libraries, how many simultaneous streams, and an optional quality cap (max bitrate).
6. **Works with stock clients.** Web, Android, iOS, TV apps, Infuse, etc. need nothing new.
7. **Survives Jellyfin updates** as well as a plugin reasonably can: use public, stable
   extension points.

## Non-goals (v1)

- No mid-playback failover (if a stream dies, the user restarts playback).
- Guaranteed preservation of watched status / resume points / favorites when a title switches
  between the local copy and a peer placeholder (a different library item). **Owner: it should
  be preserved, but losing it is acceptable** → best effort: if Jellyfin 12 keys user data in a
  way that carries over (or a cheap, public-API copy is possible), do it; otherwise skip.
- No sync of watched status, favorites or resume points between servers. Each server keeps its
  own user data. Peers never see what the consumer's users watch.
- No music, books, photos, live TV.
- No re-sharing: a server only offers **its own local files** to peers, never titles it
  receives from other peers (no transitive spread beyond each owner's choice).
- No central service, discovery, or accounts; peers are added by hand.

## Anti-goals (would make it a failure even if it "works")

- Giving a peer admin power over your server (e.g. exchanging Jellyfin API keys, which are
  full-admin).
- Leaking local users' activity or identities to peers.
- Duplicate entries for the same title, or peer copies taking over titles the owner has locally.
- Breaking or slowing the owner's own library (huge scans, DB lock storms, mass metadata
  refreshes on every sync).

## Constraints and facts

- Target: **Jellyfin 12.x** server (owner's is 12.4.0; the friend is also on 12). Plugin =
  .NET 10 class library (`net10.0`), `Jellyfin.Controller`/`Jellyfin.Model` 12.x packages,
  `targetAbi` 12.0.0.0. Plugins built for 10.11 do not load on 12.
- Jellyfin 12 **rejects `?api_key=` query authentication** (observed: 401). So a `.strm` file
  cannot simply contain a peer URL with a key in it; something must add credentials in a header.
- Jellyfin API keys are full admin; never use them for peering.
- Owner's Jellyfin runs behind nginx-proxy-manager at a sub-path
  (`https://<host>/jellyfin/`); peers must work with sub-paths and HTTPS reverse proxies.
- SQLite DB: avoid writes that trigger large library operations; the owner already hit a
  15-minute DB lock from a bulk delete.
- Home upload bandwidth is the scarce resource on the provider side.
- Jellyfin 12 merges multiple versions of a movie natively when they're in the same folder;
  otherwise separate entries.

## Approaches considered

**A. Materialized placeholders (`.strm` + `.nfo` + images) — CHOSEN.** The plugin writes, for
each title the consumer doesn't have, a small `.strm` link plus an `.nfo` with metadata/IDs and
artwork into a plugin-managed folder that is added as an extra folder to the existing Movies
and Shows libraries. Jellyfin then treats them as normal items (metadata, episodes, transcoding,
all clients). The `.strm` points to the consumer's **own** plugin endpoint, which picks a peer
and relays the stream with proper auth. Proven idea (JellyFed, 10.11-only). Trade-off: a short
gap before a fallback placeholder appears, and a generated folder to manage.

**B. Virtual items / injected media sources.** No files; the plugin adds peer media sources to
items through internal server interfaces, enabling instant play-time fallback. Rejected: relies
on internal APIs that change between major versions (the 10.11→12 break), much harder to build
and keep alive.

**C. Channel plugin.** Jellyfin's built-in remote-content mechanism. Rejected: content appears
in a separate Channels section, never merged into Movies/Shows.

## Design

### Components (one plugin, installed on every participating server)

Each server plays two roles at once:

- **Provider**: exposes its local Movies/Shows catalog to approved peers and serves streams,
  enforcing per-peer limits.
- **Consumer**: pulls peers' catalogs, writes placeholders for titles it lacks, and relays
  playback.

### Peering and trust

- Pairing is by hand: owner A creates a **peer invitation** in A's plugin settings (a peer name
  plus a generated secret token, shown once); friend B adds A as a peer with A's public URL and
  that token, and vice versa for the other direction. Each direction is independent: A can share
  with B without B sharing back.
- The token identifies the peer to the provider. It grants **only** the plugin's own federation
  endpoints (catalog, images, stream) for the libraries A chose for that peer. It is not a
  Jellyfin user or API key and gives no access to the rest of the server.
- Tokens are revocable per peer. Store them hashed on the provider side; store peers' tokens on
  the consumer side in the plugin config (admin-only).
- Transport: HTTPS through each owner's normal reverse proxy. Peer base URL may include a
  sub-path.

### Provider side

- **Sharing settings per peer:** which libraries (Movies/Shows), max simultaneous streams,
  optional max bitrate, enabled/disabled.
- **Catalog endpoint:** returns the shared items with stable identity and enough metadata to
  build placeholders: type (movie/series/season/episode), title, year, provider IDs
  (TMDB/IMDb/TVDB), season/episode numbers, runtime, basic media info (container, video codec,
  resolution, audio/subtitle tracks), image references, and a change marker so consumers can
  sync incrementally (e.g. last-modified/date-added cursor, or a full snapshot with a version
  hash). Only items backed by **local files** are listed; placeholders created by this plugin
  are excluded (no re-sharing, no loops).
- **Image endpoint:** serves posters/backdrops for shared items.
- **Stream endpoint:** serves the media for a shared item to an authenticated peer. Supports
  HTTP range requests (seeking). Enforces the per-peer concurrent stream limit (reject with a
  clear status when full) and, when a max bitrate is set and the source exceeds it, serves a
  provider-side transcode within the cap (implementation choice left to the builder; see
  Builder's choices).
- Logs per-peer usage (streams started, bytes) for the owner.

### Consumer side

- **Peer list:** name, base URL, token, enabled, priority order (used to pick among several
  peers that have the same title).
- **Sync job** (scheduled task, e.g. every few hours, plus manual "sync now"):
  1. Fetch each enabled peer's catalog.
  2. Build a merged index keyed by provider ID (movie: TMDB/IMDb; episode: series provider ID +
     season + episode number). Several peers with the same key collapse into one entry with a
     list of sources.
  3. Compare against the consumer's **local** items (by provider ID). Anything the consumer has
     locally gets **no** placeholder.
  4. Write/update/remove placeholders in the plugin-managed folders so the folder content equals
     "titles available from at least one peer and not available locally". Write only what
     changed. Remove placeholders when no peer offers the title anymore.
  5. Trigger a targeted library scan of only the placeholder folders (not a full refresh).
- **Local-file watcher:** when a local movie/episode is removed from the library (file deleted,
  drive offline → item missing/removed), check the merged index; if a peer has it, write its
  placeholder immediately and scan that path. When a local copy (re)appears, remove the
  placeholder for that key so the local copy wins.
- **Placeholder layout** (per media type folder, e.g. `<plugin data>/Movies` and
  `<plugin data>/Shows`, which the owner adds as extra folders to the existing Movies and Shows
  libraries):
  - Movie: `Title (Year) [tmdbid-123]/Title (Year).strm` + `movie.nfo` + `poster.jpg` /
    `fanart.jpg`.
  - Show: `Show (Year) [tvdbid-456]/Season 02/Show S02E01.strm` + episode `.nfo`, plus
    `tvshow.nfo` and artwork.
  - The `.strm` content is a URL to the consumer's **own** relay endpoint, e.g.
    `http://127.0.0.1:8096/<base>/JellyShare/play/<merged-key>` (exact form is the builder's
    call; it must not contain secrets).
  - NFO carries IDs and peer-provided metadata so Jellyfin doesn't need heavy lookups, and
    stream details so clients get correct codec info before playback.
- **Relay endpoint (play-time):** receives the request from the consumer's Jellyfin (ffmpeg or
  direct stream), looks up the sources for that key, picks the first healthy peer in priority
  order that isn't at its limit, and proxies the stream from that peer's stream endpoint with
  the peer token in a header, passing range requests through. If a peer refuses (limit, offline),
  try the next peer before the stream starts. The consumer's Jellyfin then does whatever
  transcoding its own client needs, exactly as for a local file.

### Shows: one entry per show

Requirement: owner's local `Show/Season 01` and the placeholder `Show/Season 02..03` must appear
as **one** series. Whether Jellyfin 12 merges a series spread over two folders of the same
library (same name / same provider ID) must be **verified first** (see Risks).

**Owner's decision:** the plugin must **not write anything into the owner's (or a peer's) own
media folders**; it only writes inside its own placeholder folders. So if the spike shows split
series don't merge, the fallback is **not** writing into the owner's show folders. Instead:
look for another approach that keeps one entry (e.g. some Jellyfin 12 mechanism found in the
spike), and if none exists, accept that a partially-owned show appears twice (the owner's copy,
plus a peer copy holding only the missing seasons/episodes) and report that limitation back to
the owner before building further. Fully peer-only shows are unaffected.

### Admin UI (plugin configuration page)

- "Share with peers" tab: invitations, per-peer libraries, limits, revoke, usage.
- "Receive from peers" tab: add peer (URL + token), test connection, priority order, enable,
  last sync result, counts (titles offered / placeholders written / skipped because local).
- Clear setup instruction: add the two placeholder folders to the existing Movies and Shows
  libraries.

## Risks and things to prove first (spike before building)

1. **Series spanning two folders** merges into one series in Jellyfin 12 (see Shows above).
   Highest risk to the per-episode goal.
2. **`.strm` → local HTTP endpoint → range requests**: ffmpeg probing, seeking, direct play and
   transcoding all work through the relay, from the web client and at least one TV/mobile app.
3. **Missing-file detection**: what Jellyfin 12 does (and how fast) when a file is deleted or a
   whole drive is offline (item removed vs. marked missing), and which event the plugin can
   hook to react.
4. **Library impact**: adding/removing thousands of placeholders triggers acceptable scans and
   no DB lock storms (batch writes, targeted path scans).
5. Plugin HTTP endpoints reachable **anonymously** by peers (no Jellyfin login), with the
   plugin doing its own token check, behind a sub-path reverse proxy.

## Calls made by the designer (not discussed with the owner; owner may override)

- **Remote users must be able to play peer titles** (the owner's users stream from outside the
  home through NPM). The relay endpoint accepts only requests from the consumer's own Jellyfin
  (loopback / internal), never anonymous internet requests. All peer playback must go through
  the consumer's server; the spike verifies no client tries to open the `.strm` URL itself. If
  some client does, switch to short-lived signed relay URLs (still no secrets in `.strm`).
- **One target library per type in v1:** the owner chooses one Movies and one Shows library to
  receive placeholders. "Already have it locally" checks **all** local libraries of that type.
  Providers control what is exposed by choosing which of their libraries each peer gets.
- **Distribution:** a Jellyfin plugin repository (manifest JSON + GitHub Releases built by CI),
  so peers install and update from Dashboard → Plugins → Catalog.
- **Versioned federation protocol:** catalog/stream responses carry a protocol version; a peer
  with an incompatible major version shows a clear error in the admin UI instead of failing
  silently.
- Conflicting IDs between peers (same TMDB id, different titles) and whole-drive-offline storms
  (thousands of items disappearing at once) are handled by the builder: batch and rate-limit
  placeholder writes and scans.

## Builder's choices (not decided with the owner)

- Exact names, endpoint paths, token format, catalog format, sync cursor mechanism.
- How the provider applies the quality cap (e.g. request a transcoded stream from its own
  Jellyfin internally vs. a simple ffmpeg pipeline), as long as the cap is enforced.
- Sync interval default, retry/backoff, timeouts.
- Where the placeholder folders live (inside Jellyfin's data dir or a configurable path).
- Test strategy (two Jellyfin 12 containers in docker compose is the obvious harness).

## Owner's environment (for testing later)

- Owner's server: Jellyfin 12.4.0 in Docker Swarm (service `jellyfin_app`, node nitro-server,
  NVIDIA NVENC), behind NPM at `https://hub-ofatech.ddns.net/jellyfin/`. Other installed
  plugins include Jellyfin Enhanced, Home Screen Sections, Media Bar, Merge Versions, Intro
  Skipper, File Transformation.
- Ops notes and how to deploy a plugin build there live in the HomeLab repo
  (`docs/apps/jellyfin/README.md`).
