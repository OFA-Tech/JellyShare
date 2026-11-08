# Share and receive by media type — design

Approved by the owner on 2026-11-08.

## Why

JellyShare shares movies and shows only. The owner's server also has Music (894 tracks, 386 albums,
303 artists), Audiobooks (62 MP3 chapter files), Podcasts (14 episodes in a "books" library), Ebooks
(1 EPUB), Collections (15) and Live TV (2 M3U tuners). He wants friends to share more than movies and
shows, and to choose **by media type** what he sends to each friend and what he takes from each
friend, with "everything" as the easy default.

## Goals

1. Media types, one list used everywhere: **Movies, Shows, Music, Audiobooks** (covers podcasts),
   **Ebooks, Collections, Live TV**. The last three are shown as "coming later" (not supported yet).
2. **Default sharing** on the share side: a type list, all types ticked to start.
3. **Each invitation (share):** "Use the default" (preselected for new invitations) or "Custom" with
   its own type ticks.
4. **Each peer received from** (token and account peers): "Accept everything they share" (default)
   or "Only" with type ticks.
5. **Music and Audiobooks/Podcasts** shared and received end to end, like movies and shows today:
   they appear inside the owner's own Music / Audiobooks libraries and play through the relay.

## Non-goals

- Ebooks, Collections, Live TV support (each its own later project; here only shown as "coming later").
- A global receive default (per peer only).
- Choosing by library (owner prefers types).
- Changing what an **account** friend sees on the owner's server: that stays the friend's Jellyfin
  user's library access. The page says so instead of showing ticks that do nothing.

## Anti-goals (a failure even if it technically works)

- An update changes what the owner already shares or receives.
- An older JellyShare peer (≤ 0.2.0.0) receives music/audiobook items it would treat as movies.
- A friend's music makes the owner's own artists or albums appear twice.
- The plugin writes into the owner's media folders (unchanged rule: only its own placeholder folders).

## Facts that shape it (owner's server, Jellyfin 12.4.0, surveyed 2026-11-08)

- Music library: items `MusicArtist`, `MusicAlbum`, `Audio`; MusicBrainz ids on about a third of
  tracks and most albums/artists; AudioDb ids on some.
- Audiobooks and Podcasts libraries are both `CollectionType=books`; their items are `AudioBook`
  (MediaType Audio) under `Folder`s, **no provider ids**. Jellyfin cannot tell a podcast episode from
  an audiobook chapter, hence one "Audiobooks" type.
- Ebooks: `Book` items (EPUB), no ids. A reader needs the file, not a stream → later project.
- Friend server (Cuts, Jellyfin 12.2.0) exposes only Movies, Shows, Collections to the owner's account.

## Approaches considered for music and audiobooks

- **A. Link files (`.strm`) in a Music / Audiobooks placeholder folder** added as an extra path of
  the owner's own library, exactly like movies today. Friend's albums appear next to the owner's and
  play through the relay. Risk: a link file has no tags, and Jellyfin normally reads artist/album from
  tags, so it must work them out from the folder layout (`Artist/Album/NN Track.strm`) plus
  `artist.nfo` / `album.nfo`. **Chosen**: the only option that blends into the owner's library.
- **B. A Jellyfin channel per friend** (plugin `IChannel`): reliable metadata, but shows up as a
  separate Channels entry, never mixed into Music. **Fallback** if A fails the spike.
