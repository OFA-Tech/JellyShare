# JellyShare

A Jellyfin 12 plugin that lets several Jellyfin servers share movies, shows, music and audiobooks (podcasts included)
with each other. A provider server shares some media types with a token; a consumer server pulls the provider's
catalog, writes small `.strm` placeholders (plus `.nfo` files for movies and shows) for the titles it does not already
own, and plays them through a local relay that streams from the provider. Titles you already own are never duplicated,
and your own media folders are never touched.

The code follows the OFA-TECH WebApi template layout (Domain, Application, infrastructure modules, presentation), so
an update to a new Jellyfin version only touches one project: `Source/Infrastructure/Platform/Jellyfin`.

Documentation:

- Design (binding for behaviour): [`docs/specs/2026-10-31-jellyfin-federation-plugin-design.md`](docs/specs/2026-10-31-jellyfin-federation-plugin-design.md)
- Verified Jellyfin API names and plan corrections: [`docs/spike-results.md`](docs/spike-results.md)
- Layout restructure plan: [`docs/plans/2026-11-01-template-restructure.md`](docs/plans/2026-11-01-template-restructure.md)
  (the original implementation plan, [`docs/plans/2026-10-31-jellyfin-federation-plugin.md`](docs/plans/2026-10-31-jellyfin-federation-plugin.md), is superseded for code layout)
- Changes: [`CHANGELOG.md`](CHANGELOG.md)

## Install

JellyShare needs Jellyfin 12 (target ABI 12.0.0.0). Install it on every server that shares or receives:

1. In Jellyfin: **Dashboard → Plugins → Repositories → +** (the Repositories button on the Plugins page).
2. Name: `JellyShare`. URL:

   ```text
   https://github.com/OFA-Tech/JellyShare/releases/latest/download/manifest.json
   ```

3. **Dashboard → Plugins → Catalog → General → JellyShare → Install** (pick the newest version).
4. Restart Jellyfin. **JellyShare** now appears under Plugins as *Active*.

Updates: new versions show up in the Catalog; install the newer one and restart.

## Pairing two servers

Sharing is one direction at a time. The server that owns the files is the **provider**; the one that wants to
watch them is the **consumer**. For a two-way exchange, each side does both parts once.

On the **provider** (**Dashboard → Plugins → JellyShare → Share with peers**):

