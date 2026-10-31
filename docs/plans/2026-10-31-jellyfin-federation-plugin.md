# JellyShare (Jellyfin federation plugin) Implementation Plan

> **Spike results change later tasks.** Before starting Tasks 4, 6–12 and 14 read
> `docs/spike-results.md` → "Plan corrections" (items 8–13 from the Task 2 spike): series NFO ids
> (Task 4); a `PlaybackInfo` filter (Task 9, accepted, signed URLs rejected) that hides the relay
> URL, disables direct play and forces a transcode when the client's max bitrate is below the
> source; relay rejects forwarded headers; stream slots per playback, not per request (Tasks 7/9);
> a marker file in each placeholder root (Task 8); scan-driven `ItemRemoved` timing plus a 60 s
> presence sweep for offline drives (Task 10); "local" = file exists in `LocalIndex` (Task 8) and in
> the provider catalog (Task 6/7).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Jellyfin 12 plugin that lets several independent Jellyfin servers share movies and
shows with each other: peer titles appear (deduplicated) inside each server's own Movies/Shows
libraries as `.strm` placeholders and play through an authenticated relay, local copies always
win, and each provider enforces per-peer limits.

**Architecture:** One plugin installed on every server, acting as **provider** (token-protected
catalog/image/stream endpoints over its local files, with per-peer library selection, stream
limit and bitrate cap) and **consumer** (scheduled sync builds a merged index keyed by provider
IDs, writes `.strm`/`.nfo`/artwork only for titles not present locally into two plugin-owned
folders, a loopback-only relay endpoint proxies playback to the best peer, and a library watcher
re-creates/removes placeholders when local files disappear/reappear). Pure logic (keys, merge,
naming, planning, limits, tokens) is unit-tested; Jellyfin integration is proven against two
real Jellyfin 12.4 containers.

**Tech Stack:** C# / .NET 10 (`net10.0`), `Jellyfin.Controller` + `Jellyfin.Model` 12.x NuGet
packages, ASP.NET Core controllers, xUnit, Docker Compose (two `jellyfin/jellyfin:12.4.0`
containers), ffmpeg (test media), Python 3 (e2e script), GitHub Actions (releases + plugin
repository manifest).

**Spec:** `docs/specs/2026-10-31-jellyfin-federation-plugin-design.md` (copied into the new repo
in Task 1; original in the HomeLab repo under `docs/superpowers/specs/`). Read it before Task 1.

**Repository:** `D:\Development\PERSONAL\jellyfin-plugin-jellyshare` (new, the owner's own
GitHub account). Working name "JellyShare"; all names below use it.

## Global Constraints

- Server target **Jellyfin 12.x**; plugin `TargetFramework` `net10.0`; `targetAbi` `12.0.0.0`.
  10.11 builds do not load on 12.
- Jellyfin 12 **rejects `?api_key=` query auth** → never put credentials in URLs or `.strm`.
- Never exchange or store Jellyfin API keys or Jellyfin user logins for peering; peers use
  plugin tokens that only open `/JellyShare/v1/*` federation endpoints.
- The plugin writes **only inside its two placeholder folders** (+ its own data dir). Never
  into the owner's or a peer's media folders. No copying of media files.
- A provider shares **only items backed by its own local files** — never placeholders it
  received (no re-sharing, no loops).
- No local user data (watch history, user names, sessions) is ever sent to peers.
- Scope v1: Movies and Shows (series/season/episode) only.
- One entry per title, matched by provider IDs (TMDB/IMDb for movies; series TVDB/TMDB +
  season + episode for episodes). Items without usable IDs are never merged or placeholdered.
- Local copy always wins; peer copy used only when no local copy exists. Fallback happens at
  library level within seconds–a minute after a local file disappears, never mid-playback.
- Peer base URLs may be HTTPS with a sub-path (e.g. `https://host/jellyfin`).
- No full library refreshes from the plugin; only targeted path scans, batched.
- Federation protocol is versioned (`"protocolVersion": "1.0"`); incompatible major → clear
  error in the admin UI.

## Review Focus

1. **Peer offline/timing out during sync** → its placeholders must NOT be deleted (keep last
   good catalog; delete only after the peer was unreachable for 7 consecutive days or removed by
   the admin). Test: `SyncPlanner_PeerUnreachable_KeepsItsPlaceholders` (Task 8).
2. **Titles without provider IDs / conflicting IDs** → skipped and counted as "unmatched", never
   placeholdered, never merged by name. Tests in Task 3.
3. **Unsafe file names** (`:`, `/`, `\`, `?`, `*`, `"`, `<`, `>`, `|`, trailing dot/space,
   Unicode, 250+ chars) → safe, stable, deterministic names. Tests in Task 4.
4. **Re-running sync with nothing changed** writes 0 files and triggers 0 scans (idempotent);
   10,000-item catalogs plan in < 2 s. Tests in Task 4.
5. **Loops / re-sharing**: a provider's catalog never contains items under its placeholder
   folders, even when two servers peer with each other. Test in Task 6 + e2e in Task 14.

---

## File Structure

