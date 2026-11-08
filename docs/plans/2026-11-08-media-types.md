# Media Types Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Share and receive by media type (default + per invitation on the share side, per peer on the receive side), and add Music and Audiobooks/Podcasts end to end.

**Architecture:** A `MediaType` enum and a `MediaTypes` helper in Domain drive every filter: the provider's catalog (effective share types ∩ requested types), the receiver's request and local filter (accept list), and the admin page. Music and audiobooks reuse the movie pipeline — catalog item → merge key → `.strm` placeholder in a per-type placeholder folder → relay — with the folder layout and `.nfo` files the Task 1 spike proves on Jellyfin 12.4.

**Tech Stack:** C# / .NET 10, Jellyfin 12.0.0 packages (server 12.4), xUnit + Shouldly, Python/Playwright harness with two Jellyfin 12.4 containers.

**Spec:** `docs/specs/2026-11-08-media-types-design.md` (read it first; its "Calls made after the builder check" section is binding).

## Amendment after Task 1 (2026-11-08, owner: "go with your recommendation")

Task 1 stopped: on 12.4 an audio `.strm` gets no artist/album (tags are read from the `.strm` file itself; no track `.nfo`) and never plays (Jellyfin follows a `.strm` URL only for video). Writing track metadata through Jellyfin's item API after a scan **does** group correctly (friend's track joins the owner's artist; a missing album shows as one album in order). Partial albums can't merge.

New order:
1. Tasks 2–5 as written (types, settings, share/receive filters, admin page) with `MediaTypes.Supported` = Movies, Shows; Music/Audiobooks show "coming later".
2. **Task 5b — playback spike for approach A′**: from plugin code, make an audio placeholder item play through the relay (e.g. an `IMediaSourceProvider` that offers an Http/IsRemote source pointing at the relay, plus the PlaybackInfo filter dropping the `.strm` source, plus an action filter on `Audio/{id}/stream|universal` for clients that skip PlaybackInfo). Prove: web client plays a music `.strm` and an audiobook `.strm`; `Audio/{id}/universal` and `/stream?static=true` return audio bytes (ffprobe); no token/key in any URL or file. Record in `docs/spike-results.md`.
3. If 5b works: Tasks 6–10 with A′ — Task 8 also writes track metadata (artist, album artist, album, track/disc number) to the placeholder items after each scan, and writes only albums the owner lacks entirely. If 5b fails: stop Music/Audiobooks for this release (README says "coming later") and report; channels (B) become a separate project.

## Global Constraints

- Only `JellyShare.Jellyfin` references Jellyfin/MediaBrowser/ASP.NET; interfaces and enums live in Domain; handlers/requests follow the existing architecture tests.
- Warnings are errors; tests use Shouldly; `dotnet test JellyShare.slnx -c Release` must stay green with 0 warnings.
- Tokens and passwords never in URLs, `.strm` files, logs, error strings or admin responses.
- The plugin writes only inside its own placeholder folders.
- Settings saved by 0.2.0.0 must load with **no change** in what is shared or received.
- A catalog request **without** a type list gets only Movies and Shows.
- Audio is never sent through the capped video transcode; for stream limits an audio title is the album (audiobooks: the book).
- Media type names everywhere (API, query, page): `Movies, Shows, Music, Audiobooks, Ebooks, Collections, LiveTv`; page labels "Movies", "Shows", "Music", "Audiobooks & podcasts", "Ebooks", "Collections", "Live TV"; unsupported ones say "coming later".
- Harness: alice `http://127.0.0.1:8101`, bob `http://127.0.0.1:8102/jellyfin`, admin/admin; deploy a build by `docker cp` of all 6 DLLs into `/config/plugins/JellyShare_0.0.0.1/` of both containers + `docker restart` (a plain restart can keep stale code); use 127.0.0.1, not localhost.

## Review Focus