1. Give the invitation a name (who it is for), choose which media types it shares (**Use the default** or
   **Custom**, see [Sharing and receiving by media type](#sharing-and-receiving-by-media-type)), the max streams at
   once and, optionally, a max bitrate. See [Stream limits and the bitrate cap](#stream-limits-and-the-bitrate-cap).
2. **Create invitation.** The token is shown **once**, on this screen only; copy it now. The server keeps only a hash,
   so a lost token means creating a new invitation (and removing the old one).
3. Send the friend your server address (the one they can reach from outside, including your reverse-proxy path if you
   use one, for example `https://alice.example.com/jellyfin`) and the token. Use a private channel.

On the **consumer** (**Dashboard → Plugins → JellyShare → Receive from peers**):

1. Follow **Setup**: JellyShare keeps the peers' titles in folders of its own, one per media type (movies, shows,
   music, audiobooks). For each type you want, pick your existing library of that kind and press **Add to library**;
   Jellyfin then scans that library once. Do this before the first sync. If you ever attach a placeholder folder by hand, attach it **with a library refresh**;
   without one Jellyfin never notices later changes in it.
2. **Add a peer:** name, server address, token. Press **Test connection** first; it must report success.
3. **Add peer**, then **Sync now**. The sync runs in the background (the page shows "Syncing..." and you can leave
   it); posters and backdrops are fetched after the placeholders are written. About a minute later the provider's titles that you do not already have appear
   in your libraries (movies, and series with their seasons and episodes). They play directly from the peer.
   If several peers have a title, the first one in the Peers list is used (change the order with the arrows).

Things to know:

- Titles you already own are not duplicated. If a series exists on both servers, the placeholder episodes join *your*
  series, as long as your Shows library has **"Automatically merge series that are spread across multiple folders"**
  turned on (Dashboard → Libraries → Shows → Manage library → Settings). **The admin page warns when it is off; if it
  stays off, peer episodes show up as a separate copy of the series.**
- The sync also runs as the scheduled task "JellyShare: sync peers", every 6 hours by default. Change the interval in
  **Dashboard → Scheduled Tasks → JellyShare: sync peers** (the plugin's `SyncIntervalHours` setting only sets the
  default trigger when the task is first registered). If your own file for a title disappears (deleted, or the drive is offline),
  the peer's copy takes over within a minute or two, and goes away again when your file is back.

## Sharing and receiving by media type

The media types are **Movies**, **Shows**, **Music** and **Audiobooks & podcasts**. **Ebooks**, **Collections** and
**Live TV** are listed on the admin page as "coming later" (their ticks are disabled); nothing is shared or received
for them yet.

Sharing (**Share with peers**):

- **Default sharing** sets the media types an invitation offers when it uses **Use the default** (all types at first).
  Changing the default changes every invitation that uses it.
- Each invitation is either **Use the default** or **Custom** with its own ticks. A type covers all your libraries of
  that kind (Music = every music library), not single libraries.
- Friends who sign in with a Jellyfin user you made for them ([Account peers](#account-peers)) see whatever that
  user's **Library access** allows; the ticks only apply to invitations.

Receiving (**Receive from peers**, per peer): **Everything they share** (default), or **Only:** the types you tick.
Unticking a type removes that peer's placeholders of that type at the next sync; its other types and other peers'
titles stay.

Music and audiobooks:

1. In **Setup**, add the Music placeholder folder to a **Music** library and the Audiobooks folder to a **Books**
   library (**Add to library**). The folders only count when they are a folder of a library of that kind; otherwise the
   sync skips the type and the admin page says "set up the folder first" (the folder's existing placeholders are kept).
2. Sync. A peer's albums show up under their artist (`Artist/Album/01 Title`), audiobooks under their author and book
   folders, with a `folder.jpg` when the peer has cover art. JellyShare writes the track details (title, artists,
   album artist, album, track and disc number) into your library after the scan and locks the title, so a rescan or
   restart keeps them.
3. Podcasts: put them in a Books library like audiobooks; they are shared as **Audiobooks & podcasts**.

Tracks and audiobooks play directly in Jellyfin Web (and in apps that use Jellyfin's universal audio endpoint), through
the same relay as videos; the relay address never reaches the client.

## Account peers

A friend can share their Jellyfin with you **without installing JellyShare**: they give you a normal Jellyfin login
and your server signs in with it.

What the **friend** does (on their Jellyfin, Dashboard → Users):

1. Create a new user just for you (do not hand out their own login). Under **Library access**, tick only the
   libraries they want to share; parental rating limits work as usual.
2. Optionally set **Internet streaming bitrate limit** on that user (see below).
3. Send you their server address (as you would reach it, including a reverse-proxy path, for example
   `https://bob.example.com/jellyfin`), the user name and the password. Use a private channel.

What **you** enter (**Receive from peers → Add a peer**, Sign in with: **Jellyfin account**): name, server address,
username, password, **Max streams at a time** (default 1) and **When the limit is reached**. Press **Test connection**,
then **Add peer** and **Sync now**, as in [Pairing two servers](#pairing-two-servers). The friend's titles appear next
to yours through the same placeholders; your own copy still wins, and the Peers list order decides between peers.

- **Password:** used once to sign in and never stored. JellyShare keeps only Jellyfin's access token for that login.
- **Stream limit:** Jellyfin cannot limit how many streams one login plays at once, so JellyShare does it on your side.
  A stream is one title playing from that peer (two of your users watching the same title count once; seeks and range
  requests of a playing title never count again). It stays counted while data flows and for 30 s after. When a new
  title would go over the limit, another peer with the same title is used first; otherwise:
  - **Refuse the new viewer** (default): the viewer gets Jellyfin's "rate limit exceeded" playback error.
  - **Stop the oldest viewer:** the oldest playback from that peer gets a short message and a stop command, its
    stream is cut, and the new one starts. Some apps ask for playback info as soon as you open a title, so with this
    choice just opening another title from that friend can stop the oldest stream.
- **Bitrate limit:** JellyShare respects the friend user's internet streaming bitrate limit. Titles above it are fetched
  as a transcoded stream from the friend's server; **such streams cannot seek** (shortcut: a seek waits or restarts,
  like a capped title, see "Seeking a capped title" below).
- **Sign-in failed** (the friend changed the password or removed the user): titles stay for 7 days but do not play
  (another peer with the same title still does). The admin page shows "Sign-in failed"; type the current password and
  press **Sign in again**. The message clears after the next sync.
- Watched status stays on your server; nothing is written back to the friend's server.

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
- The transcode counts as a stream like any other. At most two transcodes run per stream (share + title), so two
  people on the consumer can watch the same title; a third request for it stops the oldest transcode and cuts its
  connection (this also covers a seek that restarts the stream). The
  server runs at most `MaxConcurrentTranscodes` transcodes across all shares (plugin setting, default 4); beyond
  that capped requests get `429` with `Retry-After: 30`. ffmpeg is killed (whole process tree) as soon as the
  consumer disconnects, the request is replaced, or the transcode finishes.
- **Seeking a capped title:** the transcode has no byte ranges (`Accept-Ranges: none`, Range requests get the whole
  stream from the start, status 200). A peer can start at an offset with `?start=<seconds>` (cut to the runtime).
  On the consumer, Jellyfin sees a live MPEG-TS without a duration: the web client plays it through the consumer's
  own HLS transcode. Seeking works within what the consumer has already received (in the harness a 2-minute clip
  at a 500 kbps cap arrived in ~10 s and a seek to 1:30 played); a seek beyond that waits for the stream to catch up
  or, in clients that reopen the URL, restarts it from the beginning. Uncapped titles keep full byte-range seeking.

## Known limitations

- **Native apps (phone, TV) are not confirmed yet.** Everything was verified with Jellyfin Web. Other clients should
  work, because the plugin hides the relay path and forces a transcode when the client asks for a lower bitrate than
  the file has, but this still has to be tried on a real phone/TV app.
- **Capped streams are not seekable on the provider side.** Above the provider's max bitrate the file is live-transcoded
  to MPEG-TS with no byte ranges: a seek beyond what the consumer has already received waits for the stream to catch
  up or restarts it (details under "Seeking a capped title"). Uncapped titles seek normally.
- **No failover in the middle of playback.** If a peer fails or throttles before the first byte, the consumer
  tries another peer that has the title. Once data is flowing, a peer that drops the connection ends playback;
  press play again to retry.
- **`.strm` placeholders and the consumer's own bitrate limit.** Jellyfin does not transcode `.strm` items by itself
  when a client's quality setting is below the file's bitrate. JellyShare adds a PlaybackInfo filter that turns direct
  play off and asks Jellyfin for a transcode in that case; items it does not recognise as placeholders are untouched.
  Clients that skip PlaybackInfo and open the stream directly are not covered.
- **User data (watched, resume position, favourites)** carries over between your own copy of a title and the peer's
  placeholder, because both have the same provider ID (episodes: series + season/episode number). Placeholders are
  separate items: while the placeholder is the one in use, the owner's stale item is still in the library next to it
  (movies show up twice; shows stay one series). The carry-over was seen in tests but not every time: once a new
  placeholder showed as unwatched while the owner's stale item was still listed. When your file comes back, your own
  item keeps its watch state.
- **Reverse proxy on the same host as Jellyfin** must send `X-Forwarded-For` (see Security notes), otherwise the
  loopback-only relay could be reachable through it.
- **Placeholder folders must be attached to a library with a refresh** (Pairing, consumer step 1). The admin page does
  this; a manual attach without a refresh silently loses changes.
- Items are matched by provider IDs (movies: Tmdb/Imdb; episodes: the series' Tvdb/Tmdb ID + season/episode number).
  Peer titles without a usable ID get no placeholder at all; the sync report counts them as "Without usable ids".
- **Disabling or removing a peer** removes its placeholders at the next sync (removing a peer starts one right away).
  A peer that is merely offline keeps its placeholders for 7 days.
- **Jellyfin's "Nfo" metadata saver** may rewrite the placeholder `.nfo` files or add `season.nfo`; JellyShare leaves
  those edits alone. It remembers what it wrote (`placeholder-ledger.json` in the plugin data folder) and only rewrites
  a file when the peer's data for it changed, and only deletes files it wrote itself (plus the whole folder when a
  title goes away).
- **Music: partial albums are not merged.** Jellyfin makes one album per folder, so a friend's tracks can't join an
  album you own part of. A friend's album is therefore skipped completely when you own any track of it (by MusicBrainz
  id, or by artist + album + title ignoring case and punctuation). Albums you don't have at all join your existing
  artist when the artist name matches yours ignoring case, accents, spaces and punctuation; names that still differ
  ("The Beatles" vs "Beatles") stay separate artists.
- **Audio is never transcoded or capped by the provider.** A share's max bitrate applies to video only; music and
  audiobooks are always sent as the original file.
- **An album counts as one stream** (an audiobook: the book), on the provider's share limit and on an account peer's
  limit, so playing an album track after track never hits the limit; a second album or book does.
- **Account peers' audiobooks:** the friend's server is asked for each book's folder names on every catalog fetch
  (one extra request per book folder).
- **Audio playback relies on Jellyfin internals** (a media source provider plus a filter on Jellyfin's audio stream
  routes, see `docs/spike-results.md`, "Audio playback spike"). A Jellyfin update can break it; JellyShare checks at
  startup that the routes and parameters it hooks still exist and logs a warning when they don't.
- **Placeholder folders must be JellyShare's own:** a sync refuses to run (the admin page shows the error) when a
  placeholder folder is, contains or is inside one of your library folders, or when it already contains files but no
  `.jellyshare` marker.

## Security notes

- Placeholder `.strm` files contain only a loopback relay URL (`http://127.0.0.1:{port}{BaseUrl}/JellyShare/v1/play/{key}`),
  never a token. Peer tokens are sent only in the `X-JellyShare-Token` header and are never logged. A
  `RelayBaseOverride` setting must also be a loopback URL (`http://127.0.0.1…`, `http://[::1]…`, `http://localhost…`);
  any other value stops the sync with an error, because the relay would refuse it.
- The standard Jellyfin plugin configuration API (`/Plugins/{id}/Configuration`) returns the whole plugin configuration to
  administrators, including the peer tokens you received and the hashes of the invitations you created. The JellyShare
  admin page never receives them (its own `/JellyShare/v1/admin/...` endpoints are admin-only and return neither), but
  anyone with an administrator login or API key on your server can read them through the standard API.
- **Account peers:** the stored access token is a full Jellyfin login for the friend's account, not a limited
  share token: whoever has it can do anything that user can (browse, play, change its own settings). It is kept like
  the peer tokens above (same plugin configuration, sent only in the `Authorization` header, never in a URL, `.strm`
  file, log line or admin response). The friend should therefore give a **dedicated user** that sees only the libraries
  they want to share, never their own or an admin login. Changing that user's password on their side revokes the token.
- The relay answers only requests from loopback that carry **no** proxy header (`X-Forwarded-For`, `X-Forwarded-Host`,
  `X-Forwarded-Proto`, `X-Forwarded-Port`, `X-Real-IP`, `Forwarded`, `Via`, `X-Original-*`, `CF-Connecting-IP`,
  `True-Client-IP`); everything else gets 403. Jellyfin also hides the relay URL from clients.
- **Reverse proxy on the same host as Jellyfin:** it must add `X-Forwarded-For` to the requests it forwards, otherwise
  it looks like a local caller and the relay could be reached from outside through it. Most proxies do this by
  default (Nginx Proxy Manager, Caddy, Traefik). Plain nginx needs:

  ```nginx
  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
  ```

## Uninstall

1. In each library that has a JellyShare placeholder folder (Movies, Shows, Music, Audiobooks), remove that folder from
   the library (Dashboard → Libraries → Manage library → Folders) and let the library scan, so the placeholder items
   disappear.
2. **Dashboard → Plugins → JellyShare → Uninstall**, then restart Jellyfin.
3. Delete the placeholder folders (the ones shown in the plugin's Setup section; by default `placeholders/Movies`,
   `placeholders/Shows`, `placeholders/Music` and `placeholders/Audiobooks` in the plugin's data folder) if they are
   still on disk. Your own media folders are never touched.

## Structure

```text
JellyShare.slnx
Directory.Build.props  Directory.Packages.props  .editorconfig  global.json  Nuget.config
Source/
  Domain/                      JellyShare.Domain
  Application/                 JellyShare.Application
  Infrastructure/
    Api/Data.Peers/            JellyShare.Data.Peers
    Data/Data.Files/           JellyShare.Data.Files
    Platform/Media/            JellyShare.Media
    Platform/Jellyfin/         JellyShare.Jellyfin   (plugin entry DLL and composition root)
Tests/
  UnitTests/  JellyfinTests/  ArchitectureTests/
build/  harness/  docs/  .github/workflows/
```

| Project | Contains | References |
| --- | --- | --- |
| `JellyShare.Domain` | Plugin settings as plain classes, protocol and admin models, request records, ports (`Interfaces/<Area>/I*.cs`), `Result`/`Error`, messaging contracts, media keys and merge logic | nothing |
| `JellyShare.Application` | Use cases: handlers behind `ISender` (`Features/<Area>`), sync, catalog, streaming, relay, placeholder planning, logging and validation behaviors | Domain |
| `JellyShare.Data.Peers` | Outbound peer HTTP client (`IPeerClient`) | Domain |
| `JellyShare.Data.Files` | Atomic files and JSON stores in the plugin data folder, placeholder writer | Domain |
| `JellyShare.Media` | ffmpeg process runner (`IFfmpegRunner`) | Domain |
| `JellyShare.Jellyfin` | `Plugin`, service registrator, `PluginConfiguration`, controllers, PlaybackInfo filter, scheduled task, Jellyfin adapters for the Domain ports, module loader, admin page | all of the above, Jellyfin |
| `JellyShare.UnitTests` | Domain, Application and infrastructure module tests (no Jellyfin types) | Domain, Application, modules |
| `JellyShare.JellyfinTests` | Everything that needs Jellyfin or ASP.NET Core types: controllers, filter, adapters, HTTP tests | all |
| `JellyShare.ArchitectureTests` | Layer and naming rules (NetArchTest, Mono.Cecil) | Domain, Application, Jellyfin output |

### Dependency rules

Enforced by `Tests/ArchitectureTests`:

- Domain depends on nothing; Application only on Domain; each infrastructure module only on Domain.
- Only `JellyShare.Jellyfin` (and its tests) may use `Jellyfin.*`, `MediaBrowser.*` or `Microsoft.AspNetCore.*`.
- Ports (interfaces) live in the Domain. Handlers are public sealed `*Handler` classes in
  `JellyShare.Application.Features.<Area>`; requests are `*Command`/`*Query` records in
  `JellyShare.Domain.Models.Requests.<Area>` and respond with `Result` or `Result<T>`.
- Namespaces match folders; the infrastructure group folders (`Api/`, `Data/`, `Platform/`) are not part of namespaces.
- Each infrastructure project has `Modules/<X>Module.cs` (an `IInfrastructureModule`) and `Setup/Add<X>Setup.cs`.
  `JellyShare.Jellyfin/Modules/InfrastructureModuleLoader` lists the modules explicitly.

Jellyfin loads every DLL in the plugin folder, so the shipped DLLs carry no third-party runtime dependency.
`Microsoft.Extensions.*` abstractions are referenced compile-only (`ExcludeAssets="runtime"`), because the Jellyfin host
provides them. The release zip contains only the six `JellyShare.*.dll`.

### Build settings

`Directory.Build.props` and `.editorconfig` are the template's: `AnalysisLevel` `latest-recommended`,
`EnforceCodeStyleInBuild`, `TreatWarningsAsErrors`, and the analyzers `Microsoft.CodeAnalysis.NetAnalyzers` and
`SonarAnalyzer.CSharp` as `GlobalPackageReference` (build-time only, never shipped). Package versions are central in
`Directory.Packages.props`. A rule is turned off only where it conflicts with Jellyfin's or a framework's contract, with
a targeted `[SuppressMessage(..., Justification = "...")]` next to the code.

Deviations from the template, on purpose:

- No `Directory.Build.targets`: it only strips template pieces JellyShare doesn't have.
- `coverlet.collector` is in every test project (test-only). NSubstitute is not referenced, because no test needs it;
  fakes are hand-written.
- Test classes are `sealed` with Shouldly assertions; `Should.Throw` checks go through `ShouldExactly` (exact exception
  type), and string `ShouldContain`/`ShouldStartWith` always name their `Case`.

## Updating for a new Jellyfin version

Only `Source/Infrastructure/Platform/Jellyfin` (and `Tests/JellyfinTests`) reference Jellyfin. To move to a new
version: bump `Jellyfin.Controller`/`Jellyfin.Model` in `Directory.Packages.props`, `targetAbi` in `build.yaml` and
`build/update_manifest.py`, and the harness image tag; then fix what no longer compiles in that project. These are the
Jellyfin APIs it uses:

| File | Jellyfin API |
| --- | --- |
| `Plugin.cs` | `BasePlugin<PluginConfiguration>`, `IHasWebPages`/`PluginPageInfo`, `IApplicationPaths`, `IXmlSerializer` |
| `PluginServiceRegistrator.cs` | `IPluginServiceRegistrator`, `IServerApplicationHost` |
| `Configuration/PluginConfiguration.cs` | `BasePluginConfiguration` (XML-serialized by Jellyfin; mapped to the Domain `JellyShareSettings` by `PluginSettingsProvider`/`PluginSettingsStore`) |
| `Adapters/JellyfinLocalItemSource.cs` | `ILibraryManager` queries, `IMediaSourceManager`, `BaseItem`/`Movie`/`Series`/`Episode`, `ImageType` |
| `Adapters/JellyfinLocalLibrary.cs` | `ILibraryManager` item queries by provider id (`Jellyfin.Data.Enums.BaseItemKind`) |
| `Adapters/JellyfinLocalItemEvents.cs` | `ILibraryManager.ItemAdded`/`ItemRemoved` |
| `Adapters/JellyfinAdminLibraries.cs` | `ILibraryManager.GetVirtualFolders`/`AddMediaPath` (`MediaPathInfo`, `LibraryOptions`)/`QueueLibraryScan`, `ILibraryMonitor.Stop`/`Start` around the attach |
| `Adapters/LibraryMonitorScanReporter.cs` | `ILibraryMonitor.ReportFileSystemChanged` |
| `Adapters/JellyfinEncoderSettings.cs` | `IConfigurationManager.GetEncodingOptions()` (hardware encoder, VA-API device), `IMediaEncoder.EncoderPath` |
| `Adapters/JellyfinRelayBase.cs` | `IServerApplicationHost.HttpPort`, `IServerConfigurationManager.GetNetworkConfiguration()` (BaseUrl) |
| `Adapters/JellyfinPlaceholderLookup.cs` | `ILibraryManager`, `INetworkManager`, `IDeviceManager`, `IServerConfigurationManager` |
| `Adapters/JellyfinCallerResolver.cs` | `IAuthorizationContext` |
| `Adapters/ServerIdentity.cs` | `IServerApplicationHost.FriendlyName` |
| `Controllers/V1/*` | ASP.NET Core MVC; `AdminController` uses Jellyfin's `Policies.RequiresElevation` |
| `Filters/PlaybackInfoFilter.cs` | an MVC filter on Jellyfin's PlaybackInfo and item endpoints: `PlaybackInfoResponse`, `MediaSourceInfo`, `BaseItemDto`, `QueryResult<T>` |
| `Adapters/JellyfinBitrateRules.cs` | mirrors Jellyfin's `MediaInfoHelper.GetMaxBitrate`: the requested bitrate capped by the remote-client limit, which applies only to callers outside the local network (`INetworkManager.IsInLocalNetwork`). Jellyfin-internal ports and records (`IPlaceholderLookup`, `CallerContext`, `CallerLimits`) sit next to it and are `internal` |
| `Filters/PlaybackInfoTargetCheck.cs` | checks at startup that Jellyfin's PlaybackInfo actions still have the route and parameter names the filter expects (logs a warning otherwise) |
| `Tasks/SyncTask.cs` | `IScheduledTask`, `TaskTriggerInfo` |
| `Web/configPage.{html,js}` | the dashboard's `ApiClient` (`getUrl`, `ajax`) |

`Tests/JellyfinTests/Filters/PlaybackInfoFilterTests.cs` pins the PlaybackInfo routes and parameter names of the
supported version; update it with the filter. Then run the full test suite and the harness e2e (below).

### What else mirrors Jellyfin's conventions

These places are outside the Jellyfin project because they are JellyShare's own rules, but they copy how Jellyfin
behaves. After a Jellyfin update, recheck them:

- **Library folder paths** (`Application/Features/Admin/AdminRules.SamePath`): compares paths the way Jellyfin stores
  them (slash style, trailing separator, case rules per OS). Recheck if Jellyfin changes how virtual-folder paths are
  stored or reported.
- **Placeholder naming and NFO** (`Application/Features/Placeholders/PathNaming`, `NfoWriter`): folder and file names
  `<Name> (<Year>) [tmdbid-<id>]` and the `[xxxid-...]` tags, plus the `movie.nfo`/`tvshow.nfo`/episode `.nfo` files, are
  the forms Jellyfin's scanner and NFO reader use to match titles to providers. Recheck the provider tag syntax and the
  NFO elements Jellyfin reads (`uniqueid`, season/episode numbers).
- **`.strm` handling** (`Application/Features/Relay/RelayUrl`, `Data.Files/Placeholders/PlaceholderWriter`, and
  `Adapters/JellyfinPlaceholderLookup` which reads `IsShortcut`/`ShortcutPath`): a placeholder is a `.strm` file holding a
  loopback relay URL, and Jellyfin must keep treating such items as shortcuts with a remote source. Recheck that
  `.strm` items are still scanned, still expose their URL, and that a client bitrate limit is still not applied to them
  (that last point is what `PlaybackInfoFilter` works around).
- **Library merge setting**: episodes of a shared series join the user's own series only while Jellyfin's "merge series
  spread across multiple folders" library option (LibraryOptions.EnableAutomaticSeriesGrouping, read in
  Adapters/JellyfinAdminLibraries.cs) exists and is on; the admin page warns when it is off.

## Build and test

Requires the .NET 10 SDK (and Python 3 for the release scripts).

```powershell
dotnet build JellyShare.slnx -c Release
dotnet test JellyShare.slnx -c Release
py -m unittest discover -s build -p "test_*.py"
```

The build must stay at 0 warnings (warnings are errors). Tests use xUnit with Shouldly assertions.

## Two-server harness

Two Jellyfin 12.4 servers in Docker, with the plugin from `Source/Infrastructure/Platform/Jellyfin/bin/Release/net10.0/`
mounted as a dev plugin:

| Server | URL | Notes |
| --- | --- | --- |
| alice | `http://localhost:8101` | libraries "Movies" and "Shows" |
| bob | `http://localhost:8102/jellyfin` | same libraries; server BaseUrl is `/jellyfin` |

Admin login on both: `admin` / `admin` (throwaway harness credentials).

1. Build the plugin: `dotnet build JellyShare.slnx -c Release`.
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

7. End-to-end suite (pairs both ways, syncs, then checks merge/dedupe, loop guard, relay, playback, limits, fallback; undoes
   what it changes; about 3-5 minutes): `py harness/e2e.py` prints one line per check and `ALL PASS`.
   `py harness/e2e.py --deploy` first copies the Release DLL into both containers and restarts them.

After rebuilding the plugin, `docker compose -f harness/docker-compose.yml restart` to load the new DLL.
To start over from scratch: `docker compose -f harness/docker-compose.yml down`, delete `harness/config/`, repeat steps 3-4.

The image tag is `jellyfin/jellyfin:12.4` because `12.4.0` does not exist on Docker Hub.

Placeholder names in the harness look doubled, for example
`Tears of Steel (2012) [tmdbid-133701] (2012) [tmdbid-133701]`. That comes from the harness, not from JellyShare: the
servers run without metadata downloaders or NFO files, so Jellyfin keeps the whole folder name, year and id tag
included, as the item's Name (`py harness/list_items.py bob` shows `'Tears of Steel (2012) [tmdbid-133701]'` for bob's
own file). The consumer then names the placeholder folder `<Name> (<Year>) [tmdbid-<id>]` from what the provider sends.
On a server with metadata (or NFO files) the Name is the plain title and the folder is `Tears of Steel (2012) [tmdbid-133701]`.

## Releasing

`.github/workflows/ci.yml` builds (warnings are errors) and runs all tests and the `build/` script tests on every push
and pull request to `main`. The template's OFA-TECH reusable Docker/Swarm pipeline does not apply: JellyShare is a
Jellyfin plugin shipped as a zip through a plugin repository manifest, not a service.

Releases are built by GitHub Actions (`.github/workflows/release.yml`) when a tag `v<version>` is pushed. The version
has four numbers and becomes the plugin's assembly version:

```powershell
git tag v0.1.0.1
git push origin v0.1.0.1
```

The workflow (the tagged commit must be on `main`):

1. runs `dotnet test` and the `build/` script tests;
2. runs `dotnet publish -c Release` with the version from the tag;
3. zips only the `JellyShare.*.dll` artifacts listed in `build.yaml` (`JellyShare.Jellyfin`, `.Application`, `.Domain`,
   `.Data.Peers`, `.Data.Files`, `.Media`; Jellyfin already ships the rest, and
   writes `meta.json` itself on install) as `jellyshare_<version>.zip`;
4. takes `manifest.json` from the latest release (the seed on `main` for the very first release) and adds the version
   with `build/update_manifest.py`: version, changelog (commit subjects since the previous tag), `targetAbi`
   `12.0.0.0`, `sourceUrl` of the release asset, lowercase MD5 `checksum` of the zip (what Jellyfin verifies) and a UTC
   `timestamp`. Re-running for the same version replaces its entry;
5. creates the GitHub Release `v<version>` with the zip and `manifest.json` (a re-run replaces the assets).

The plugin repository URL `https://github.com/OFA-Tech/JellyShare/releases/latest/download/manifest.json` always
redirects to the newest release's manifest, which lists every version. Nothing is pushed to a branch, so the ruleset
on `main` (changes only through reviewed pull requests) stays untouched. Drafts and pre-releases are never "latest".
The catalog image is `assets/jellyshare.png` on `main` (source: `assets/jellyshare.svg`), linked by `imageUrl`
through raw.githubusercontent.com. Jellyfin downloads it into the
plugin folder once and keeps it while a file of that name exists, so a changed icon needs a new file name to reach
servers that already have it.

Try the whole pipeline without GitHub: `./build/package.ps1 -Version 0.1.0.1 -BaseUrl http://host.docker.internal:8000`
builds the zip and a copy of the manifest in `dist/`; serve `dist/` with `python -m http.server` and add that
`manifest.json` as a plugin repository (nothing is committed or tagged).
