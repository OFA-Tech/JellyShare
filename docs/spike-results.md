# Spike results

Findings recorded while building; later tasks rely on them. Verified against
`Jellyfin.Controller` / `Jellyfin.Model` **12.0.0** (assemblies `MediaBrowser.Controller`,
`MediaBrowser.Model`, `MediaBrowser.Common` 12.0.0.0, inspected by reflection) and a running
`jellyfin/jellyfin:12.4` container (server version 12.4.0).

## Jellyfin 12 API names

NuGet: stable 12.x packages are `12.0.0`, `12.1.0`, `12.2.0`, `12.3.0`, `12.4.0` (plus `12.0.0-rc*`).
The lowest stable 12 package, `12.0.0`, is referenced for both `Jellyfin.Controller` and
`Jellyfin.Model`. Plugin `targetAbi` is `12.0.0.0`.

| Concept | Name in 12.x | Namespace / assembly |
| --- | --- | --- |
| Plugin base | `BasePlugin<TConfigurationType>` (ctor `(IApplicationPaths, IXmlSerializer)`; overrides `Name`, `Id`) | `MediaBrowser.Common.Plugins` / `MediaBrowser.Common` |
| Config base | `BasePluginConfiguration` | `MediaBrowser.Model.Plugins` / `MediaBrowser.Model` |
| Web pages | `IHasWebPages.GetPages() : IEnumerable<PluginPageInfo>` (`PluginPageInfo`: `Name`, `DisplayName`, `EmbeddedResourcePath`, `EnableInMainMenu`, `MenuSection`, `MenuIcon`) | `MediaBrowser.Model.Plugins` / `MediaBrowser.Model` |
| DI registration | `IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)` | `MediaBrowser.Controller.Plugins` / `MediaBrowser.Controller` |
| Scheduled task | `IScheduledTask`: `Name`, `Key`, `Description`, `Category`, `ExecuteAsync(IProgress<double>, CancellationToken)`, `GetDefaultTriggers() : IEnumerable<TaskTriggerInfo>` | `MediaBrowser.Model.Tasks` / `MediaBrowser.Model` |
| Library item events | `ILibraryManager.ItemAdded`, `ItemUpdated`, `ItemRemoved`, all `EventHandler<ItemChangeEventArgs>` | `MediaBrowser.Controller.Library` / `MediaBrowser.Controller` |
| Library queries | `ILibraryManager.GetItemList(InternalItemsQuery)`, `QueryItems`, `GetItemById(Guid)`, `FindByPath`, `QueueLibraryScan()`, `ValidateMediaLibrary(IProgress<double>, CancellationToken)` | same |
| Library monitor | `ILibraryMonitor.ReportFileSystemChanged(string path)` (also `ReportFileSystemChangeBeginning(string)`, `ReportFileSystemChangeComplete(string, bool)`) | `MediaBrowser.Controller.Library` |
| Server config | `IServerConfigurationManager.Configuration : ServerConfiguration` (`ServerName`, `MetadataPath`, ...) and `.ApplicationPaths` | `MediaBrowser.Controller.Configuration` |
| Network config | `ServerConfiguration` has **no** network settings. Use the extension `IConfigurationManager.GetNetworkConfiguration() : NetworkConfiguration` (`BaseUrl`, `InternalHttpPort`, `InternalHttpsPort`, `PublicHttpPort`, `EnableHttps`, `RequireHttps`, `LocalNetworkSubnets`, `KnownProxies`, ...) | `MediaBrowser.Common.Net.NetworkConfigurationExtensions` / `MediaBrowser.Common` |
| Application host | `IServerApplicationHost`: `FriendlyName`, `HttpPort`, `HttpsPort`, `ListenWithHttps`, `GetSmartApiUrl(...)`, `GetLocalApiUrl(string host, string? scheme, int? port)`, `GetApiUrlForLocalAccess(IPAddress, bool)` | `MediaBrowser.Controller` |

All of these match their 10.x names; nothing had to be renamed.

### Authorization policy for admin-only endpoints (needed by Task 11)