1. An older receiver (no `types` value) asking a new provider whose share includes Music gets no music items → Task 3 test `Catalog_WithoutTypes_ReturnsOnlyMoviesAndShows`.
2. A 0.2.0.0 settings file (shares with `LibraryIds`, peers without accept elements) loads as Custom-by-library-type shares and accept-everything peers → Task 2 tests.
3. Unticking Music on one peer removes only that peer's music placeholders; its movies and another peer's music stay → Task 4 test `Sync_PeerStopsAcceptingType_RemovesOnlyThatTypeFromThatPeer`.
4. Playing an album track after track counts as one stream on the provider and on the receiver → Task 6 `Stream_AudioTracksOfOneAlbum_ShareOneLease`, Task 7 `TitleKey_TracksOfOneAlbum_AreOneTitle`.
5. A friend's track the owner already has (same MusicBrainz id, or same artist/album/title with different case and punctuation) is not written; an artist name with `/` (AC/DC) gives a safe folder → Task 8 tests.

---

### Task 1: Spike — music and audiobook link files on Jellyfin 12.4 (STOP POINT)

**Files:**
- Modify: `harness/make-media.sh` (audio clips), `harness/setup.py` (Music `music` and Audiobooks `books` libraries on both servers), `harness/e2e.py` `MEDIA_FILES`
- Create: `harness/spike/music_strm.py`
- Modify: `docs/spike-results.md` (new section "Media types spike (2026-11)")

- [ ] **Step 1:** Add audio media: with the image's ffmpeg (`lavfi sine`, 20 s, mp3 and flac, ID3/Vorbis tags `artist`, `album_artist`, `album`, `title`, `track`, `disc`, and a MusicBrainz recording id tag on one track). alice `/media/Music/Artist A/Album One/01 …`, `02 …`; bob `/media/Music/Artist A/Album One/03 …` (album alice partly has), `/media/Music/Artist B/Album Two/01-02 …` (album alice lacks), `/media/Audiobooks/Author X/Book Y/01 …, 02 …`. Add the two libraries in `setup.py`.
- [ ] **Step 2:** In `harness/spike/music_strm.py`, by hand-written files in a scratch folder added as an extra path of alice's Music and Audiobooks libraries, test layouts: `Artist/Album/NN Title.strm` alone; plus `album.nfo` (`<album>` with title, artist, albumartist, musicbrainz ids) and `artist.nfo`; and a per-track `.nfo` if Jellyfin reads one. `.strm` points at a URL bob serves (bob's `/JellyShare/v1/...` is not ready yet: point at a plain HTTP file server container or Jellyfin's own `Audio/{id}/stream?static=true&api_key` **only inside the scratch spike, never committed with a key**).
- [ ] **Step 3:** For each layout, scan and record via the API: artists, albums, tracks (names, `IndexNumber`, `ParentIndexNumber`, `AlbumArtist`), whether bob's "Artist A" track merges into alice's existing Artist A / Album One, whether Album Two is one album under Artist B, whether an audio `.strm` plays (`PlaybackInfo` + stream returns audio bytes, ffprobe shows the codec), and the same for Audiobooks (book grouping, chapter order).
- [ ] **Step 4:** Write the results table + the chosen layout and `.nfo` contents into `docs/spike-results.md`. Decision rule (spec call 1): if Music or Audiobooks can't show proper artists/albums/books or can't play → **STOP and report to the controller** (owner decides on approach B). If only partial-album merge fails → record "write only albums the owner lacks entirely" and continue.
- [ ] **Step 5:** Commit `test: media types spike (music and audiobook link files)`.

### Task 2: Media types and settings

**Files:**
- Create: `Source/Domain/Enums/MediaType.cs`, `Source/Domain/Models/Configuration/MediaTypes.cs`
- Modify: `Source/Domain/Models/Configuration/JellyShareSettings.cs`, `PeerShare.cs`, `RemotePeer.cs`; `Source/Infrastructure/Platform/Jellyfin/Configuration/PluginConfiguration.cs`; `Source/Application/Features/Shares/UpdateShare.cs` (copy the new fields)
- Test: `Tests/UnitTests/Configuration/MediaTypesTests.cs`, `Tests/UnitTests/Peers/RemotePeerTests.cs`, `Tests/JellyfinTests/Configuration/PluginConfigurationTests.cs`, `PluginConfigurationXmlTests.cs`