```
jellyfin-plugin-jellyshare/
  Jellyfin.Plugin.JellyShare.sln
  build.yaml                              # plugin metadata (guid, targetAbi, category)
  manifest.json                           # plugin repository manifest (CI-updated)
  .github/workflows/release.yml           # build, test, zip, release, update manifest
  docs/specs/…design.md, docs/plans/…md   # copied spec + this plan
  docs/spike-results.md                   # Task 2 findings (decisions recorded here)
  src/Jellyfin.Plugin.JellyShare/
    Plugin.cs                             # BasePlugin<PluginConfiguration>, IHasWebPages
    PluginServiceRegistrator.cs           # DI registrations
    Configuration/PluginConfiguration.cs  # PeerShare (provider), RemotePeer (consumer), paths
    Configuration/configPage.html|.js     # admin UI (Task 11)
    Protocol/ProtocolDtos.cs              # CatalogResponse, CatalogItem, StreamInfo…
    Keys/MediaKey.cs                      # canonical keys from provider IDs
    Keys/MergeIndex.cs                    # union of peer catalogs → merged entries
    Placeholders/PathNaming.cs            # safe folder/file names
    Placeholders/NfoWriter.cs             # movie.nfo / tvshow.nfo / episode nfo XML
    Placeholders/PlaceholderPlanner.cs    # desired vs existing → PlaceholderDiff
    Placeholders/PlaceholderWriter.cs     # applies a diff to disk (+ images)
    Placeholders/PlaceholderPaths.cs      # IsPlaceholder(path) shared by provider/consumer
    Provider/ShareTokenService.cs         # create/hash/verify tokens
    Provider/CatalogBuilder.cs            # local items → CatalogItem list
    Provider/StreamLimiter.cs             # per-peer concurrent stream sessions
    Provider/FederationController.cs      # /JellyShare/v1/{ping,catalog,image,stream}
    Provider/CappedStreamService.cs       # bitrate-capped transcode (Task 12)
    Consumer/PeerClient.cs                # HTTP client for a RemotePeer
    Consumer/LocalIndex.cs                # keys of local (non-placeholder) items
    Consumer/PeerStateStore.cs            # last good catalog per peer + failure since (JSON)
    Consumer/SyncService.cs               # orchestrates a sync run
    Consumer/SyncTask.cs                  # IScheduledTask "JellyShare: sync peers"
    Consumer/SourceSelector.cs            # pick peer for a key (priority, health, limits)
    Consumer/RelayController.cs           # /JellyShare/v1/play/{key} (loopback only)
    Consumer/LocalItemWatcher.cs          # IHostedService on library item add/remove
  tests/Jellyfin.Plugin.JellyShare.Tests/ # xUnit, one file per class under test
  harness/docker-compose.yml              # alice + bob Jellyfin 12.4, shared ./media
  harness/make-media.sh                   # ffmpeg test clips (movies + show seasons)
  harness/e2e.py                          # end-to-end scenarios against the two servers
```

---

### Task 1: Repository, plugin skeleton, two-server harness

**Files:**
- Create: everything under "File Structure" that Task 1 needs: `.sln`, `build.yaml`,
  `src/.../Plugin.cs`, `PluginServiceRegistrator.cs`, `Configuration/PluginConfiguration.cs`
  (empty lists for now), `Provider/FederationController.cs` (ping only),
  `Protocol/ProtocolDtos.cs` (`ProtocolInfo.Version = "1.0"`), `tests/…Tests.csproj`,
  `harness/*`, `README.md`, `docs/specs/`, `docs/plans/`.

**Interfaces:**
- Produces: plugin GUID (generate once with `[guid]::NewGuid()`, record in `build.yaml` and
  `Plugin.cs`); route prefix `JellyShare/v1`; `GET /JellyShare/v1/ping` (anonymous) →
  `{"protocolVersion":"1.0","serverName":"<server name>"}`.
- Produces: harness URLs `http://localhost:8101` (alice) and `http://localhost:8102` (bob),
  each with admin `admin`/`admin`, libraries "Movies" and "Shows" over `./media/<server>/…`,
  and server **BaseUrl `/jellyfin`** on bob (to prove sub-path support).

- [ ] **Step 1:** `git init` the repo at `D:\Development\PERSONAL\jellyfin-plugin-jellyshare`;
  copy the spec and this plan into `docs/specs/` and `docs/plans/`; commit.
- [ ] **Step 2:** Check toolchain: `dotnet --list-sdks` shows a `10.` SDK (install .NET 10 SDK
  if not); `docker version` works (Docker Desktop locally, or run the harness on a homelab
  node).
- [ ] **Step 3:** Create the solution + class library (`net10.0`) referencing
  `Jellyfin.Controller` and `Jellyfin.Model` **12.0.x** (lowest 12 package on NuGet, with
  `<ExcludeAssets>runtime</ExcludeAssets>`), and the xUnit test project. Record in
  `docs/spike-results.md` the exact 12.x names of: `BasePlugin<T>`, `IHasWebPages`,
  `IPluginServiceRegistrator`, `IScheduledTask`, `ILibraryManager` item events,
  `ILibraryMonitor.ReportFileSystemChanged`, `IServerConfigurationManager` network/base-URL
  settings. (They existed in 10.x; confirm before relying on them in later tasks.)
- [ ] **Step 4:** Implement `Plugin` (name "JellyShare") and `FederationController.Ping`
  with `[AllowAnonymous]`.