`[Authorize(Policy = Policies.RequiresElevation)]`, where
`MediaBrowser.Common.Api.Policies.RequiresElevation` is the const string `"RequiresElevation"`
(unchanged from 10.x). Related constants in the same class: `LocalAccessOnly`,
`LocalAccessOrRequiresElevation`, `FirstTimeSetupOrElevated`, `FirstTimeSetupOrDefault`,
`Download`, `IgnoreParentalControl`, `AnonymousLanAccessPolicy`.
Anonymous endpoints use `[AllowAnonymous]` (verified in the harness: `/JellyShare/v1/ping` answers
without credentials).

### Internal HTTP port and BaseUrl (to build `http://127.0.0.1:{port}{BaseUrl}`)

- Port: `IServerApplicationHost.HttpPort` (or `GetNetworkConfiguration().InternalHttpPort`). This is the
  port **inside** the server's network namespace, e.g. `8096` in the harness containers, not the
  published `8101`/`8102`. Use `HttpsPort` / `ListenWithHttps` for TLS.
- BaseUrl: `IServerConfigurationManager.GetNetworkConfiguration().BaseUrl`. Stored as written in
  `network.xml` (bob: `/jellyfin`, alice: empty). Jellyfin's own `LocalAddress`
  (`/System/Info/Public`) is `http://<ip>:8096/jellyfin` for bob, i.e. host + port + BaseUrl, so the
  loopback URL is `http://127.0.0.1:{HttpPort}{BaseUrl}` (normalise: no trailing slash, empty when unset).
- Not yet exercised from plugin code: `GetLocalApiUrl("127.0.0.1", "http", HttpPort)` is the host's own
  helper and probably yields the same string; verify when Task 11 needs it.

## Task 2 spike (risky assumptions)