**Interfaces — Produces:**
- `enum MediaType { Movies, Shows, Music, Audiobooks, Ebooks, Collections, LiveTv }`
- Settings: `JellyShareSettings.DefaultShareTypes : List<MediaType>?` (null = all, including future types; no initializer — `XmlSerializer` appends to initialized lists); `MusicPlaceholderPath`, `AudiobooksPlaceholderPath : string` (`ResolvePlaceholderRoots` fills `<data>/placeholders/Music|Audiobooks`); `PeerShare.UseDefaultTypes : bool?` (null = saved by 0.2.0.0), `PeerShare.Types : List<MediaType>`; `RemotePeer.AcceptAllTypes : bool = true`, `RemotePeer.AcceptTypes : List<MediaType>`.
- `static class MediaTypes`:
  - `IReadOnlyList<MediaType> All` (7, enum order); `IReadOnlyList<MediaType> Supported` = Movies, Shows (Task 9 adds Music, Audiobooks).
  - `MediaType? Of(string catalogItemType)`: `Movie`→Movies, `Episode`→Shows, `Audio`→Music, `AudioBook`→Audiobooks, else null.
  - `IReadOnlyList<MediaType> FromLibrary(string? collectionType)`: `movies`→Movies, `tvshows`→Shows, `music`→Music, `books`→Audiobooks, anything else/null→Movies+Shows.
  - `IReadOnlySet<MediaType> DefaultShare(JellyShareSettings cfg)` = `DefaultShareTypes ?? All`.
  - `IReadOnlySet<MediaType> EffectiveShare(PeerShare share, JellyShareSettings cfg, IReadOnlyList<LibraryInfo> libraries)`: `UseDefaultTypes == true` → default; `false` → `Types`; `null` → union of `FromLibrary` over the share's `LibraryIds` found in `libraries` (unknown ids ignored).
  - `IReadOnlySet<MediaType> Accepted(RemotePeer peer)` = `AcceptAllTypes ? All : AcceptTypes`.
  - `string Format(IEnumerable<MediaType>)` → `"Movies,Shows"` (enum order, distinct); `IReadOnlySet<MediaType> ParseOrLegacy(string? value)`: null/empty → {Movies, Shows}; unknown names ignored.

- [ ] **Step 1: Failing tests** in `MediaTypesTests`: `Of_MapsCatalogTypes`, `FromLibrary_MapsCollectionTypes` (incl. `null`, `"mixed"` → Movies+Shows, `"books"` → Audiobooks only), `EffectiveShare_LegacyShare_UsesLibraryTypes` (share with Movies+Shows library ids, `UseDefaultTypes = null` → {Movies, Shows}), `EffectiveShare_UseDefault_FollowsDefault`, `DefaultShare_Null_IsAll`, `Accepted_All_And_Custom`, `ParseOrLegacy_Absent_IsMoviesAndShows`, `Format_RoundTripsThroughParse`. XML tests: `ShareXmlFrom020_LoadsWithUseDefaultTypesNull_AndLibraryIdsKept`, `PeerXmlFrom020_LoadsAcceptingAllTypes`, `DefaultShareTypesAbsent_LoadsNull`, `DefaultShareTypesSaved_RoundTripsWithoutDuplicates`. Extend the parity/round-trip helper `NonDefault` for `bool`, `bool?`, `List<MediaType>`, `List<MediaType>?`; make the `RemotePeer_Copy_CopiesEveryProperty` fixture set the new props.
- [ ] **Step 2:** Run `dotnet test JellyShare.slnx -c Release --filter "FullyQualifiedName~MediaTypes|FullyQualifiedName~PluginConfiguration|FullyQualifiedName~RemotePeer"` → FAIL (types missing).
- [ ] **Step 3:** Implement the enum, settings props (both `JellyShareSettings` and `PluginConfiguration`, same order/types, `ToSettings`/`Apply`), `MediaTypes`, and the new fields in `UpdateShare.cs`'s copy. `RemotePeer.Copy()` stays shallow: code that changes `AcceptTypes` must assign a new list.
- [ ] **Step 4:** Run the full suite → PASS, 0 warnings.
- [ ] **Step 5:** Commit `feat: media types and their settings`.