- [ ] **Step 5:** Write `harness/make-media.sh`: ffmpeg `testsrc2` + sine, 2-minute 1080p
  H.264/AAC MKV clips named as real titles, e.g. alice: `Movies/Big Buck Bunny (2008)
  [tmdbid-10378]/…mkv`, `Movies/Sintel (2010) [tmdbid-45745]/…`, `Shows/Pioneer One
  [tvdbid-153021]/Season 01/Pioneer One S01E01.mkv`; bob: `Movies/Sintel (2010)
  [tmdbid-45745]/…` (duplicate), `Movies/Tears of Steel (2012) [tmdbid-133701]/…`,
  `Shows/Pioneer One [tvdbid-153021]/Season 01/S01E01, S01E02` and `Season 02/S02E01`.
  Also one movie without IDs: `Movies/Home Video/clip.mkv`.
- [ ] **Step 6:** `harness/docker-compose.yml`: two `jellyfin/jellyfin:12.4.0` services, each
  mounting its media dir, a config volume, and `../src/…/bin/Release/net10.0/` as
  `/config/plugins/JellyShare_0.0.0.1` (dev deploy). Add `harness/setup.py` that completes the
  startup wizard via API, creates the two libraries, sets bob's BaseUrl to `/jellyfin`, and
  restarts.
- [ ] **Step 7:** Verify: `dotnet build -c Release`, `docker compose up -d`, `python
  harness/setup.py`; then `curl http://localhost:8101/JellyShare/v1/ping` and
  `curl http://localhost:8102/jellyfin/JellyShare/v1/ping` both return
  `{"protocolVersion":"1.0",…}` without auth, and both dashboards list JellyShare as Active.
- [ ] **Step 8:** Commit `feat: plugin skeleton, ping endpoint, two-server harness`.

### Task 2: Spike — prove the risky assumptions (throwaway code, decisions kept)

No product code. Use the harness and hand-written files. Write every result (what was tried,
what Jellyfin did, decision) into `docs/spike-results.md`.

- [ ] **Step 1 — Series split across folders (spec risk 1):** in alice, add an extra library
  folder `placeholders/Shows` to the Shows library; create
  `Pioneer One [tvdbid-153021]/Season 02/Pioneer One S02E01.strm` (pointing to any reachable
  HTTP test file) + `tvshow.nfo` with the same TVDB id. Scan. **Pass** = Shows shows ONE
  "Pioneer One" with seasons 1 and 2. Also try: same folder name only; same ID only; with and
  without `tvshow.nfo`. Record which combination (if any) merges.
- [ ] **Step 2 — Gate:** if no combination merges, try Jellyfin 12 options found in the
  12.0 release notes (series grouping/merge settings) and the Merge Versions plugin. If still
  none, stop and report to the owner (spec: no writing into media folders; fallback = show
  appears twice). Continue only with the owner's answer.
- [ ] **Step 3 — `.strm` → HTTP with Range (risk 2):** serve a clip with
  `python -m http.server` (supports Range) from a URL reachable from inside the container;
  `.strm` contains that URL. Verify in alice: playback starts in web client, seeking to 90 s
  works, forced transcode (set user max bitrate 1 Mbps) works, ffprobe metadata shown. Note
  the request headers Jellyfin's ffmpeg sends (log them in the test server).
- [ ] **Step 4 — Clients never open `.strm` URLs themselves:** with the `.strm` URL pointing
  to `127.0.0.1`, play from the web client and one mobile/TV app (or the Jellyfin Android
  app) on another device. Pass = plays (server fetched it). If any client tries the URL
  directly, record which; the relay then needs signed URLs (spec "Calls made").
- [ ] **Step 5 — Missing-file behaviour (risk 3):** delete a movie file → note how long until
  the item disappears and which `ILibraryManager` event fires (`ItemRemoved`?). Then stop the
  container, make the media dir unavailable (bind mount to an empty dir), start → note whether
  items are removed or kept as missing. Record the hook to use in Task 10.
- [ ] **Step 6 — User data carry-over (best-effort goal):** mark Sintel watched + resume
  point on alice; delete the file; create a Sintel `.strm` placeholder with the same TMDB id;
  scan. Record whether watched/resume carry over automatically (if user data keys come from
  provider IDs it may be free). Decision: keep if free, otherwise skip in v1.
- [ ] **Step 7 — Anonymous plugin endpoint behind sub-path (risk 5):** already proven in
  Task 1 Step 7; additionally put bob behind a local nginx with HTTPS sub-path if easy, or
  record that the owner's NPM `/jellyfin` path will be tested in Task 14.
- [ ] **Step 8:** Commit `docs: spike results`. Adjust later tasks if any result contradicts
  them (e.g. different event name, signed URLs needed) and note the change at the top of this
  plan.

### Task 3: Media keys and merge index (pure)

**Files:** Create `Keys/MediaKey.cs`, `Keys/MergeIndex.cs`, `Protocol/ProtocolDtos.cs`
(CatalogItem); Test `tests/…/MergeIndexTests.cs`, `MediaKeyTests.cs`.

