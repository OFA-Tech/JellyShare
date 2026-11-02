# Account peers — design

Agreed with the owner on 2026-11-02 (conversation; owner said "that's exactly it" and asked to implement).

## Intent

A friend can share their Jellyfin with you without installing JellyShare: they create a normal Jellyfin user for
you, you enter their address, username and password in JellyShare, and their movies and shows appear in your
Jellyfin next to your own, playing through your server. This is an addition: invitation-token peers stay as they are.

## Goals

- A second peer kind, **account peer**, next to token peers. Works with any Jellyfin 12 server; the friend needs no
  plugin.
- The friend's titles appear through the same placeholder files, matched by TMDB/TVDB ids. Own copy still wins; peer
  order still decides between peers.
- What the account sees is decided by the friend's Jellyfin user settings (libraries, parental rating). JellyShare
  also respects the account's remote bitrate limit.
- The password is used only to sign in and is never stored. Only Jellyfin's access token is kept, the same way share
  tokens are kept today. The access token never appears in URLs, `.strm` files, logs or admin responses.
- Playback goes through the existing hidden loopback relay. Watched status stays on our server.
- **Stream limit per account peer** (Jellyfin cannot limit concurrent streams of one login): `MaxStreams`, default 1,
  and `LimitAction`:
  - `Refuse` (default): the viewer gets Jellyfin's "rate limit exceeded" playback error.
  - `StopOldest`: the oldest playback from that peer gets a stop command (and a short message), the new one starts.
  - Before either, another source with the same title (another peer) is used.
- When sign-in stops working (password changed, account removed): titles stay (existing 7-day failure grace) but
  don't play; the admin page shows "sign-in failed" and lets the admin sign in again with the password.

## Non-goals

- EF Core / a JellyShare database (dropped for now: plugins share Jellyfin's EF Core 10.0.14, so other providers would
  be fragile).
- Syncing watched status to the friend's server.
- Per-user access rules on our side (all our users can play account-peer titles, as with token peers).

## Anti-goals

- Never write into media folders, never store the password, never put a token in a URL.
- Never let one account peer's failure break sync or playback of other peers.

## Approach (my call, owner did not object)

Account peers are a new kind inside the existing pipeline: a Jellyfin-API client in `Data.Peers` maps the friend's
library into the same `CatalogResponse` token peers produce, so sync, matching, placeholders, relay and settings stay
one code path. A router `IPeerClient` dispatches by peer kind. The stream limit is enforced on our side in two places:
the relay (skip an account peer that is full) and the PlaybackInfo filter (refuse or stop the oldest when no source
has room).

Alternatives dropped: a separate sync pipeline for accounts (duplicates matching/placeholders); enforcing the limit
only in the relay (the viewer would get a generic failure instead of a message, and stop-oldest needs the PlaybackInfo
moment).

## Calls I made

- `MaxStreams`/`LimitAction` are per account peer (each friend may allow a different number).
- Stream slots are counted per title: two of our users watching the same title from the same peer count as one slot
  (they share one relay session). Shortcut, documented.
- Titles above the account's remote bitrate limit are fetched as a transcoded progressive stream from the friend's
  server; such streams cannot seek. Shortcut, documented.
- Items the friend's server itself streams from elsewhere (non-`File` media sources, e.g. their own JellyShare
  placeholders) and virtual/missing items are skipped, so titles are never relayed twice.