### Task 3: Share side — catalog by type, default sharing, invitation types

**Files:**
- Modify: `Source/Domain/Models/Requests/Federation/GetCatalogQuery.cs` (add `string? Types`), `Source/Application/Features/Federation/GetCatalog.cs`, `Source/Application/Features/Catalog/CatalogBuilder.cs`, `Source/Domain/Interfaces/Library/ILocalItemSource.cs`, `Source/Infrastructure/Platform/Jellyfin/Adapters/JellyfinLocalItemSource.cs`, `Source/Infrastructure/Platform/Jellyfin/Controllers/V1/FederationController.cs` (read `types` query), admin share DTOs/commands/handlers (`AdminDtos.cs`, `CreateShareCommand`, `UpdateShareCommand`, `CreateShare.cs`, `UpdateShare.cs`, `AdminRules.ValidateShare`/`ToView`), `GetStatus.cs`, `AdminController.cs`
- Create: `Source/Domain/Models/Requests/Shares/UpdateDefaultSharingCommand.cs`, `Source/Application/Features/Shares/UpdateDefaultSharing.cs`
- Test: `Tests/UnitTests/Catalog/CatalogBuilderTests.cs`, `Tests/UnitTests/Federation/FederationHandlerTests.cs`, `Tests/UnitTests/Shares/ShareHandlerTests.cs`, `Tests/JellyfinTests/Controllers/FederationControllerCatalogTests.cs`, `AdminControllerTests.cs`

**Interfaces:**
- Consumes: Task 2 `MediaTypes`, settings.
- Produces:
  - `ILocalItemSource.GetItems(IReadOnlySet<MediaType> types)` and `GetExposureCandidates(Guid itemId, IReadOnlySet<MediaType> types)` replace the library-id overloads (all libraries; the type decides). Jellyfin adapter maps types → item kinds (Movies→Movie, Shows→Episode; Music/Audiobooks added in Task 6).
  - `CatalogBuilder.Plan(PeerShare share, IReadOnlySet<MediaType> requested)`, `Build(share, requested)`, `IsExposed(PeerShare share, Guid itemId)` (uses all effective share types). Served types = `EffectiveShare ∩ requested ∩ ProviderSupported`, where `ProviderSupported` = Movies, Shows (Task 6 adds Music, Audiobooks). ETag input and cache key include `MediaTypes.Format(served)`.
  - `GET JellyShare/v1/catalog?types=Movies,Shows,Music` (absent → legacy {Movies, Shows}).
  - Admin: `PUT JellyShare/v1/admin/sharing/default` body `DefaultSharingRequest(bool? All, List<MediaType>? Types)` → `UpdateDefaultSharingCommand(bool? All, IReadOnlyList<MediaType>? Types) : ICommand<DefaultSharingView>`; `DefaultSharingView(bool All, IReadOnlyList<MediaType> Types)`. `All = true` stores `DefaultShareTypes = null`.
  - `CreateShareRequest`/`UpdateShareRequest` gain trailing `bool? UseDefaultTypes = null, List<MediaType>? Types = null`; `LibraryIds` becomes optional and ignored for new shares. New share: `UseDefaultTypes = true` unless `UseDefaultTypes == false`. Update with `UseDefaultTypes` or `Types` set writes both fields (`false` + types, or `true`). Validation: Custom needs ≥1 type; unknown/duplicate types rejected.
  - `ShareView` gains `bool UseDefaultTypes, IReadOnlyList<MediaType> Types` (effective; legacy shares show `false` + derived types). `StatusResponse` gains `DefaultSharingView DefaultSharing, IReadOnlyList<MediaType> SupportedTypes` (init props).