**Interfaces:**
- Produces:
  - `record CatalogItem(string Id, string Type /* "Movie"|"Episode" */, string Name, int? Year,
    Dictionary<string,string> ProviderIds, string? SeriesName,
    Dictionary<string,string>? SeriesProviderIds, int? SeasonNumber, int? EpisodeNumber,
    long? RunTimeTicks, string? Overview, MediaInfo Media, string[] ImageTypes,
    DateTime DateModified)`; `record MediaInfo(string? Container, string? VideoCodec, int? Width,
    int? Height, int? BitRate, AudioTrack[] Audio, SubtitleTrack[] Subtitles)`.
  - `static IReadOnlyList<string> MediaKey.For(CatalogItem item)` → all keys of an item:
    movies `movie:tmdb:{id}`, `movie:imdb:{id}`; episodes
    `ep:tvdb:{seriesId}:s{S}e{E}`, `ep:tmdb:{seriesId}:s{S}e{E}` (provider id keys lowercased,
    case-insensitive provider names `Tmdb`/`Imdb`/`Tvdb`). Empty list = unmatched.
  - `class MergeIndex { static MergeIndex Build(IEnumerable<(Guid PeerId, CatalogItem Item)> items);
    IReadOnlyList<MergedEntry> Entries; int UnmatchedCount; MergedEntry? Find(string key); }`
  - `record MergedEntry(string CanonicalKey, IReadOnlyList<string> Keys,
    IReadOnlyList<(Guid PeerId, CatalogItem Item)> Sources)`; `CanonicalKey` = tmdb key if any,
    else imdb/tvdb, chosen deterministically (ordinal string order within the preferred kind).

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] public void Movie_WithTmdbAndImdb_HasBothKeys()
  => MediaKey.For(Movie("Sintel", tmdb: "45745", imdb: "tt1727587"))
     .Should().BeEquivalentTo("movie:tmdb:45745", "movie:imdb:tt1727587");
[Fact] public void Item_WithoutIds_IsUnmatched()
  => MergeIndex.Build(new[] { (PeerA, Movie("Home Video")) }).UnmatchedCount.Should().Be(1);
[Fact] public void SameMovie_FromTwoPeers_OneEntryTwoSources() { /* tmdb 45745 on A and B → 1 entry, 2 sources */ }
[Fact] public void PeersSharingOnlyImdb_WithOneHavingTmdb_StillMerge() { /* A: tmdb+imdb, B: imdb only → 1 entry */ }
[Fact] public void Episode_KeyedBySeriesIdSeasonEpisode() { /* tvdb 153021 S02E01 → "ep:tvdb:153021:s2e1" */ }
[Fact] public void Episodes_SameSeriesDifferentEpisode_AreSeparate() { /* S01E01 vs S01E02 → 2 entries */ }
[Fact] public void SameName_DifferentIds_NotMerged() { /* "Dune" tmdb 438631 vs tmdb 841 → 2 entries */ }
[Fact] public void Episode_WithoutSeasonOrEpisodeNumber_IsUnmatched() { }
```

  (Assertions via plain xUnit `Assert` if FluentAssertions licensing is unwanted.)
- [ ] **Step 2:** Run `dotnet test --filter MergeIndex` → FAIL (types missing).
- [ ] **Step 3:** Implement. MergeIndex uses union-find over keys: every item's keys are
  unioned; each connected component is one MergedEntry. Sources ordered by peer priority later
  (SourceSelector), not here.
- [ ] **Step 4:** Run tests → PASS. **Step 5:** Commit `feat: media keys and merge index`.

### Task 4: Safe names, NFO, placeholder planning (pure)

**Files:** Create `Placeholders/PathNaming.cs`, `NfoWriter.cs`, `PlaceholderPlanner.cs`,
`PlaceholderPaths.cs`; Tests `PathNamingTests.cs`, `NfoWriterTests.cs`,
`PlaceholderPlannerTests.cs`.

**Interfaces:**
- Consumes: `MergedEntry`, `CatalogItem` (Task 3).
- Produces:
  - `static string PathNaming.Safe(string name)` — replaces `<>:"/\|?*` and control chars with
    space-collapsed `-`, trims trailing dots/spaces, max 120 UTF-16 chars, never empty
    (`"Untitled"`).
  - `static PlaceholderFile PathNaming.ForEntry(MergedEntry e)` →
    `record PlaceholderFile(string RelativeDir, string BaseName, string Kind /* Movie|Episode */)`:
    movie `"{Safe(Name)} ({Year}) [tmdbid-{id}]"` / base `"{Safe(Name)} ({Year})"` (imdb form
    `[imdbid-tt…]` when no tmdb; `(Year)` omitted when null); episode
    `"{Safe(SeriesName)} [tvdbid-{id}]/Season {S:00}"` / base
    `"{Safe(SeriesName)} S{S:00}E{E:00}"` (`tmdbid` when no tvdb). Naming input = the source
    with highest priority (first in `Sources`), so names are stable.
  - `static string NfoWriter.Movie(CatalogItem)`, `.Episode(CatalogItem)`,
    `.Series(CatalogItem anyEpisode)` — Kodi-style XML: title, year, plot, `<uniqueid
    type="tmdb" default="true">`, runtime (minutes), and `<fileinfo><streamdetails>` from
    `MediaInfo`.
  - `static string PlaceholderPaths.StrmContent(string relayBase, string canonicalKey)` →
    `"{relayBase}/JellyShare/v1/play/{Uri.EscapeDataString(canonicalKey)}"` (no secrets).
  - `static bool PlaceholderPaths.IsPlaceholder(string? path, PluginConfiguration cfg)`.
  - `PlaceholderDiff PlaceholderPlanner.Plan(IEnumerable<MergedEntry> wanted,
    ISet<string> localKeys, IReadOnlyDictionary<string,string> existing /* relPath → content hash */,
    string relayBase)` → `record PlaceholderDiff(IReadOnlyList<FileWrite> Writes,
    IReadOnlyList<string> Deletes, IReadOnlyList<string> TouchedDirs)`;
    `record FileWrite(string RelativePath, string Content)`. An entry is wanted only if none of
    its `Keys` is in `localKeys`. Writes include `.strm`, `.nfo`, `tvshow.nfo` (once per
    series dir); only files whose content hash differs are written; files in `existing` not in
    the wanted set are deleted (images handled by the writer, Task 8).

