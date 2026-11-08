# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Plugin versions have four numbers (Jellyfin convention).

## [Unreleased]

### Added

- Media types: **Movies, Shows, Music, Audiobooks & podcasts** (Ebooks, Collections and Live TV are listed as "coming
  later"). **Default sharing** on the admin page sets the types an invitation offers with **Use the default**; an
  invitation can instead be **Custom** with its own types. Admin API: `PUT admin/sharing/default` (`all`, `types`),
  `useDefaultTypes`/`types` on `POST`/`PUT admin/shares`, status `DefaultSharing`, `SupportedTypes`.
- Receiving by type, per peer: **Everything they share** (default) or **Only:** a list. Unticking a type removes that
  peer's placeholders of that type at the next sync. Admin API: `acceptAllTypes`/`acceptTypes` on `POST`/`PUT
  admin/peers`. The catalog request carries the wanted types (`?types=`).
- Music and audiobooks: placeholder folders `placeholders/Music` and `placeholders/Audiobooks` with **Add to library**
  in Setup (music library / books library; status `Music`, `Audiobooks`). Tracks are written as
  `{album artist}/{album}/{disc}{track} {title}.strm`, audiobooks as their author/book folders with `{NN} {name}.strm`,
  plus `folder.jpg` when the peer has cover art (no `.nfo`). After the scan JellyShare writes the track details (title,
  artists, album artist, album, track and disc number; title and people locked) into the library, so a rescan or
  restart keeps them. A peer's album is skipped when you own any track of it; albums you lack join your artist when the
  names match. A type whose folder is not a folder of a library of that kind is skipped and reported
  (`lastSync.SkippedTypesWithoutFolder`, "set up the folder first" on the admin page).
- Audio playback through the relay: a media source provider plus the PlaybackInfo filter point `Audio/{id}/universal`,
  `stream`, HLS and `PlaybackInfo` at the relay (also for clients that skip PlaybackInfo); the relay address stays
  hidden. Audio is never sent through the capped video transcode, and an album (a book) counts as one stream on share
  and account peer limits. The startup check also warns when Jellyfin's audio routes change.
- Account peers receive music and audiobooks from the friend user's music and books libraries (static audio stream,
  never the video transcode).
- Harness: `harness/e2e.py` "media types" section and `harness/web_play_audio.js` (jellyfin-web plays a track and an
  audiobook in headless Chromium).

### Changed

- Shares are by media type, not by library: a type covers every library of that kind on the server. **A share that
  covered one of two movie libraries now covers all movie libraries** (the library list in `LibraryIds` is ignored on
  create and update).
- A share saved by 0.2.0.0 keeps offering exactly Movies and/or Shows, derived from the kinds of the libraries it
  listed (a mixed library counts as both); music or books libraries in it are **not** shared until you tick Music or
  Audiobooks for that invitation. Editing only its name or limits keeps that (the admin page only sends the types when
  you change them); choosing types makes it Use the default or Custom. New invitations use the default sharing (all
  types at first). **A script that creates a share with only `libraryIds` now gets a "Use the default" share** (the
  library list is ignored); send `useDefaultTypes: false` with `types` for a fixed list.
- A list of media types (Custom share types, Custom default sharing, a peer's Only: list) must contain at least one
  supported type ("Pick at least one supported media type.").
- Peers saved by 0.2.0.0 accept everything. The first sync after the upgrade fetches each peer's full catalog once (the
  catalog ETag now includes the media types). A 0.2.0.0 receiver, which sends no `types`, still gets only movies and
  episodes.

### Known limitations

- A friend's audiobook or podcast folder is skipped when you own any file of it (books group per folder, so a partial
  folder would show twice): once you own one episode of a podcast, the friend's newer episodes of it don't appear.

## [0.2.0.0] - 2026-11-02

### Added

- Account peers: receive from a friend's Jellyfin that does not run JellyShare by signing in with a Jellyfin user they
  made for you (address, username, password). The password is never stored; only the access token is kept, in headers
  only. Per peer stream limit (`MaxStreams`, default 1) with **Refuse the new viewer** (Jellyfin's
  `RateLimitExceeded` playback error) or **Stop the oldest viewer** (message + stop command, stream cut). The friend
  user's remote bitrate limit is respected (titles above it play as a non-seekable transcode). A failed sign-in keeps
  the titles for 7 days and shows "Sign-in failed" with **Sign in again** on the admin page. Admin API:
  `kind`, `username`, `password`, `maxStreams`, `limitAction` on `POST`/`PUT admin/peers`. The harness e2e
  (`harness/e2e.py`) has an "account peers" section.
- Catalog icon (`assets/jellyshare.png`).

### Changed

- The plugin repository URL is now `https://github.com/OFA-Tech/JellyShare/releases/latest/download/manifest.json`
  (each release carries the manifest). Replace the old `manifest` branch URL under Plugins → Repositories.

## [0.1.0.0] - 2026-11-02

First release.

### Changed

- The code follows the OFA-TECH WebApi template layout: `JellyShare.Domain`, `JellyShare.Application`, the
  infrastructure modules `JellyShare.Data.Peers`, `JellyShare.Data.Files` and `JellyShare.Media`, and the plugin
  project `JellyShare.Jellyfin`. Routes, the `X-JellyShare-Token` header, the `.jellyshare` marker, data file names,
  the plugin GUID, the plugin name and `targetAbi` 12.0.0.0 are unchanged.
- The plugin ships six DLLs instead of one: the entry DLL `Jellyfin.Plugin.JellyShare.dll` is now
  `JellyShare.Jellyfin.dll`, next to `JellyShare.Application.dll`, `JellyShare.Domain.dll`, `JellyShare.Data.Peers.dll`,
  `JellyShare.Data.Files.dll` and `JellyShare.Media.dll`. The release zip contains all six.
- Jellyfin names the plugin data folder and configuration file after the entry assembly, so they move from
  `plugins/Jellyfin.Plugin.JellyShare/` and `plugins/configurations/Jellyfin.Plugin.JellyShare.xml` to
  `plugins/JellyShare.Jellyfin/` and `plugins/configurations/JellyShare.Jellyfin.xml`. An existing installation has to
  move them (no release used the old names).
- Logger categories follow the new namespaces (`JellyShare.<Project>.<Folder>.<Type>` instead of
  `Jellyfin.Plugin.JellyShare.*`). Messages are unchanged, but some lines moved to the type that now does the work:
  admin messages log under the `JellyShare.Application.Features.*.*Handler` types; placeholder writes and "placeholder
  folder unreadable" under `PlaceholderWriter`; "pending scan list unreadable" under `PendingScanStore`; the startup
  line "JellyShare infrastructure modules enabled" under `InfrastructureModuleLog`.
- The bitrate decision is split: how Jellyfin derives the max bitrate (`MediaInfoHelper.GetMaxBitrate`) and the
  Jellyfin-internal lookup port (`IPlaceholderLookup`, `CallerContext`, `CallerLimits`) moved from Domain/Application
  into `JellyShare.Jellyfin` (now `internal`); `PlaybackBitrateRule.ForcedTranscodeLimit` now takes the already derived
  max bitrate.
- Use cases run as handlers behind a dispatcher with logging and validation behaviors. The logging behavior logs
  request type names at debug level only; exceptions are left to Jellyfin to log once.
- Some log lines now include the exception (stack trace) next to the unchanged message: the warning "Could not start
  ffmpeg for a capped stream", the error "JellyShare sync failed" (error level; the exception carries no inner exception
  and the peer client already redacts tokens from its messages), the warning "watcher: stopped before ... background
  passes finished" and the debug line "Capped stream write failed". Peer errors in the relay are still logged as a
  redacted message only, and the expected-cancellation debug lines ("Capped stream replaced by a newer request", "client
  disconnected", relay peer timeout or stall) log no exception.
- Build: the template's analyzers (`Microsoft.CodeAnalysis.NetAnalyzers`, `SonarAnalyzer.CSharp`) at
  `latest-recommended`, code style enforced in the build and warnings treated as errors. Tests use Shouldly.


### Added

- `.github/workflows/ci.yml`: build and tests on every push and pull request to `main`.
- Architecture tests for layer dependencies, contract locations, handler and request conventions and module setup.