- [ ] **Step 1: Failing tests:** `Catalog_WithoutTypes_ReturnsOnlyMoviesAndShows` (share Custom {Movies, Shows, Music}, source has a music item → none served); `Catalog_ServesIntersectionOfShareAndRequest` (share Movies only, request Movies+Shows → movies only); `Catalog_EtagDiffersByServedTypes`; `Catalog_LegacyShare_ServesWhatItServedBefore`; `IsExposed_ItemOfUnsharedType_False`; `UpdateDefaultSharing_All_StoresNull`; `UpdateDefaultSharing_EmptyCustom_Rejected`; `CreateShare_DefaultsToUseDefault`; `UpdateShare_Custom_StoresTypes_AndKeepsOtherFields` (name, limits, token hash unchanged); controller test that `types` reaches the query.
- [ ] **Step 2:** Run the filtered tests → FAIL.
- [ ] **Step 3:** Implement as in Interfaces. `GetCatalog` parses `types` with `ParseOrLegacy`.
- [ ] **Step 4:** Full suite → PASS, 0 warnings.
- [ ] **Step 5:** Commit `feat: share by media type`.

### Task 4: Receive side — accept list per peer

**Files:**
- Modify: `Source/Infrastructure/Api/Data.Peers/Clients/PeerClient.cs` (send `types`, keep local filter), `AccountPeerClient.cs` (`IncludeItemTypes` from the accept list ∩ supported), `Source/Application/Features/Sync/SyncService.cs` (replace the `Movie`/`Episode` string checks at the source collection and the movie/episode split with `MediaTypes.Of` + accepted), `Source/Application/Features/Relay/SourceSelector.cs` (skip a peer that no longer accepts the entry's type), peer admin DTOs/commands/handlers (`AddPeerRequest`, `UpdatePeerRequest`, `AddPeerCommand`, `UpdatePeerCommand`, `AddPeer.cs`, `UpdatePeer.cs`, `AdminRules.ToView`)
- Test: `Tests/UnitTests/Peers/PeerClientTests.cs`, `AccountPeerClientTests.cs`, `PeerHandlerTests.cs`, `Tests/UnitTests/Sync/SyncServiceTests.cs`, `Tests/UnitTests/Relay/SourceSelectorTests.cs`

**Interfaces:**
- Consumes: Task 2/3 (`MediaTypes.Accepted`, `Supported`, `Format`, catalog `types`).
- Produces: `Add/UpdatePeerRequest` and commands gain trailing `bool? AcceptAllTypes = null, List<MediaType>? AcceptTypes = null` (Only needs ≥1 type); `PeerView` gains `bool AcceptAllTypes, IReadOnlyList<MediaType> AcceptTypes`. Requested types = `Accepted(peer) ∩ Supported`. Changing the accept list clears that peer's stored `PeerState.Etag` (next sync is a full fetch).

- [ ] **Step 1: Failing tests:** `FetchCatalog_SendsAcceptedSupportedTypes` (query `types=Movies,Shows`); `FetchCatalog_DropsItemsOfTypesNotAccepted` (an older provider sends episodes to an "Only Movies" peer → dropped); `Account_FetchCatalog_IncludeItemTypesFollowAccept` (Only Movies → `IncludeItemTypes=Movie`); `Sync_PeerStopsAcceptingType_RemovesOnlyThatTypeFromThatPeer` (peers A and B both share a movie and an episode; A switches to Only Movies → A's episode source gone, B's episode placeholder stays, A's movie stays); `UpdatePeer_AcceptListChange_ClearsEtag`; `UpdatePeer_KeepsAcceptListWhenOmitted`; `SourceSelector_SkipsPeerNotAcceptingType`.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite → PASS.
- [ ] **Step 5:** Commit `feat: receive by media type`.

### Task 5: Admin page — default sharing, invitation types, receive row

**Files:**
- Modify: `Source/Infrastructure/Platform/Jellyfin/Web/configPage.html`, `configPage.js`; `harness/admin_page.js` (new mode `types`)

**Interfaces:** Consumes Task 3/4 API (enums are JSON strings). Labels from Global Constraints.

- [ ] **Step 1: Failing browser check** `node harness/admin_page.js types` on alice: the Share tab shows "Default sharing" with 7 ticks (Ebooks, Collections, Live TV disabled + "coming later"), "All media types" on; switching to a list and saving persists after reload; the create form offers "Use the default" (preselected) / "Custom"; an existing (legacy) share shows Custom with Movies + Shows ticked; on the Receive tab each peer has "Receive: Everything they share / Only:" with ticks enabled only for "Only"; choosing Only Movies, Save, reload shows it. The library picker on the share form is gone. The Share tab says once: "Friends who sign in with a Jellyfin user you made see what that user's library access allows; these ticks apply to invitations."
- [ ] **Step 2:** Run it → FAIL.
- [ ] **Step 3:** Implement with the page's existing helpers (`el`, `field`, `btn`, `api`, `busy`); all text through `el()` (no `innerHTML`).
- [ ] **Step 4:** Build, deploy to alice and bob, run the check → PASS; screenshot `harness/screens/media-types.png` (gitignored). Full `dotnet test` → PASS.
- [ ] **Step 5:** Commit `feat: media types on the admin page`.

### Task 6: Provider — music and audiobooks in the catalog and stream

**Files:**
- Modify: `Source/Domain/Models/Protocol/ProtocolDtos.cs` (`CatalogItem` trailing optional fields), `Source/Domain/Models/Library/LocalItemModels.cs` (`StreamInfo` gains `Guid? TitleId = null`), `JellyfinLocalItemSource.cs` (Music → `Audio`, Audiobooks → `AudioBook`; exposure candidates for `MusicAlbum`/`MusicArtist`/`Folder` ids resolve to their tracks for images), `CatalogBuilder` (`ProviderSupported` += Music, Audiobooks), `FederationController.Stream` (allow `Audio`/`AudioBook`; never capped; limiter key = `TitleId ?? itemId`), `FederationController.Image` (album/book artwork via the item's `AlbumId`)
- Test: `CatalogBuilderTests.cs`, `FederationControllerStreamTests.cs`, `StreamEndpointHttpTests.cs`

**Interfaces — Produces:** `CatalogItem` new trailing params (all default null): `string? Album`, `string? AlbumArtist`, `string[]? Artists`, `int? DiscNumber` (episode-style `SeasonNumber`/`EpisodeNumber` are **not** reused), `int? TrackNumber`, `string? AlbumId` (provider id of the album / book folder, for artwork and the title key), `string? FolderPath` (audiobooks only: the book's relative folder names below the library root, e.g. `Author X/Book Y`, no absolute paths). `Type` is `"Audio"` or `"AudioBook"`. `ProviderIds` carries MusicBrainz ids as Jellyfin names them.

- [ ] **Step 1: Failing tests:** `Catalog_MusicRequestedAndShared_ServesTracksWithAlbumFields`; `Catalog_AudiobookItem_CarriesFolderPathWithoutRoot`; `Stream_AudioOverShareCap_IsServedDirect_NotTranscoded` (share `MaxBitrate` 100 kbps, 320 kbps mp3 → 200 with the file); `Stream_AudioTracksOfOneAlbum_ShareOneLease` (`MaxStreams = 1`: track 1 open, track 2 of the same album → 200; a track of another album → 429); `Stream_AudioNotShared_404`.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite → PASS.
- [ ] **Step 5:** Commit `feat: share music and audiobooks`.

### Task 7: Identity and title keys for audio

**Files:**
- Modify: `Source/Domain/Keys/MediaKey.cs`, `Source/Domain/Keys/MergeIndex.cs` (kind preference keeps tmdb/imdb/tvdb first, then `mb`, then name keys), `Source/Application/Features/Sync/LibraryScopes.cs` (pass audio keys through), `RelayController.cs`, `Source/Infrastructure/Platform/Jellyfin/Filters/PlaybackInfoFilter.cs`, `JellyfinPlaybackStopper.cs` (all use the title key for `RelaySessions`)
- Create: `Source/Domain/Keys/TitleKey.cs`
- Test: `Tests/UnitTests/Keys/MediaKeyTests.cs`, `MergeIndexTests.cs`, `Tests/UnitTests/Keys/TitleKeyTests.cs`, `RelayControllerTests.cs`, `PlaybackInfoFilterTests.cs`

**Interfaces — Produces:**
- `MediaKey.For(CatalogItem)` adds: Audio → `track:mb:{recording id}` when present (`NormalizeId` accepts 36-char UUIDs `[0-9a-f-]`), plus always `track:name:{h}` where `h` = first 16 hex of SHA-256 over normalized `albumartist|album|disc|track|title` (normalize: lowercase, Unicode NFKD, strip diacritics and everything but letters/digits, collapse spaces); AudioBook → `book:name:{h}` over normalized `folderPath|name`. No title → no key (counted "without usable ids").
- `static class TitleKey { string For(MergedEntry entry) }`: Audio → `album:name:{h(albumartist|album)}`; AudioBook → `book:{h(folderPath)}`; everything else → `entry.CanonicalKey`.
- `PlaybackInfoFilter` skips the bitrate rule for Audio/AudioBook entries.

- [ ] **Step 1: Failing tests:** `For_Audio_WithMbid_HasMbAndNameKeys`; `For_Audio_NameKeyIgnoresCaseAndPunctuation` ("AC/DC" vs "ac dc"); `For_AudioBook_KeyFromFolderAndName`; `NormalizeId_AcceptsMusicBrainzUuid`; `MergeIndex_TrackFromTwoPeers_SameMbid_OneEntry`; `TitleKey_TracksOfOneAlbum_AreOneTitle`; `TitleKey_Movie_IsCanonicalKey`; `Relay_AudioOfSameAlbum_OneStreamOnAccountPeer` (MaxStreams 1, two tracks of one album both play); `PlaybackInfo_AudioPlaceholder_NoForcedTranscode`.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite → PASS.
- [ ] **Step 5:** Commit `feat: keys and stream titles for music and audiobooks`.

### Task 8: Receiver — music and audiobook placeholders

**Files:**
- Modify: `Source/Domain/Models/Library/LocalLibraryModels.cs` (`LocalMedia` trailing optional album/artist/track/folder fields), `JellyfinLocalLibrary.cs` and `JellyfinLocalItemEvents.cs` (read `Audio`, `AudioBook`, plus local `MusicArtist`/`MusicAlbum` names for merging), `Source/Domain/Models/Sync/LocalIndexResult.cs` (keys per `MediaType` instead of two buckets; keep `MovieKeys`/`EpisodeKeys` as views), `LocalIndex.cs`, `SyncService.cs` (apply per accepted+supported type with its own root; a type whose root is empty → skipped and reported), `Source/Application/Features/Placeholders/PathNaming.cs`, `NfoWriter.cs`, `PlaceholderPlanner.cs`, `Source/Domain/Models/Placeholders/PlaceholderLedger.cs` (`Seed` keeps the `.nfo` files the Task 1 layout uses), `PlaceholderWriter.cs` (artwork per album/book folder as Task 1 recorded), `Source/Domain/Models/Sync/SyncReports.cs` (`SkippedTypesWithoutFolder : IReadOnlyList<MediaType>`)
- Test: `PathNamingTests.cs`, `NfoWriterTests.cs`, `PlaceholderPlannerTests.cs`, `PlaceholderWriterTests.cs`, `LocalIndexTests.cs`, `SyncServiceTests.cs`

**Interfaces:** Consumes Task 1 layout, Task 6 fields, Task 7 keys. Produces `PathNaming.ForEntry` for Audio (`{Safe(albumArtist)}/{Safe(album)}/{disc?}{track:00} {Safe(title)}`) and AudioBook (`{Safe(each folderPath segment)}/{track:00?} {Safe(name)}`), using the owner's own artist/album names when the owner has that artist/album (like `LocalSeries` for shows); `NfoWriter.Album(...)`/`Artist(...)` with exactly the elements Task 1 proved.

- [ ] **Step 1: Failing tests:** `Planner_OwnerHasTrackByMbid_NotWritten`; `Planner_OwnerHasTrackByNames_DifferentCase_NotWritten`; `Planner_AlbumOwnerLacks_WritesTracksAndAlbumNfo`; partial album per the Task 1 result (merge into the owner's album names, or skip the whole album when the owner has any of it); `PathNaming_ArtistWithSlash_IsSafe` (AC/DC); `PathNaming_Audiobook_KeepsFolderSegments`; `Ledger_Seed_KeepsMusicNfo`; `Sync_MusicWithoutFolder_SkippedAndReported`; `Sync_MusicAndMoviesFromOnePeer_EachInItsRoot`; `LocalIndex_ReadsMusicKeys`.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite → PASS.
- [ ] **Step 5:** Commit `feat: receive music and audiobooks`.

### Task 9: Account peers' audio, setup folders, and switching the types on

**Files:**
- Modify: `AccountPeerClient.cs` + `JellyfinApiDtos.cs` (Audio/AudioBook fields: `Album`, `AlbumArtist`, `Artists`, `AlbumId`, `IndexNumber`, `ParentIndexNumber`, `Path`-free folder names via the parent chain; stream via `Audio/{id}/stream?static=true`, never the video transcode), `MediaTypes.Supported` (+ Music, Audiobooks), `AttachLibrary.cs` (+ `Music` → `music`, `Audiobooks` → `books`), `GetStatus.cs` + `StatusResponse` (`Music`, `Audiobooks` root views as init props), `configPage.html/js` (`attachControl('Music'…)`, `('Audiobooks'…)`, "set up the folder first" from `SkippedTypesWithoutFolder`)
- Test: `AccountPeerClientTests.cs`, `AdminHandlerTests.cs`, `AdminControllerTests.cs`, `harness/admin_page.js types` extended

- [ ] **Step 1: Failing tests:** `Account_Audio_MapsAlbumFields`; `Account_Audio_StreamsStaticAudio_EvenOverBitrateLimit`; `Attach_Music_AddsToMusicLibrary`; `Attach_Audiobooks_RejectsNonBooksLibrary`; `Status_HasMusicAndAudiobooksRoots`; browser check: Music and Audiobooks ticks enabled, Setup shows both folders with "Add to library".
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite + browser check → PASS.
- [ ] **Step 5:** Commit `feat: music and audiobooks from account peers, setup folders`.

### Task 10: End to end and docs

**Files:**
- Modify: `harness/e2e.py` (section `check_media_types`), `README.md` (sharing by type, receiving by type, Music/Audiobooks setup, podcasts = audiobooks, what's "coming later"), `CHANGELOG.md` (`## [Unreleased]`: Added/Changed incl. "a share that covered one of two movie libraries now covers all movie libraries"), `docs/spike-results.md` (what the e2e proved)

- [ ] **Step 1:** Write the section (cleans up in `finally`, each undo step in its own try): attach Music and Audiobooks folders on alice; bob shares Music+Audiobooks → alice's Music shows Artist B / Album Two (2 tracks, right order) merged under existing artists where the Task 1 rule says, alice's own Album One tracks not duplicated, Audiobooks shows Book Y; a music track plays through the relay (ffprobe shows audio); with the share `MaxStreams = 1`, two tracks of one album play back to back; alice sets bob to Only Movies → after sync no music/audiobook placeholders from bob remain and movies remain; a catalog request without `types` returns only Movie/Episode items; account peer with the friend user granted Music gets Album Two too.
- [ ] **Step 2:** Deploy to alice and bob, run `py harness/e2e.py` until `ALL PASS` twice in a row. A product bug → stop and report, don't bend the test.
- [ ] **Step 3:** Docs as listed; no secrets.
- [ ] **Step 4:** `dotnet test JellyShare.slnx -c Release` → PASS, 0 warnings.
- [ ] **Step 5:** Commit `test: media types end to end` and `docs: media types`.