- [ ] **Step 1: Failing tests:**

```csharp
[Theory]
[InlineData("Mission: Impossible", "Mission- Impossible")]
[InlineData("What If...?", "What If")]
[InlineData("AC/DC: Live", "AC-DC- Live")]
[InlineData("  .  ", "Untitled")]
public void Safe_ReplacesIllegalChars(string input, string expected) => Assert.Equal(expected, PathNaming.Safe(input));
[Fact] public void Safe_TruncatesTo120() { }
[Fact] public void Movie_Path_UsesTmdbFolderTag() { /* "Sintel (2010) [tmdbid-45745]" */ }
[Fact] public void Episode_Path_UsesSeasonFolder() { /* "Pioneer One [tvdbid-153021]/Season 02", "Pioneer One S02E01" */ }
[Fact] public void Strm_ContainsNoTokenAndEscapesKey() { /* content == "http://127.0.0.1:8096/jellyfin/JellyShare/v1/play/movie%3Atmdb%3A45745" */ }
[Fact] public void Plan_SkipsEntriesPresentLocally() { /* local has movie:imdb:tt1727587 → no write for Sintel */ }
[Fact] public void Plan_SecondRunWithSameInput_WritesNothing() { }
[Fact] public void Plan_DeletesPlaceholdersNoLongerOffered() { }
[Fact] public void Plan_10000Entries_Under2Seconds() { }
[Fact] public void Nfo_Movie_HasDefaultTmdbUniqueIdAndStreamDetails() { }
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: placeholder naming, nfo and planner`.

### Task 5: Configuration model and share tokens

**Files:** Modify `Configuration/PluginConfiguration.cs`; Create `Provider/ShareTokenService.cs`;
Test `ShareTokenServiceTests.cs`.

**Interfaces:**
- Produces:
  - `class PeerShare { Guid Id; string Name; string TokenHash; bool Enabled = true;
    List<Guid> LibraryIds; int MaxStreams = 2; int? MaxBitrate /* bps, null = no cap */; }`
  - `class RemotePeer { Guid Id; string Name; string BaseUrl; string Token; bool Enabled = true;
    int Priority /* lower = preferred */; }`
  - `PluginConfiguration { List<PeerShare> Shares; List<RemotePeer> Peers; string
    MoviesPlaceholderPath; string ShowsPlaceholderPath /* defaults: <plugin data>/placeholders/{Movies,Shows} */;
    int SyncIntervalHours = 6; string? RelayBaseOverride; }`
  - `ShareTokenService`: `(string Token, string Hash) Create()` — token
    `"js1_" + base64url(32 random bytes)`, hash = lowercase hex SHA-256 of the token;
    `PeerShare? Verify(string? presentedToken, IEnumerable<PeerShare> shares)` — constant-time
    compare (`CryptographicOperations.FixedTimeEquals`), ignores disabled shares.

- [ ] **Step 1: Failing tests:** `Create_TokenHasPrefixAndHashMatches`,
  `Verify_CorrectToken_ReturnsShare`, `Verify_WrongToken_ReturnsNull`,
  `Verify_DisabledShare_ReturnsNull`, `Verify_NullOrEmpty_ReturnsNull`,
  `Config_RoundTripsThroughXmlSerializer` (Jellyfin stores plugin config as XML: all fields
  survive serialize/deserialize; tokens of RemotePeer included, TokenHash only for shares).
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** Commit `feat: config model and share tokens`.

### Task 6: Provider — catalog and image endpoints

**Files:** Create `Provider/CatalogBuilder.cs`; Modify `Provider/FederationController.cs`;
Test `CatalogBuilderTests.cs` (builder takes an abstraction, see below) + harness check.