- **C. Copying files**: ruled out (disk use, duplicates the friend's library).

**Stop point:** the first build step is a spike on the harness servers. If Jellyfin 12.4 cannot build
proper artists, albums and track order from link files plus `.nfo` files (or cannot play an audio
`.strm` through the relay), stop and ask the owner whether to switch to B.

## Design

### Media types

A `MediaType` enum in Domain: `Movies, Shows, Music, Audiobooks, Ebooks, Collections, LiveTv`.
"Supported" today: Movies, Shows; after this project also Music, Audiobooks. Mapping from Jellyfin
item kinds lives in `JellyShare.Jellyfin` (Movie → Movies; Series/Season/Episode → Shows;
MusicArtist/MusicAlbum/Audio → Music; AudioBook → Audiobooks; Book → Ebooks; BoxSet → Collections).

### Settings (saved XML must stay backward compatible)

- `JellyShareSettings.DefaultShareTypes`: list of `MediaType`, default all seven.
- `PeerShare.UseDefaultTypes` (bool) + `PeerShare.Types` (list). New invitations: `UseDefaultTypes =
  true`. `LibraryIds` stays readable for the update path; new code stops using it for exposure.
- `RemotePeer.AcceptAllTypes` (bool, default true) + `RemotePeer.AcceptTypes` (list).
- Placeholder folders: `MusicPlaceholderPath`, `AudiobooksPlaceholderPath` next to the Movies/Shows
  ones, each with its "Add to library" button on the admin page (Music → a `music` library,
  Audiobooks → a `books` library).
- **Update path:** an existing share (no saved `UseDefaultTypes`) becomes Custom with the types of the
  libraries in its `LibraryIds` (by each library's collection type). The owner's shares cover Movies and
  Shows → no change. Existing peers become "Accept everything", and since an older provider only sends
  movies and shows, nothing changes. Note: a share that had one of two movie libraries is widened to
  all movie libraries; the CHANGELOG says so.
- "All"/"Everything" include types that become supported in later versions; "Custom"/"Only" do not.

### Between servers

- Effective share types = the share's Custom types, or the default when "Use the default".
- The receiver sends the types it wants with the catalog request (e.g. a `types` query value).
  The provider answers with (effective share types ∩ wanted types ∩ supported types).
- **A request without a type list (older receivers) gets only Movies and Shows.** This is what
  protects older peers.
- A provider that ignores the type list (older provider) only ever sends Movies/Shows; the receiver
  also filters locally by its accept list, so an "Only Movies" choice still holds.
- `CatalogItem` gains the fields music and audiobooks need (artist, album artist, album, track/disc
  number, album id for artwork, the item's media type). Exact names are the builder's call; the
  existing fields keep their names and meaning.
- Account peers: JellyShare asks the friend's Jellyfin for only the wanted item kinds
  (`IncludeItemTypes`).

### Receiving

- Placeholders for Music under the Music placeholder folder as `Artist/Album/…`, for Audiobooks as
  `Author-or-folder/Book/…`, with `.nfo` files as the spike shows Jellyfin needs. Playback goes
  through the existing relay (audio, no transcode cap changes in this project unless the spike shows
  one is needed).
- **"You already own it"**: movies and shows unchanged. Music: MusicBrainz recording id when both
  sides have it, else normalized artist + album + track title; a friend's album that the owner
  already has fully is not written; the owner's album missing some tracks gets only the missing
  tracks, and they must merge into the owner's album and artist (same keys / names as the owner's,
  copied into the `.nfo` like `tvshow.nfo` today). Audiobooks: normalized author/folder + book +
  file name.
- Unticking a type for a peer: that peer's placeholders of that type are removed at the next sync;
  watched status is kept (Jellyfin keys it by provider id / path as today). A received type with no
  folder set: the sync skips it and the admin page shows "set up the folder first".
- Peer priority, presence check, failure grace, stream limits and the relay are unchanged.

### Admin page

- Share tab: a "Default sharing" box with the type ticks; each invitation shows "Use the default" /
  "Custom" with ticks (ticks disabled while on default). Unsupported types are greyed and marked
  "coming later".
- Receive tab: each peer gets a row "Receive: (•) Everything they share ( ) Only: ☑ Movies ☑ Shows
  ☐ Music ☐ Audiobooks ☐ Ebooks" (ticks active only with "Only"). Account peers get the same row.
- Setup: Music and Audiobooks placeholder folders with their "Add to library" buttons and the same
  warnings as today.

### Tests

- Unit: settings update path (old XML → Custom by library type; peers → everything), type filtering
  on the provider (including the no-types-means-movies-and-shows rule) and on the receiver, music and
  audiobook matching, placeholder layout.
- End to end (harness): music and audiobook clips on bob (free/CC audio, generated or downloaded
  in the harness like the video clips); they appear merged into alice's Music and Audiobooks
  libraries, play through the relay, and disappear when the type is unticked; a catalog request
  without types returns only movies and shows; an "Only Movies" peer gets no music.

## Calls made after the builder check (controller's, not the owner's)

1. **Spike scope and partial results.** The spike gates Music and Audiobooks separately. It must show,
   on 12.4: an audio `.strm` plays through the relay; a friend's album the owner lacks appears as one
   album under the right artist with track order; a friend's artist the owner already has merges into
   the owner's artist (no second artist). If tracks of an album the owner **already has** can't be
   merged into it, JellyShare only writes albums the owner lacks entirely (never a second copy of an
   album) — no stop needed for that. Stop and ask the owner only if a whole type can't work with A
   (then B, the channel, is the question). Approach B is not planned unless that happens.
2. **Audio and the limits.** Audio is never sent through the capped (video) transcode: a share's
   bitrate cap does not apply to audio. For the stream limits (provider `MaxStreams`, account-peer
   `MaxStreams`, Refuse / Stop oldest) an audio "title" is the **album** (audiobooks: the book), so
   playing through an album counts as one stream.
3. **Share exposure is a pure type filter.** `LibraryIds` stops filtering once a share is migrated
   (owner chose types over libraries knowing one movie library can't be kept private from another).
   Library collection type → media types for the update path: `movies` → Movies, `tvshows` → Shows,
   `music` → Music, `books` → Audiobooks, anything else / null / mixed → Movies + Shows (what such a
   library exposes today, since only Movie and Episode items were ever served).
4. **Identity of items without ids.** Music: MusicBrainz track/recording id when present (the id
   normalizer accepts 36-character UUIDs), else a key derived from normalized artist + album + disc +
   track number + title. Audiobooks: normalized folder/author + book + file name. Such keys are used for
   cross-peer merge, priority and the relay lookup like movie ids; a rename on the provider shows up
   as remove + add. Items whose key can't be built (no title) are counted as "without usable ids".
5. **Account peers get audio too.** Type ticks map to `IncludeItemTypes`; audio streams use Jellyfin's
   audio stream endpoint. The harness e2e covers it with bob's friend user given access to music.
6. **Catalog cache/ETag** includes the effective type set; changing a peer's accept list clears that
   peer's saved ETag so the next sync is a full fetch.

## Left to the builder

- Exact music/audiobook folder layout and `.nfo` contents (from the spike).
- How the type list is sent between servers (query value name/format).
- New `CatalogItem` field names; how item kinds map to media types in detail.
- Whether `LibraryIds` is removed later (kept now for the update path).