Run on 2026-10-31 against the harness (alice 12.4.0). Throwaway scripts are in `harness/spike/`
(usage in each file's header); source references are to `jellyfin/jellyfin` tag `v12.4` and
`jellyfin/jellyfin-web` tag `v12.4`. For the spike, alice got a harness-owned folder
`harness/media/alice-placeholders` mounted at `/placeholders` and a sidecar `spike-http`
(Range-capable file server that logs request headers) sharing alice's network namespace, so
`http://127.0.0.1:8900/...` inside alice reaches it exactly like the plugin's loopback relay
(`harness/spike/docker-compose.spike.yml`). All of it was removed afterwards (see "Harness state").

### Step 1/2 — series split across two folders: **MERGES (gate passed)**

Setup: `/placeholders/Shows` added as a **second path of the existing "Shows" library**; placeholder
`<series folder>/Season 02/Pioneer One S02E01.strm`; owner's `/media/Shows/Pioneer One [tvdbid-153021]/Season 01`
has no `tvshow.nfo` (its Tvdb id comes from the folder name). Each variant used a fresh folder
name, one scan, and was checked through `/Items?IncludeItemTypes=Series&Recursive=true`,
`/Shows/{id}/Seasons`, `/Shows/{id}/Episodes` (`harness/spike/step1_merge.py`, `step1_inspect.py`).

| Placeholder series folder | `tvshow.nfo` | Placeholder ProviderIds | Result |
| --- | --- | --- | --- |
| `Pioneer One [tvdbid-153021]` (same name) | `<uniqueid type="tvdb">` + `<tvdbid>` | Tvdb 153021 | **1 series, seasons 1+2, episodes S01E01+S02E01** |
| `Pioneer One [tvdbid-153021]` | none | Tvdb 153021 | **merged** |
| `Pioneer One (shared) [tvdbid-153021]` (different name) | none | Tvdb 153021 | **merged** |
| `Pioneer One (shared)` | `<uniqueid type="tvdb">` + `<tvdbid>` | Tvdb 153021 | **merged** |
| `Pioneer One (shared u)` | `<uniqueid type="tvdb">` only | Tvdb 153021 | **merged** |
| `Pioneer One (shared t)` | `<tvdbid>` only | **none** | 2 series (not merged) |
| `Pioneer One` (same name, no id) | none | none | 2 series (not merged) |
| `Pioneer One (control)` | none | none | 2 series (not merged) |

Mechanism (verified in source and by toggling the option):

- Library option **`EnableAutomaticSeriesGrouping`** ("merge series spread across folders";
  model default `true`, shown as `true` in `/Library/VirtualFolders` for the harness libraries).
  Setting it to `false` + scan → the same placeholder shows as a second series; back to `true` +
  scan → one series again.
- `Series.CreatePresentationUniqueKey()` (`MediaBrowser.Controller/Entities/TV/Series.cs`): when
  grouping is on, key = **first user-data key** (`Custom` > `Tvdb` > `Imdb` provider id; **Tmdb is
  not used**) or, when the series has none of those, `"series-" + lower(Name)`; then
  `-{metadata language}-{ids of the collection folders}`. So both folders must be **in the same
  library** and the placeholder's highest-priority id must equal the owner's (owner Tvdb +
  placeholder Imdb-only, or owner Tvdb + placeholder no-id, does not merge).
- `<tvdbid>` alone in `tvshow.nfo` is **not read** as a provider id in 12.4; `<uniqueid type="tvdb">`
  or the `[tvdbid-…]` folder tag is.
- The merged list shows **one surviving Series item = the lowest item Id** of the group
  (`BaseItemRepository.QueryBuilding.cs`, `Min(Id)`; Id is a hash of the path, i.e. arbitrary).
  Here the survivor was the placeholder series (name "Pioneer One" from the nfo, path
  `/placeholders/...`). `/Shows/{id}/Seasons|Episodes` return both seasons for **either** series
  Id. So the show page may display the placeholder's series metadata/images.
- Reusing a deleted folder path brings back the old DB item and its ProviderIds even after the nfo
  is gone (the item Id is path-based) — tests must use fresh names.
- Not needed, so not tried: Merge Versions plugin (Step 2 only applies if the gate fails).

**Empty library folder is never cleaned:** when the last show folder under `/placeholders/Shows`
was deleted, scans kept the old series forever; the log says
`Library folder "/placeholders/Shows" is inaccessible or empty, skipping` (`Folder.IsLibraryFolderAccessible`).
With one file in the root (`.jellyshare` marker) the next scan removed it. The same rule protects
offline drives (Step 5).

**Decision:** keep the design (placeholder folder = extra path of the owner's Shows library).
Write series placeholders as `<Name> [tvdbid-<id>]/Season NN/...` plus a `tvshow.nfo` with
`<uniqueid type="...">` for **every** id known, and — when the owner already has the series — copy
the **owner's local series ProviderIds and title** into the placeholder `tvshow.nfo` so the
grouping key is guaranteed equal and the surviving series looks like the owner's. Keep a marker
file in each placeholder root. Warn in the admin page when the target Shows library has
`EnableAutomaticSeriesGrouping = false`.

### Step 3 — `.strm` → HTTP with Range: **works**

`.strm` content `http://127.0.0.1:8900/bob/Shows/.../Pioneer%20One%20S02E01.mkv` (the clip is
h264 1080p + aac, 120 s). Evidence (`harness/spike/step3_play.py`, ffprobe in the container):

- After a scan the `.strm` episode has **no media info** (`RunTimeTicks` null, no streams): scans
  do not probe `.strm`. The first `PlaybackInfo` probes the URL (one `GET Range: bytes=0-` by
  `Lavf`) and stores it: afterwards `RunTimeTicks=1200210000`, h264 1920x1080 + aac.
- `PlaybackInfo` (no profile) → `Protocol: Http`, `IsRemote: true`, `Path: <the .strm URL>`,
  `SupportsDirectPlay/DirectStream/Transcoding: true`, `Container: mkv`, `Size: 107` (the .strm file).
- Static stream `GET /Videos/{id}/stream?static=true` (header auth): `Range: bytes=0-1048575` → 206,
  1048576 bytes, `Content-Range: bytes 0-1048575/19691603`; mid-file range → 206; full → 200, 19691603
  bytes, ffprobe = original (h264 1920x1080, 120.02 s). Jellyfin proxies the client's `Range`.
- Forced transcode (HLS `master.m3u8`, h264 900 kbps, 640 wide): segment 0 → 200 (ffprobe h264
  640x360 + aac); **seek to 90 s** = request segment 30 of the playlist → server restarts ffmpeg,
  segment start_time 99.98 s (10 s mpegts offset, i.e. 90 s); progressive `stream.ts` with
  `startTimeTicks=900000000` → 30.0 s of h264 640x360. ffmpeg seeks via Range
  (`bytes=14824749-` ≈ 75 % of the file).
- Web client (headless Chromium + jellyfin-web 12.4) also played and seeked to 90 s (see Step 4).
- **User/client max bitrate does not force a transcode for `.strm` items**: `StreamBuilder.IsBitrateLimitExceeded`
  returns false when `IsRemote`. With `MaxStreamingBitrate=1000000` and an mkv/h264/aac
  direct-play profile, the `.strm` item still gets `SupportsDirectPlay: true`, while a local file
  gets a transcode (`ContainerBitrateExceedsLimit`). The brief's "set user max bitrate 1 Mbps"
  check therefore cannot pass for `.strm`; a transcode only happens when the client cannot play
  the format (proved with an HLS-only profile above).
- **Fix proven (review follow-up, `harness/spike/bitrate_test.py`, `web_play_capped.js`):** if the
  `PlaybackInfo` **request** carries `EnableDirectPlay=false` and `EnableDirectStream=false`
  (what a request-side filter sets when `MaxStreamingBitrate` < source bitrate, here 1 000 000 <
  1 312 543), Jellyfin returns `SupportsDirectPlay/DirectStream: false` and a working
  `TranscodingUrl` (`master.m3u8?...VideoBitrate=808000&AudioBitrate=192000...`). Fetching 4 HLS
  segments: HTTP 200, ffprobe h264 1280x720 + aac, 12.0 s, **978 kbps**. In headless Chromium
  (request rewritten the same way, `Path` hidden) jellyfin-web played via HLS (`currentTime 13.8`),
  ffmpeg ran `libx264 ... -maxrate 808000`, and no browser request went to 127.0.0.1. Clearing
  the flags only in the **response** does not work: the default response has
  `TranscodingUrl: null` (computed before), so the filter must change the request before the
  action runs.

Request headers seen by the test server (relay must accept all of these):

| Caller | Headers | Range forms |
| --- | --- | --- |
| Jellyfin's ffmpeg (probe and transcode) | `User-Agent: Lavf/62.12.103`, `Accept: */*`, `Range`, `Connection: close`, `Host: 127.0.0.1:8900`, `Icy-MetaData: 1` | `bytes=0-`, `bytes=N-` (open-ended), never closed ranges |
| Jellyfin static-stream proxy | `User-Agent: Jellyfin-Server/12.4.0`, `Accept`, `Accept-Encoding`, `traceparent`, `Host`, client's `Range` (or none) | whatever the client sent (`bytes=0-1048575`, `bytes=19660800-`, none) |

No auth headers can be added (a `.strm` holds only a URL); the source IP was `127.0.0.1`.

### Step 4 — do clients open the `.strm` URL themselves? **YES (jellyfin-web)** — design change needed

- `PlaybackInfo` hands the raw `.strm` URL to clients in `MediaSources[].Path` with
  `IsRemote: true`, `Protocol: Http`, `SupportsDirectPlay: true` (`BaseItem.GetVersionInfo` sets
  `IsRemote`/`Path` from `ShortcutPath`).
- jellyfin-web 12.4 `playbackmanager.js`: `supportsDirectPlay()` → for `IsRemote` + `Http`
  sources it returns true when the app host supports `RemoteVideo` (every browser except
  Tizen/webOS/Opera TV/Edge UWP), and `isHostReachable()` returns true for any `IsRemote` source
  (the localhost/127.0.0.1 guard only applies to non-remote sources); then `mediaUrl = mediaSource.Path`.
- Proven in a real browser (headless Chromium, `harness/spike/web_play.js`): after `PlaybackInfo`
  the browser itself requested `http://127.0.0.1:8900/bob/Shows/...S02E01.mkv` →
  `net::ERR_CONNECTION_REFUSED`, then retried `PlaybackInfo` with direct play disabled and played
  through a full **libx264 re-encode** (`FFmpeg.Transcode-*.log`: `-codec:v:0 libx264 ...`), although
  the source was browser-playable h264. So it "works" in the web client only through an
  unnecessary transcode, and a browser on the server machine would hit its own 127.0.0.1.
- Proposed fix, proven client-side (`harness/spike/web_play_rewrite.js`, which rewrites the
  `PlaybackInfo` response in the browser the way a server-side filter would): for sources whose
  Path starts with the relay base set `SupportsDirectPlay=false` and `Path=""`. The web client
  then used `GET /Videos/{id}/stream.mkv?Static=true&...` → Jellyfin proxied the relay with Range
  (`Jellyfin-Server/12.4.0`, `bytes=0-`), **no transcode**, and seeking to 90 s worked
  (`currentTime 94.9`, server sent `bytes=14811136-`). No request went to 127.0.0.1 from the browser.
- Native apps (Android, Android TV, Swiftfin, Kodi add-ons, Infuse, ...) could not be tested here
  (no GUI/device). Inferred from `PlaybackInfo`: any client that direct-plays `Path` for remote
  HTTP sources has the same problem; with the filter they only see server URLs. **The owner must
  confirm with a real phone/TV app** (Task 14).
- Signed relay URLs (the spec's fallback) do not fit: a `.strm` is a static file and cannot
  carry short-lived signatures, so the URL would have to be rewritten per request in
  `PlaybackInfo` anyway (the same hook as the filter). The relay would also have to accept
  non-loopback requests (giving up the loopback-only rule) and need a stable public host that
  every client can reach (LAN IP, public domain, sub-path vary per client).

**Decision (accepted by the controller; signed URLs rejected):** keep the loopback-only relay and
`.strm` with no secrets; add a server-side `PlaybackInfo` filter in the plugin that hides the URL,
disables direct play, and forces a transcode when the client's max bitrate is below the source
bitrate (see Plan corrections 9).

### Step 5 — missing-file behaviour and the hook

- Hook: **`ILibraryManager.ItemRemoved`** (and `ItemAdded` for re-appearing files). Fired
  synchronously from `LibraryManager.DeleteItem` → `ReportItemRemoved(item, parent)` while a
  scan/refresh validates the folder; `ItemChangeEventArgs.Item` still has Path and ProviderIds.
  Observed through the WebSocket `LibraryChanged` message (`LibraryChangedNotifier` subscribes to
  the same three events and batches them for `LibraryUpdateDuration` = 30 s):
  `ItemsRemoved: ["40ef…"]` for the deleted movie.
- Timing, file deleted (Big Buck Bunny):
  - `EnableRealtimeMonitor=false` (harness default): the item stays indefinitely (polled 60 s);
    removed by the next library scan (2 s after `POST /Library/Refresh`). Default scheduled scan
    interval is 12 h.
  - `EnableRealtimeMonitor=true` on a native Linux filesystem (container overlay dir added as
    library path): removed **~61 s** after `rm` (= `LibraryMonitorDelay` default 60 s).
    The watcher only starts after the option is saved **and** a scan runs (`LibraryMonitor.Start`
    runs at startup and after each scan).
  - On the harness bind mounts (Docker Desktop/WSL2 `9p` drvfs) inotify never fires, even for
    `rm` inside the container — the harness needs explicit scans.
  - Deleting only the video leaves the empty movie folder, which then appears as a **`Folder`
    item** in the Movies library until the folder is removed.
- Drive offline (alice restarted with an empty dir mounted at `/media`, `spike/docker-compose.offline.yml`):
  after startup and after a full scan **all items are kept** as `LocationType: FileSystem`
  (nothing removed, nothing marked missing, no `ItemRemoved` storm), because an inaccessible or
  empty library root is skipped. After remounting, everything was unchanged.
  In the same offline state a stat of the owner's file inside the container
  (`docker exec jellyshare-alice test -f "/media/Movies/Sintel (2010) [tmdbid-45745]/Sintel (2010).mkv"`)
  reports **MISSING**, and **EXISTS** again after remounting. .NET `File.Exists` uses the same
  `stat`, so it returns false here (assumed, not run from .NET). Caveat: on a hung network mount
  (NFS hard mount, stale SMB) `stat` can block instead of failing, so the sweep must run off the
  request path, with a per-pass time budget.
- `ILibraryMonitor.ReportFileSystemChanged(path)` has no `EnableRealtimeMonitor` check (source):
  it debounces `LibraryMonitorDelay` (60 s) and refreshes that path. Not exercised from plugin code
  yet (needs Task 8).

**Decision for Task 10:** subscribe to `ILibraryManager.ItemRemoved` / `ItemAdded`, filter
`Movie`/`Episode` (ignore `Folder`/`Series`/`Season` and items under the placeholder roots).
Expect removals only after a scan or ~60 s with realtime monitoring. Offline drives produce **no**
events and keep every item, so events alone cannot meet the spec's goal "drive offline → peer
copy appears". Both sides therefore check the file itself: the provider skips missing files
(catalog/stream), and the consumer counts an item as "local" only if its file exists
(`LocalIndex`). Task 10's watcher also runs a periodic **presence sweep** that writes placeholders
for local files that are gone and removes them when the files come back (see corrections 11/12).

### Step 6 — user data carry-over: **free**

- Sintel on alice: `Played`, `PlaybackPositionTicks=600000000`, favourite, play count 1 →
  `UserData.Key = "45745"` (the Tmdb id).
- Deleted the owner's Sintel folder, scanned (item gone), then created
  `/placeholders/Movies/Sintel shared (2010) [tmdbid-45745]/Sintel shared (2010).strm` (new path →
  new item Id `2136…`), scanned: the new item had **Played, resume 60 s, favourite, play count**
  and appeared in `/UserItems/Resume`. Also true when both happen in one scan.
- Keys: movies use the provider id (Tmdb here), episodes `<series key><season:000><episode:000>`
  (`153021002001`), series the series key. Items without provider ids use their Guid (no carry-over).

**Decision:** keep — no plugin code needed, as long as placeholders carry the same provider ids.

### Step 7 — anonymous endpoint behind a sub-path

Proven in Task 1 (`/JellyShare/v1/ping` anonymous on bob under `/jellyfin`). No local HTTPS
nginx was set up; the owner's NPM `/jellyfin` path is tested in Task 14.

### Harness state after the spike

Both servers up, libraries back to `/media/Movies` and `/media/Shows` only, `EnableRealtimeMonitor`
false, `EnableAutomaticSeriesGrouping` true, Big Buck Bunny/Sintel files restored (copies of
`.base.mkv`), Sintel user data reset, `harness/media/alice-placeholders` deleted, `spike-http`
removed, alice recreated from `docker-compose.yml` only. To rerun the spike:
`docker compose -f docker-compose.yml -f spike/docker-compose.spike.yml up -d` (from `harness/`).

## Plan corrections

1. **Docker tag `jellyfin/jellyfin:12.4.0` does not exist** on Docker Hub (`docker pull` returns
   "not found"). Tags are `12`, `12.4`, `12.3`, ... (and `12.4.<date>-<time>` builds). The harness uses
   `jellyfin/jellyfin:12.4`; the running server reports version `12.4.0`.
2. **`Jellyfin.Controller` / `Jellyfin.Model` 12.0.x**: only `12.0.0` exists (plus rc builds). Used `12.0.0`.
3. **No ffmpeg on the host**: `harness/make-media.sh` runs inside the Jellyfin image using its bundled
   `/usr/lib/jellyfin-ffmpeg/ffmpeg` (command in the root `README.md`).
4. **`/System/Restart` cannot be used after changing BaseUrl.** In 12.4, once `network.xml` has a new
   BaseUrl the running server answers 404 on both the old and the new root until it restarts (only the
   root redirect follows the new value), so the API restart is unreachable. `harness/setup.py` therefore
   restarts the container with `docker restart`. (A plain `POST /System/Restart` works fine otherwise.)
5. **Online metadata makes the "no IDs" movie get IDs.** With default library settings and internet access,
   Jellyfin matched `Movies/Home Video` to a TMDB entry and resolved `tvdbid-153021` to *The Walking Dead*
   (not Pioneer One) and replaced names. `setup.py` creates the libraries with empty `MetadataFetchers` /
   `ImageFetchers` for every type, so items keep only the provider IDs written in their folder names
   (`Tmdb`/`Tvdb`), `Home Video` stays ID-less, and item names stay as the raw folder names
   (e.g. `Sintel (2010) [tmdbid-45745]`). Later tasks and e2e checks must key on `ProviderIds`, not on names.
   Note also that file-based episodes get no `ProviderIds` in this mode (episodes are matched by series +
   season/episode numbers).
6. **Startup wizard / provisioning notes**: right after container start the API answers 503 for a few
   seconds; `setup.py` retries. Authentication uses the `Authorization: MediaBrowser ... Token="..."`
   header only.
7. **Home Video** exists only on alice (the brief lists "one movie without IDs"); bob has none.

From the Task 2 spike (details above):

8. **Task 4 (NFO / naming) — series ids.** `NfoWriter.Series` must write `<uniqueid type="tvdb">`,
   `type="imdb"`, `type="tmdb"` for every id known (not only `tmdb`; `<tvdbid>` alone is ignored).
   Series grouping uses Custom > **Tvdb** > Imdb, never Tmdb. Add an optional "local series"
   input: when the owner already has the series, the placeholder `tvshow.nfo` copies the **local
   series' ProviderIds and title** (so the grouping key is identical and the surviving merged
   series looks like the owner's). Folder tag stays `[tvdbid-…]`; when the series has no Tvdb id,
   the folder tag must not add an id the owner's series lacks (use `[imdbid-…]` if that is the
   owner's top id; Tmdb-only series merge only by name, so then use the owner's series name).
   New tests: `Nfo_Series_WritesAllUniqueIds`, `Nfo_Series_UsesLocalSeriesIdsAndTitle`.
9. **Task 9 (relay) — clients DO open `.strm` URLs; PlaybackInfo filter (accepted; signed URLs
   rejected).** Add `Consumer/PlaybackInfoFilter.cs`, an MVC **action** filter
   (`IAsyncActionFilter`, registered in `IPluginServiceRegistrator` via
   `services.Configure<MvcOptions>(o => o.Filters.Add<PlaybackInfoFilter>())` or `IStartupFilter`)
   on `GET /Items/{id}/PlaybackInfo` and `POST /Items/{id}/PlaybackInfo`, applied only when the
   item is a JellyShare placeholder (path under a placeholder root / `.strm` starting with the relay base):
   - **Before the action (request):** when the effective max bitrate (`MaxStreamingBitrate` from
     query/body, else `DeviceProfile.MaxStreamingBitrate`, else the user's remote/LAN limit) is
     below the source bitrate (stored media info, or the catalog bitrate from the peer), set
     `EnableDirectPlay=false` **and** `EnableDirectStream=false` in the request so Jellyfin builds a
     transcode at that bitrate. Spike proof: 1 Mbps request on a 1.31 Mbps `.strm` →
     `TranscodingUrl` with `VideoBitrate=808000`, HLS output 978 kbps 720p, and jellyfin-web played it
     (Step 3). Changing only the response does not work (`TranscodingUrl` would be null).
   - **After the action (response):** for every `MediaSourceInfo` whose `Path` starts with the
     relay base: `SupportsDirectPlay = false`, `Path = ""` (keep `SupportsDirectStream` /
     `SupportsTranscoding` unless cleared above). Clients then use
     `/Videos/{id}/stream.{container}?Static=true` (Jellyfin proxies the relay) or the transcode URL.
   - Also hide `Path` in item DTOs: `MediaSources` returned by `/Items`, `/Items/{id}`,
     `/Users/{u}/Items/{id}` with `Fields=MediaSources` (same filter on those actions, or a DTO hook).
   - **Task 9 gate (new step):** in the harness prove (a) the filter is registered and runs on
     **both** GET and POST PlaybackInfo, (b) `Fields=MediaSources` item responses contain no relay
     URL, (c) `web_play.js` without the client-side rewrite → no browser request to 127.0.0.1 and
     no libx264 in the FFmpeg log, (d) a 1 Mbps request gives a `TranscodingUrl` and ffprobe of the
     output ≈ 1 Mbps. Tests: `Filter_HidesRelayPathAndDisablesDirectPlay`,
     `Filter_LowMaxBitrate_DisablesDirectStream`, `Filter_HighMaxBitrate_KeepsDirectStream`,
     `Filter_IgnoresNonPlaceholderItems`, `Filter_RunsOnGetAndPostPlaybackInfo`.
   - Relay: keep loopback-only (`RemoteIpAddress` loopback) **and** reject (403) any request
     carrying `X-Forwarded-For`, `X-Real-IP` or `Forwarded` (a reverse proxy on the same host would
     otherwise look like loopback). Handle `bytes=N-` open-ended ranges, no Range, and the
     `Icy-MetaData`/`Connection: close` headers ffmpeg sends.
   - Stream slots: the first `PlaybackInfo` ffprobe of a `.strm` (`Lavf`, `Range: bytes=0-`) goes
     through the relay to the peer. It belongs to the same playback (probe, then
     playback/transcode requests, and HLS seeks reopen the URL), so per-peer limits must not count
     each relay request as a new stream. Count a stream per (consumer, item) with a short grace
     window (e.g. 30 s after the last request), not per HTTP request (Task 7 limit + Task 9 relay).
   - Task 9 Step 5 "forced transcode at 1 Mbps" is done through the request-side rule above (set
     the client's quality to 1 Mbps). The provider-side cap (Task 12) stays as the provider's own
     control. Task 14 must test one native phone/TV app.
10. **Task 8 (writer) — keep a marker file.** An empty placeholder root is skipped by scans, so the
    last removed placeholder would never disappear. `PlaceholderWriter` creates `.jellyshare` in
    each root and never deletes it ("remove empty dirs" stops at the root). Movie placeholders are
    removed as a whole folder (`.strm` + nfo + images), otherwise Jellyfin shows an empty `Folder`
    item. Placeholder roots must be added as extra paths of the chosen Movies/Shows library (not
    a separate library) — the series grouping key includes the collection folder ids.
    Admin page (Task 11): warn when the chosen Shows library has `EnableAutomaticSeriesGrouping=false`.
11. **Task 10 (watcher) — hook and timing.** Use `ILibraryManager.ItemRemoved` / `ItemAdded`
    (as planned). They fire only when a scan or the realtime monitor (≈60 s,
    `LibraryMonitorDelay`) notices the change; with `EnableRealtimeMonitor=false` or on mounts
    without inotify (the harness, NFS/SMB) only at the next scan. Ignore `Folder` items. Task 10
    Step 5 in the harness must `POST /Library/Refresh` after deleting the file instead of waiting
    "~1 min". Drive-offline does **not** produce events (items are kept), so events alone cannot
    meet spec goal 3. **Add a presence sweep** to `LocalItemWatcher`: every **60 s** (configurable),
    run off the request path, stat (`File.Exists`) the files of local (non-placeholder)
    Movie/Episode items **whose keys appear in the merged index**, i.e. only titles a peer could
    replace (bounded set). Local file missing → write that entry's placeholder (same path as the
    event path) and scan its dir. File back → delete the placeholder and scan. Keep per-key state
    so each title is written/removed once. The 15 s batching and the >500 storm rule apply (a
    whole drive offline → hand to a full `SyncService.RunAsync`, which uses the same
    `LocalIndex` rule below). Cap each pass by a time budget (e.g. 10 s) and continue next pass,
    because `stat` on a hung network mount can block. While the drive is offline the owner's stale
    item stays in the library next to the placeholder (two entries for movies; one series for
    shows); accepted, the placeholder plays.
    Tests: `Sweep_LocalFileMissing_WritesPlaceholder`, `Sweep_FileBack_RemovesPlaceholder`,
    `Sweep_OnlyChecksKeysInMergedIndex`, `Sweep_ManyMissing_DelegatesToFullSync`,
    `Sweep_RespectsTimeBudget`. Harness check: start alice with
    `spike/docker-compose.offline.yml` → within ~1–2 min placeholders appear for titles a peer has;
    remount → they are removed.
12. **Offline files stay in the DB: "local" must mean the file exists (Tasks 6/7 and 8).**
    With the media drive offline Jellyfin keeps every item as present (stat of the file in the
    container says missing; .NET `File.Exists` assumed to agree).
    - Provider (Task 6/7): the catalog skips items whose `Path` does not exist, and the stream
      endpoint answers **503** (not 500) when the file cannot be opened, so the consumer fails
      over to the next peer.
    - Consumer (Task 8): `LocalIndex.GetLocalKeys` counts an item as local **only if its file
      exists** (non-placeholder and `File.Exists(path)`), so a sync during an outage writes
      placeholders for the missing titles. Tests: `LocalIndex_MissingFile_NotLocal`,
      `LocalIndex_ExistingFile_IsLocal`, `Sync_LocalFileMissing_WritesPlaceholder`.
13. **Task 10 user data:** watched/resume/favourite carry over automatically between a local item
    and a placeholder with the same provider id (movie key = provider id, episode key = series key
    + season/episode numbers). No plugin code; Task 10 Step 5 just records it.