**Interfaces:**
- Consumes: `ShareTokenService.Verify`, `PlaceholderPaths.IsPlaceholder`, `CatalogItem`.
- Produces:
  - `interface ILocalItemSource { IEnumerable<LocalItem> GetItems(IReadOnlyCollection<Guid> libraryIds); }`
    with `record LocalItem(Guid Id, string Type, string? Path, CatalogItem Item)`; real
    implementation `JellyfinLocalItemSource` (ILibraryManager query for Movie + Episode under
    the given libraries, recursive, `IsVirtualItem=false`), fake in tests.
  - `CatalogBuilder.Build(PeerShare share) → CatalogResponse(string ProtocolVersion,
    string ServerName, string Etag, IReadOnlyList<CatalogItem> Items)`; excludes items whose
    path `IsPlaceholder` or ends with `.strm`; Etag = SHA-256 over sorted item ids +
    DateModified.
  - Endpoints (all `[AllowAnonymous]`, token in header **`X-JellyShare-Token`**, 401 on
    missing/invalid, never echo the token):
    `GET /JellyShare/v1/catalog` (supports `If-None-Match` → 304),
    `GET /JellyShare/v1/image/{itemId}/{imageType}` (Primary|Backdrop|Logo|Thumb; 404 if item
    not in the share's catalog).

- [ ] **Step 1: Failing tests:** `Build_ExcludesPlaceholderPaths`, `Build_ExcludesStrmFiles`,
  `Build_OnlySharedLibraries`, `Build_EtagStableForSameInput`, `Build_EtagChangesWhenItemModified`.
- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5: Harness check:** add a share for "bob" on alice via config file or API
  (`POST /Plugins/{id}/Configuration` with admin auth header); `curl -H "X-JellyShare-Token:
  <token>" http://localhost:8101/JellyShare/v1/catalog` returns alice's items incl. IDs and
  media info (the "Home Video" item is listed with empty ProviderIds; the consumer counts it
  as unmatched); without header → 401; poster image downloads.
- [ ] **Step 6:** Commit `feat: provider catalog and image endpoints`.

### Task 7: Provider — stream endpoint and per-peer limits

**Files:** Create `Provider/StreamLimiter.cs`; Modify `FederationController.cs`; Test
`StreamLimiterTests.cs`.

**Interfaces:**
- Produces:
  - `StreamLimiter(TimeProvider clock)`: `bool TryAcquire(Guid shareId, Guid itemId, int maxStreams)`;
    a *session* = (shareId, itemId); every request for an active session refreshes it and is
    always allowed; a new session is allowed only if active sessions for that share <
    maxStreams; sessions expire **30 s** after their last request. `int Active(Guid shareId)`.
  - `GET /JellyShare/v1/stream/{itemId}` with token header: 401/404 as above; 429 with
    `Retry-After: 30` when the limit is reached; otherwise serves the local file with Range
    support (`PhysicalFile(path, contentType, enableRangeProcessing: true)`). If the share has
    `MaxBitrate` and the item's bitrate exceeds it → delegate to `CappedStreamService` (Task
    12; until then return 409 "over cap" so consumers try another peer).
  - Usage log line per new session: share name, item name, bytes are not tracked in v1.

- [ ] **Step 1: Failing tests:** `SecondItem_OverLimit_Rejected`,
  `SameItemRangeRequests_DoNotConsumeExtraSlots`, `Session_ExpiresAfter30sIdle` (fake
  `TimeProvider`), `DifferentShares_HaveIndependentLimits`.
- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5: Harness check:** `curl -r 0-1023 -H "X-JellyShare-Token: …"
  …/stream/<id>` → 206 with 1024 bytes; with MaxStreams=1, a second item within 30 s → 429.
- [ ] **Step 6:** Commit `feat: provider stream endpoint with per-peer limits`.

### Task 8: Consumer — peer client, local index, sync, placeholder writer

**Files:** Create `Consumer/PeerClient.cs`, `LocalIndex.cs`, `PeerStateStore.cs`,
`SyncService.cs`, `SyncTask.cs`, `Placeholders/PlaceholderWriter.cs`; Tests
`SyncServiceTests.cs`, `PlaceholderWriterTests.cs` (temp dir).

**Interfaces:**
- Consumes: Tasks 3–6.
- Produces:
  - `PeerClient`: `Task<PeerFetchResult> FetchCatalogAsync(RemotePeer peer, string? etag, CancellationToken ct)`
    → `record PeerFetchResult(bool Ok, bool NotModified, CatalogResponse? Catalog, string? Error)`
    (timeout 30 s; rejects `protocolVersion` major ≠ 1 with Error "incompatible protocol x");
    `Task<Stream?> GetImageAsync(RemotePeer, string itemId, string type, CancellationToken)`;
    `Task<HttpResponseMessage> OpenStreamAsync(RemotePeer, string itemId, string? range, CancellationToken)`.
    Base URL joined safely with sub-paths (`https://h/jellyfin` + `/JellyShare/v1/...`).
  - `PeerStateStore` (JSON in plugin data dir): per peer `LastGoodCatalog`, `Etag`,
    `LastSuccessUtc`, `FailingSinceUtc`, `LastError`.
  - `LocalIndex.GetLocalKeys(MediaType)` → `ISet<string>` of `MediaKey.For` over local
    non-placeholder items in **all** libraries of that type.
  - `SyncService.RunAsync(CancellationToken) → SyncReport(int Offered, int Unmatched,
    int SkippedLocal, int Written, int Deleted, IReadOnlyList<string> PeerErrors)`:
    fetch each enabled peer → on failure use `LastGoodCatalog` unless `FailingSinceUtc` is
    older than **7 days** (then the peer contributes nothing) → MergeIndex over all catalogs →
    Planner per type → Writer applies diff → `ILibraryMonitor.ReportFileSystemChanged` (or the
    hook recorded in Task 2) once per touched directory, batched max 200 per run then the rest
    next run → persists merged index to `merged-index.json` (used by Tasks 9–10).
  - `PlaceholderWriter.Apply(PlaceholderDiff diff, string root, Func<…> imageFetcher)`:
    writes atomically (temp + move), creates dirs, deletes files and empty dirs, downloads
    `poster.jpg`/`fanart.jpg` once per movie dir / series dir if missing; never touches paths
    outside `root` (guard against `..`).
  - `SyncTask : IScheduledTask` "JellyShare: sync peers", default trigger every
    `SyncIntervalHours`, plus run-on-demand.

