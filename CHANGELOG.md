# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Plugin versions have four numbers (Jellyfin convention).

## [Unreleased]

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

- Account peers: receive from a friend's Jellyfin that does not run JellyShare by signing in with a Jellyfin user they
  made for you (address, username, password). The password is never stored; only the access token is kept, in headers
  only. Per peer stream limit (`MaxStreams`, default 1) with **Refuse the new viewer** (Jellyfin's
  `RateLimitExceeded` playback error) or **Stop the oldest viewer** (message + stop command, stream cut). The friend
  user's remote bitrate limit is respected (titles above it play as a non-seekable transcode). A failed sign-in keeps
  the titles for 7 days and shows "Sign-in failed" with **Sign in again** on the admin page. Admin API:
  `kind`, `username`, `password`, `maxStreams`, `limitAction` on `POST`/`PUT admin/peers`. The harness e2e
  (`harness/e2e.py`) has an "account peers" section.
- `.github/workflows/ci.yml`: build and tests on every push and pull request to `main`.
- Architecture tests for layer dependencies, contract locations, handler and request conventions and module setup.