- [ ] **Step 1: Failing tests:** `SyncPlanner_PeerUnreachable_KeepsItsPlaceholders`,
  `SyncPlanner_PeerUnreachable8Days_RemovesItsPlaceholders`,
  `Sync_IncompatibleProtocol_ReportsErrorAndKeepsLastGood`,
  `Writer_RefusesPathsOutsideRoot`, `Writer_IsAtomic_NoPartialStrm`,
  `Writer_RemovesEmptyDirectories`, `Sync_LocalCopyPresent_NoPlaceholder`,
  `Sync_SecondRun_NoWritesNoScans` (fakes for PeerClient/LocalIndex/library monitor).
- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5: Harness check:** on bob add alice as RemotePeer (token from Task 6), add bob's
  placeholder folders to bob's Movies/Shows libraries, run the task. Expect: Big Buck Bunny
  placeholder written; **Sintel not** (bob has it); Pioneer One S01E01 not, but nothing for
  S02 either (alice has only S01E01 — bob has it). Swap: on alice add bob as peer → Tears of
  Steel, Pioneer One S01E02 + S02E01 appear; Sintel not duplicated; series shows ONE entry
  (per Task 2 result). Second run: report `Written=0, Deleted=0`.
- [ ] **Step 6:** Commit `feat: consumer sync and placeholders`.

### Task 9: Consumer — relay playback

**Files:** Create `Consumer/SourceSelector.cs`, `Consumer/RelayController.cs`; Test
`SourceSelectorTests.cs`.

**Interfaces:**
- Consumes: `merged-index.json`, `PeerClient.OpenStreamAsync`, `PeerStateStore`.
- Produces:
  - `SourceSelector.Order(MergedEntry entry, IReadOnlyList<RemotePeer> peers, PeerStateStore state)`
    → sources ordered by: peer enabled, not currently failing, `Priority` ascending, then peer
    name.
  - `GET /JellyShare/v1/play/{key}` `[AllowAnonymous]` but **403 unless
    `RemoteIpAddress` is loopback** (or matches the Task 2 decision if signed URLs were
    required). Tries sources in order: forwards `Range`; on 429/409/5xx/timeout → next source
    (only before any byte was sent); copies status (200/206), `Content-Type`,
    `Content-Length`, `Content-Range`, `Accept-Ranges`; streams the body with
    `HttpCompletionOption.ResponseHeadersRead`. All sources failed → 503.
  - `RelayBase` for `.strm`: `RelayBaseOverride` or `http://127.0.0.1:{internal HTTP port}{BaseUrl}`
    from server network config.

- [ ] **Step 1: Failing tests:** `Order_PrefersLowerPriority`, `Order_SkipsDisabledAndFailing`,
  `Order_TieBreaksByName`.
- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5: Harness check:** on bob play Big Buck Bunny in the web client: starts, seek to
  90 s works, forced transcode at 1 Mbps works; `curl` to `/JellyShare/v1/play/...` from the
  host (non-loopback) → 403. Set alice MaxStreams=1 and play two alice titles on bob → second
  fails cleanly (or uses another peer if one has it).
- [ ] **Step 6:** Commit `feat: relay playback with peer failover at start`.

### Task 10: Local file disappears / reappears

**Files:** Create `Consumer/LocalItemWatcher.cs`; Test `LocalItemWatcherTests.cs`.

**Interfaces:**
- Consumes: the item-removed/added hook recorded in Task 2; `merged-index.json`;
  `PathNaming`, `PlaceholderWriter`.
- Produces: `IHostedService` that queues events and every **15 s** processes the batch:
  local Movie/Episode removed and its key exists in the merged index → write that entry's
  placeholder + scan its dir; local Movie/Episode added (non-placeholder) → delete the
  placeholder for any of its keys + scan. Batches over 500 events in one window are handed to
  a full `SyncService.RunAsync` instead (drive-offline storm).

- [ ] **Step 1: Failing tests:** `LocalRemoved_WithPeerCopy_WritesPlaceholder`,
  `LocalRemoved_NoPeerCopy_DoesNothing`, `LocalAdded_RemovesPlaceholder`,
  `PlaceholderEvents_AreIgnored` (no feedback loop), `Storm_Over500_DelegatesToFullSync`.
- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5: Harness check:** on bob delete its Sintel file → within ~1 min Sintel is back
  (from alice, plays); restore the file → placeholder removed, one Sintel entry, local plays.
  Record watched-status behaviour against the Task 2 finding.
- [ ] **Step 6:** Commit `feat: fallback to peer copy when local file disappears`.

### Task 11: Admin page

**Files:** Create `Configuration/configPage.html`, `configPage.js`; Modify `Plugin.cs`
(register pages); add admin-only endpoints in a new `Provider/AdminController.cs`
(`[Authorize(Policy = "RequiresElevation")]` or the 12.x equivalent recorded in Task 1).

**Interfaces:**
- Produces admin endpoints: `POST /JellyShare/v1/admin/shares` (name, libraries, limits) →
  returns the token **once**; `DELETE /JellyShare/v1/admin/shares/{id}`;
  `POST /JellyShare/v1/admin/peers/test` (url, token) → ping + catalog count or error;
  `GET /JellyShare/v1/admin/status` → per-peer last sync, counts, errors, active streams.

- [ ] **Step 1:** Page with two tabs as in the spec ("Share with peers": invitations,
  libraries checkboxes, max streams, max bitrate Mbps, revoke, active streams; "Receive from
  peers": add peer URL + token + Test, priority up/down, enable, last sync report, "Sync now";
  setup hint to add the two placeholder folders to the Movies/Shows libraries, showing their
  paths).
- [ ] **Step 2: Verify in harness with playwright-cli (or by hand):** create share on alice →
  token shown once; paste on bob → Test shows "ok, N titles"; Sync now → report numbers match
  Task 8; incompatible/garbage URL → readable error.
- [ ] **Step 3:** Commit `feat: admin page`.

### Task 12: Quality cap (provider-side)

**Files:** Create `Provider/CappedStreamService.cs`; Modify `FederationController.cs`.

**Interfaces:**
- Produces: when `share.MaxBitrate` is set and source bitrate is higher, the stream endpoint
  serves a live ffmpeg transcode (Jellyfin's configured ffmpeg path and hardware accel
  settings if accessible, else software x264) to MPEG-TS at `MaxBitrate` (video = cap − 192
  kbps audio, AAC stereo); optional `?start=<seconds>` query → `-ss` before input. No Range
  support for capped streams (documented limitation: seeking may restart the stream); counts
  against StreamLimiter like any stream; ffmpeg process killed when the client disconnects.

- [ ] **Step 1: Harness check first (this is integration-heavy):** set alice share
  MaxBitrate = 2 Mbps; play a high-bitrate alice title on bob → plays; alice's `docker stats`
  shows ffmpeg; output bitrate ≈ cap (ffprobe the stream); closing playback kills ffmpeg
  within 10 s.
- [ ] **Step 2:** Implement until the check passes. Record seeking behaviour in README.
- [ ] **Step 3:** Commit `feat: provider bitrate cap`. (If this proves too unreliable, ship
  v1 with "over cap → 409, consumer tries another peer" and note it; the owner agreed limits
  are wanted, streams-at-once is the essential one.)

### Task 13: Packaging and plugin repository

**Files:** Modify `build.yaml`; Create `.github/workflows/release.yml`, `manifest.json`,
`README.md` (install, pairing walkthrough, limits, known limitations, security notes).

- [ ] **Step 1:** Workflow on tag `v*`: `dotnet test`, `dotnet publish -c Release`, zip the
  DLL(s) as `jellyshare_<version>.zip`, compute MD5, create GitHub Release, update
  `manifest.json` (Jellyfin plugin repository format: guid, name, versions[] with
  `version`, `targetAbi: "12.0.0.0"`, `sourceUrl`, `checksum`, `timestamp`, `changelog`) on
  `main`.
- [ ] **Step 2:** Tag `v0.1.0.0`; verify: add
  `https://raw.githubusercontent.com/<owner>/jellyfin-plugin-jellyshare/main/manifest.json` as
  a repository on harness alice, install from Catalog, restart → Active.
- [ ] **Step 3:** Commit `chore: release pipeline and plugin repository`.

### Task 14: End-to-end suite and real-world rollout

**Files:** Create `harness/e2e.py`.

- [ ] **Step 1:** `e2e.py` (Python stdlib only) automates against the harness: pairing both
  ways, sync, asserts (no duplicates by TMDB id in either Movies library; Pioneer One one
  series with expected episodes; Home Video never shared; no item on either server whose
  path is under the other's placeholder dir appears in any catalog — loop check), play via
  `/Videos/{id}/stream?static=true` with header auth and ffprobe the first 5 s, limit 429,
  delete/restore local file fallback. Run: `python harness/e2e.py` → `ALL PASS`.
- [ ] **Step 2: Owner's server (HomeLab):** install from the plugin repository on
  `jellyfin_app` (12.4.0, behind NPM `https://hub-ofatech.ddns.net/jellyfin/`); add the
  placeholder folders as paths the container can write (e.g. under `/config` or a new
  bind-mount — record in HomeLab `docs/apps/jellyfin/README.md`); pair with the friend's
  server; check through NPM: peer catalog reachable over HTTPS sub-path, a friend-only title
  plays remotely (outside LAN, phone on 4G), a shared title is not duplicated, Jellyfin
  logins stay fast during sync (DB lock regression check).
- [ ] **Step 3:** Commit `test: e2e suite`; tag release; write the README "Known
  limitations" from real findings.

---

## Self-review notes

- Spec coverage: many peers (5, 8), merge/dedupe (3, 8), local first (4, 8, 10), per-episode
  (3, 4, Task 2 gate), provider limits (5, 7, 12), stock clients (2, 9), stable extension
  points (Task 1 Step 3 records the 12.x API names), no admin keys / no media-folder writes /
  no re-share (Global Constraints, 5, 6, 8), versioned protocol (8, 11), plugin repository
  (13), user data best-effort (2, 10).
- Risky items are front-loaded in Task 2; later tasks must be adjusted to its results.
