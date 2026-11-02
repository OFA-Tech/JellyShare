# Account Peers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the admin add a friend's Jellyfin server as a peer with a normal Jellyfin username/password (no JellyShare needed on the friend's side), with a per-peer stream limit that refuses or stops the oldest playback.

**Architecture:** A new peer kind inside the existing pipeline. `Data.Peers` gets an `AccountPeerClient` that signs in with Jellyfin's API and maps the friend's library into the existing `CatalogResponse`; a router `IPeerClient` picks the client by `RemotePeer.Kind`, so sync, matching, placeholders and relay stay one code path. The stream limit lives in `Application` (`StreamLimitGate`) and is applied in the relay (skip full peers) and in the PlaybackInfo filter (refuse or stop the oldest).

**Tech Stack:** .NET 10, Jellyfin 12 plugin API (`Jellyfin.Controller` 12.0.0), xUnit + Shouldly, Python harness (`harness/`, Jellyfin `12.4` containers alice :8101 and bob :8102/jellyfin).

**Spec:** `docs/specs/2026-11-02-account-peers-design.md`

## Global Constraints

- Only `JellyShare.Jellyfin` may reference Jellyfin assemblies (architecture tests enforce this).
- Build has 0 warnings (warnings are errors); follow the existing analyzers and XML-doc style.
- Never store the password. Never put an access token or password in a URL, `.strm` file, log line, exception message or admin response. Tokens travel only in headers.
- Redirects are never followed for peer requests (the named client `JellyShare.Peer` already sets `AllowAutoRedirect = false`); account requests use the same client.
- Token peers keep working unchanged; existing saved settings (no `Kind`) load as token peers.
- Defaults: `MaxStreams = 1`, `LimitAction = Refuse`.
- Jellyfin auth header for account requests: `Authorization: MediaBrowser Client="JellyShare", Device="JellyShare", DeviceId="jellyshare-<peer id N format>", Version="<assembly version>"` plus `, Token="<access token>"` once signed in.
- Error strings for an account sign-in failure start with `sign-in failed` (the admin page keys on it).
- Tests first, one commit per task, Conventional Commit subjects.

## Review Focus

1. Friend changed the password → every request answers 401: sync records `sign-in failed`, titles stay (7-day grace), playback falls back to other sources, admin page shows it. (Task 2 test `Account_401_IsSignInFailed`, Task 3 status test.)
2. Friend's server also runs JellyShare: its own placeholder items (non-`File` sources) must not be offered back to us. (Task 2 test `Account_SkipsNonFileAndVirtualItems`.)
3. Large libraries: catalog must page (`StartIndex`/`Limit`) and not load one giant response. (Task 2 test `Account_PagesItems`.)
4. Two titles requested at once from a peer with `MaxStreams = 1`: the second must be refused (or stop the first) — but seeks/range requests of the playback already running must never count as a new stream. (Task 4 tests.)
5. Stop-oldest when the oldest playback belongs to a client that cannot be reached: the slot is still released so the new viewer plays. (Task 5 test `StopOldest_ReleasesSlotEvenIfStopFails`.)

---

### Task 1: Peer kind and limit fields

**Files:**
- Create: `Source/Domain/Enums/PeerKind.cs`, `Source/Domain/Enums/StreamLimitAction.cs`
- Modify: `Source/Domain/Models/Configuration/RemotePeer.cs`, `Source/Application/Features/Peers/UpdatePeer.cs`, `Source/Application/Features/Peers/ReorderPeers.cs`, `Source/Domain/Models/Admin/AdminDtos.cs` (`PeerView`), `Source/Application/Features/Admin/AdminRules.cs` (`ToView`)
- Test: `Tests/UnitTests/Peers/RemotePeerTests.cs` (new), existing admin/reorder tests, `Tests/JellyfinTests` configuration round-trip (add next to existing `PluginConfiguration` tests)

**Interfaces:**
- Produces: `enum PeerKind { Token = 0, Account = 1 }`; `enum StreamLimitAction { Refuse = 0, StopOldest = 1 }`.
- Produces on `RemotePeer`: `PeerKind Kind` (default `Token`), `string Username` (default empty; account peers only), `string AccountUserId` (friend-side user id, default empty), `int MaxStreams` (default 1), `StreamLimitAction LimitAction` (default `Refuse`), `RemotePeer Copy()` (shallow `MemberwiseClone`). `Token` holds the Jellyfin access token for account peers.
- Produces on `PeerView` (appended, positional record): `PeerKind Kind, string? Username, int MaxStreams, StreamLimitAction LimitAction, bool SignInFailed` (`SignInFailed` = kind is Account and `LastError` starts with `sign-in failed`).

- [ ] **Step 1: Write failing tests**
  - `RemotePeer_Defaults`: `new RemotePeer()` has `Kind == Token`, `MaxStreams == 1`, `LimitAction == Refuse`, `Username == ""`.
  - `RemotePeer_Copy_CopiesEveryProperty`: set every public property to a non-default value, `Copy()` returns a different instance whose properties (compared by reflection over all public instance properties) are equal — this guards future fields.
  - `UpdatePeer_KeepsAccountFields` and `ReorderPeers_KeepsAccountFields`: an account peer (Username, AccountUserId, MaxStreams 3, StopOldest) keeps those after update/reorder.
  - `ToView_AccountPeer`: `SignInFailed` true when `LastError = "sign-in failed: HTTP 401"`; view never exposes `Token`.
  - Jellyfin test: XML round-trip of `PluginConfiguration` with an account peer keeps Kind/MaxStreams/LimitAction; an XML without those elements loads as `Token`, 1, `Refuse`.
- [ ] **Step 2:** Run `dotnet test JellyShare.slnx -c Release` → the new tests fail to compile/fail.
- [ ] **Step 3:** Implement. `UpdatePeer` and `ReorderPeers` build their copy-on-write peer with `current.Copy()` then set the changed fields (no hand-listed property copies).
- [ ] **Step 4:** Run the full test command → all pass, 0 warnings.
- [ ] **Step 5:** Commit `feat: peer kind and stream limit fields`.

### Task 2: Account peer client (Jellyfin API)

**Files:**
- Create: `Source/Domain/Interfaces/Peers/IAccountSignIn.cs`, `Source/Domain/Models/Peers/AccountSignInResult.cs`, `Source/Infrastructure/Api/Data.Peers/Clients/AccountPeerClient.cs`, `Source/Infrastructure/Api/Data.Peers/Clients/PeerClientRouter.cs`, `Source/Infrastructure/Api/Data.Peers/Clients/JellyfinApiDtos.cs` (internal DTOs for the few Jellyfin JSON fields used)
- Modify: `Source/Domain/Interfaces/Peers/IPeerClient.cs` (`OpenStreamAsync` takes the `CatalogItem`), `Source/Infrastructure/Api/Data.Peers/Clients/PeerClient.cs`, `Source/Infrastructure/Api/Data.Peers/Setup/AddDataPeersSetup.cs`, `Source/Infrastructure/Platform/Jellyfin/Controllers/V1/RelayController.cs` (call site only)
- Test: `Tests/UnitTests/Peers/AccountPeerClientTests.cs`, `Tests/UnitTests/Peers/PeerClientRouterTests.cs`, update `PeerClientTests`/`RelayControllerTests` for the new `OpenStreamAsync` signature

**Interfaces:**
- Consumes: Task 1 `RemotePeer.Kind/Username/AccountUserId/Token`.
- Produces: `Task<HttpResponseMessage> IPeerClient.OpenStreamAsync(RemotePeer peer, CatalogItem item, string? range, CancellationToken ct)` (token client uses `item.Id`).
- Produces: `interface IAccountSignIn { Task<AccountSignInResult> SignInAsync(string baseUrl, string username, string password, Guid peerId, CancellationToken ct); Task SignOutAsync(string baseUrl, string accessToken, Guid peerId, CancellationToken ct); }`
- Produces: `sealed record AccountSignInResult(bool Ok, string? AccessToken, string? UserId, string? ServerName, string? Error)`; `Error` texts: `"sign-in failed: wrong username or password"` (401), `"sign-in failed: HTTP <code>"`, `"connection failed: …"`, `"timeout"`, `"invalid base url"`, `"HTTP 3xx (redirects are not followed)"`. Never contains the password or token.
- Produces: `AccountPeerClient : IPeerClient, IAccountSignIn` and `PeerClientRouter : IPeerClient` (delegates by `peer.Kind`; `PingAsync` goes to the token client). DI: `IPeerClient` → router, `IAccountSignIn` → the account client singleton.

Jellyfin endpoints (verify against harness bob, Jellyfin 12.4, before relying on them; record any difference in `docs/spike-results.md`):
- Sign in: `POST {base}/Users/AuthenticateByName` body `{"Username":…,"Pw":…}` → `AccessToken`, `User.Id`, `ServerId`; server name from `GET {base}/System/Info/Public` (`ServerName`).
- Sign out: `POST {base}/Sessions/Logout` with the token header.
- Policy: `GET {base}/Users/Me` → `Policy.RemoteClientBitrateLimit` (0 = none); cached in memory per peer id for 1 hour.
- Catalog: `GET {base}/Items?userId=<AccountUserId>&Recursive=true&IncludeItemTypes=Movie,Episode&Fields=ProviderIds,Overview,MediaSources,DateCreated&EnableImages=true&StartIndex=<n>&Limit=500` until `StartIndex >= TotalRecordCount`; series ids from `GET {base}/Items?userId=…&Recursive=true&IncludeItemTypes=Series&Fields=ProviderIds` (same paging).
- Image: `GET {base}/Items/{id}/Images/{type}` (same 32 MiB cap as `PeerClient`).
- Stream: `GET {base}/Videos/{id}/stream?static=true` with `Range` forwarded. When the cached bitrate limit is > 0 and `item.Media.BitRate` is above it: `GET {base}/Videos/{id}/stream?static=false&container=mkv&videoCodec=h264&audioCodec=aac&videoBitRate=<limit − 192000>&audioBitRate=192000&deviceId=jellyshare-<peer id>` without `Range`. `// shortcut: transcoded account streams cannot seek; upgrade to HLS if owners use bitrate limits a lot.`

Mapping to `CatalogItem`: `Id`, `Type` (`Movie`/`Episode` only), `Name`, `Year`=`ProductionYear`, `ProviderIds`, `SeriesName`, `SeriesProviderIds` (from the series query by `SeriesId`), `SeasonNumber`=`ParentIndexNumber`, `EpisodeNumber`=`IndexNumber`, `RunTimeTicks`, `Overview`, `Media` from the first media source (`Container`, `Bitrate`, video stream codec/width/height, audio language/codec/channels/`DisplayTitle`, subtitle language/codec/`DisplayTitle`), `ImageTypes` = keys of `ImageTags` plus `Backdrop` when `BackdropImageTags` is non-empty, `DateModified` = `DateCreated`, `SeriesId`. Skip items with `LocationType == "Virtual"`, without media sources, or whose first media source `Protocol` is not `File`. `ServerName` from `/System/Info/Public`, `ProtocolVersion` = `ProtocolInfo.Version`, `Etag` = lowercase hex SHA-256 of the serialized item list; when it equals the `etag` argument return `NotModified`. Any 401 → `PeerFetchResult` error `"sign-in failed: HTTP 401"`.

- [ ] **Step 1: Write failing tests** (use the existing `FakeHandler`):
  - `Account_SignIn_PostsCredentialsInBodyAndReturnsToken`: request URL has no username/password/token; body JSON has `Username`/`Pw`; `Authorization` header starts with `MediaBrowser ` and has `DeviceId="jellyshare-<id N>"`; result `Ok`, token, user id, server name.
  - `Account_SignIn_401_IsWrongPassword` → `Error == "sign-in failed: wrong username or password"`, error text never contains the password.
  - `Account_SignIn_Redirect_IsRefused` (302 → error starts `HTTP 302`, no second request).
  - `Account_Catalog_MapsMoviesAndEpisodes`: fixture JSON with one movie and one episode (series query returns `Tvdb` ids) → mapped fields as above, `SeriesProviderIds["Tvdb"]` filled, `ImageTypes` contains `Primary` and `Backdrop`.
  - `Account_SkipsNonFileAndVirtualItems`: items with `Protocol:"Http"`, `LocationType:"Virtual"`, no `MediaSources` are absent.
  - `Account_PagesItems`: `TotalRecordCount` 1200 → three item requests with `StartIndex` 0/500/1000.
  - `Account_SameContent_IsNotModified`: second fetch with the first result's `Etag` → `NotModified`.
  - `Account_401_IsSignInFailed`: catalog 401 → error `sign-in failed: HTTP 401`.
  - `Account_TokenOnlyInHeader`: for catalog, image and stream requests the URL never contains the token; the header has `Token="…"`.
  - `Account_Stream_StaticWithRange` and `Account_Stream_TranscodesAboveBitrateLimit` (Users/Me policy limit 2000000, item bitrate 8000000 → `static=false`, `videoBitRate=1808000`, no `Range` header).
  - `Router_DispatchesByKind`: token peer → token client, account peer → account client; `PingAsync` → token client.
- [ ] **Step 2:** Run tests → fail.
- [ ] **Step 3:** Implement; reuse `PeerUri.Build` for URLs, `PeerClient.HttpClientName` client, the same timeouts/size caps and redaction approach as `PeerClient`.
- [ ] **Step 4:** Run full tests → pass, 0 warnings. Then verify the endpoints for real: on harness bob (`http://127.0.0.1:8102/jellyfin`, admin/admin) sign in via a scratch script and fetch one catalog page; note results in `docs/spike-results.md` (append a dated "Account peers" section; no secrets).
- [ ] **Step 5:** Commit `feat: account peer client for the Jellyfin API`.

### Task 3: Admin API for account peers

**Files:**
- Modify: `Source/Domain/Models/Admin/AdminDtos.cs` (`AddPeerRequest`, `UpdatePeerRequest`, `TestPeerRequest`, `TestPeerResponse` unchanged), `Source/Domain/Models/Requests/Peers/{AddPeerCommand,UpdatePeerCommand,TestPeerQuery}.cs`, `Source/Application/Features/Peers/{AddPeer,UpdatePeer,TestPeer,PeerErrors}.cs`, `Source/Infrastructure/Platform/Jellyfin/Controllers/V1/AdminController.cs`
- Test: `Tests/UnitTests/Peers/*` (existing handler tests + new account cases), `Tests/JellyfinTests` controller tests if they bind request bodies

**Interfaces:**
- Consumes: Task 1 fields, Task 2 `IAccountSignIn`.
- Produces: request bodies gain `PeerKind? Kind, string? Username, string? Password, int? MaxStreams, StreamLimitAction? LimitAction` (all optional; `Kind` null = `Token`). Commands carry the same.
- Produces: `PeerErrors.MissingUsername`, `PeerErrors.MissingPassword`, `PeerErrors.InvalidMaxStreams` (allowed 1–20), `PeerErrors.SignInFailed(string message)` (validation type, message is the sign-in error).

Rules:
- Add (account): validate name, URL, username, password, MaxStreams; sign in; on failure return `SignInFailed`; on success store `Kind=Account`, `Username`, `AccountUserId`, `Token`=access token, limits. Password goes nowhere else.
- Update (account): name/URL/enabled/MaxStreams/LimitAction editable; a non-empty `Password` signs in again (with the request's username or the stored one) and replaces token + user id; a changed URL without a password is refused (`"The address was changed. Enter the password again."`), mirroring the token rule. Clears nothing else.
- Test (account): with a password → sign in, fetch the catalog, report titles, then `SignOutAsync` (best effort); without a password and a saved peer at the same URL → fetch with the saved token. Messages never contain password or token. A token peer test is unchanged.
- Kind cannot change on update (`PeerErrors.KindChange`).

- [ ] **Step 1: Write failing tests**: `AddAccountPeer_SignsInAndStoresTokenNotPassword` (saved settings serialized to string do not contain the password), `AddAccountPeer_SignInFails_SavesNothing`, `AddAccountPeer_MaxStreamsOutOfRange`, `UpdateAccountPeer_PasswordReSignsIn`, `UpdateAccountPeer_UrlChangeNeedsPassword`, `UpdatePeer_KindChange_IsRefused`, `TestAccountPeer_SignsOutAfterTest`, `TestAccountPeer_UsesSavedTokenForSameUrl`, `AddTokenPeer_Unchanged` (existing tests keep passing).
- [ ] **Step 2:** Run → fail.
- [ ] **Step 3:** Implement. `UpdatePeerCommandHandler` becomes async-capable (it currently returns `Task.FromResult`); keep the settings lock only around the read-modify-save, never around the network call (sign in first, then lock and re-check the peer still exists).
- [ ] **Step 4:** Run full tests → pass.
- [ ] **Step 5:** Commit `feat: add, update and test account peers`.

### Task 4: Stream limit core and relay

**Files:**
- Create: `Source/Application/Features/Relay/StreamLimitGate.cs`
- Modify: `Source/Application/Features/Relay/RelaySessions.cs`, `Source/Infrastructure/Platform/Jellyfin/Controllers/V1/RelayController.cs`
- Test: `Tests/UnitTests/Relay/RelaySessionsTests.cs`, `Tests/UnitTests/Relay/StreamLimitGateTests.cs`, `Tests/JellyfinTests/Controllers/RelayControllerTests.cs`

**Interfaces:**
- Produces on `RelaySessions`: `IReadOnlyList<string> ActiveTitles(Guid peerId)` (oldest start first; start = first `Touch` of that key on that peer), `void Release(string key)`. `Touch` keeps its signature; switching peers restarts the start time.
- Produces: `static class StreamLimitGate`:
  - `bool HasRoom(RemotePeer peer, string key, RelaySessions sessions)` — token peers always true; account peers true when `key` is already active on that peer or `ActiveTitles(peer.Id).Count < peer.MaxStreams`.
  - `StreamDecision Decide(IReadOnlyList<RelaySource> ordered, string key, RelaySessions sessions)` → `sealed record StreamDecision(StreamDecisionKind Kind, RemotePeer? Peer, string? StopKey)`; `enum StreamDecisionKind { Play, Refuse, StopOldest }`. `Play` when any source has room (or there are no sources — the relay reports those itself); otherwise, the first source (in order) whose peer has `LimitAction == StopOldest` gives `StopOldest` with `StopKey = ActiveTitles(peer.Id)[0]`; otherwise `Refuse` with the first source's peer.
- Relay: sources without room are skipped before any request; if every source was skipped for lack of room, answer 503 `{"error":"stream limit reached"}`.

- [ ] **Step 1: Write failing tests**: `ActiveTitles_OrdersByStart`, `Release_FreesSlot`, `Touch_SameKey_DoesNotRestartStart`, `HasRoom_TokenPeerAlways`, `HasRoom_AccountFull_ButSameTitle_IsTrue` (seeks of the running playback), `Decide_UsesOtherSourceWithRoom`, `Decide_RefuseByDefault`, `Decide_StopOldest_PicksOldestKey`, relay `Play_AccountPeerFull_SkipsToNextSource`, relay `Play_AllFull_Is503StreamLimit`.
- [ ] **Step 2:** Run → fail. **Step 3:** Implement. **Step 4:** Run full tests → pass.
- [ ] **Step 5:** Commit `feat: per-peer stream limit for account peers in the relay`.

### Task 5: Refuse or stop the oldest at PlaybackInfo

**Files:**
- Create: `Source/Domain/Interfaces/Relay/IPlaybackStopper.cs`, `Source/Infrastructure/Platform/Jellyfin/Adapters/JellyfinPlaybackStopper.cs`
- Modify: `Source/Infrastructure/Platform/Jellyfin/Filters/PlaybackInfoFilter.cs`, Jellyfin setup registration (`AddJellyfinSetup.cs`)
- Test: `Tests/JellyfinTests/Filters/PlaybackInfoFilterTests.cs`, `Tests/JellyfinTests/Adapters/JellyfinPlaybackStopperTests.cs`

**Interfaces:**
- Consumes: Task 4 `StreamLimitGate.Decide`, `RelaySessions.Release`; existing `IPlaceholderLookup.Find(Guid)` (gives `CanonicalKey`), `IConsumerIndex.Find(key)`, `SourceSelector.Order`.
- Produces: `interface IPlaybackStopper { Task<int> StopTitleAsync(string canonicalKey, string message, CancellationToken ct); }` — returns the number of Jellyfin sessions told to stop.
- `JellyfinPlaybackStopper`: for each `ISessionManager.Sessions` whose `NowPlayingItem` maps (via `IPlaceholderLookup.Find`) to `canonicalKey`: `SendMessageCommand(null, session.Id, new MessageCommand { Header = "JellyShare", Text = message, TimeoutMs = 10000 }, ct)` then `SendPlaystateCommand(null, session.Id, new PlaystateRequest { Command = PlaystateCommand.Stop }, ct)`; exceptions per session are logged and skipped. Verify a `null` controlling session id is accepted by Jellyfin 12 (read `SessionManager` source); if not, record in `docs/spike-results.md` and use the requesting session.

Filter rule (before the action, only for placeholders that resolve to a merged entry):
- `Play` → continue.
- `Refuse` → short-circuit with `context.Result = new OkObjectResult(new PlaybackInfoResponse { ErrorCode = PlaybackErrorCode.RateLimitExceeded })` and log `JellyShare: refused {Key}: peer {Peer} allows {Max} stream(s)` at Information.
- `StopOldest` → `StopTitleAsync(StopKey, $"Stopped: {peer.Name} allows {max} stream(s) at a time and another viewer started.")`, then `RelaySessions.Release(StopKey)` **even when 0 sessions were stopped or the call threw**, then continue.

- [ ] **Step 1: Write failing tests**: `PlaybackInfo_LimitReached_Refuse_ReturnsRateLimitExceeded`, `PlaybackInfo_LimitReached_StopOldest_StopsAndContinues`, `StopOldest_ReleasesSlotEvenIfStopFails`, `PlaybackInfo_TokenPeer_Unaffected`, `PlaybackInfo_NotPlaceholder_Unaffected`, stopper `StopTitle_OnlySessionsPlayingThatTitle`.
- [ ] **Step 2:** Run → fail. **Step 3:** Implement. **Step 4:** Run full tests → pass.
- [ ] **Step 5:** Commit `feat: refuse or stop the oldest playback when an account peer is full`.

### Task 6: Admin page

**Files:**
- Modify: `Source/Infrastructure/Platform/Jellyfin/Web/configPage.html`, `Source/Infrastructure/Platform/Jellyfin/Web/configPage.js`
- Test: harness Playwright check (extend `harness/admin_page.js`)

Behavior:
- The add-peer form gets a kind choice: "Invitation token" (current fields) or "Jellyfin account" (username, password, max streams 1–20 default 1, "When the limit is reached": "Refuse the new viewer" (default) / "Stop the oldest viewer"). Password field `type="password"`, `autocomplete="new-password"`, cleared after every submit.
- Peer list shows the kind, the username for account peers, `max N stream(s)`, and when `SignInFailed` a "Sign-in failed" badge with a "Sign in again" button that asks for the password and sends `PUT admin/peers/{id}` with it.
- Edit dialog for account peers: name, URL, enabled, max streams, limit action, optional new password.
- Test button works for both kinds.

- [ ] **Step 1:** Extend `harness/admin_page.js` to add an account peer on alice (bob, user from Task 7 setup), assert it appears with kind "Jellyfin account" and "max 1 stream", then delete it; it fails before the UI change.
- [ ] **Step 2:** Implement the UI. **Step 3:** Build, deploy to alice (see harness README / project memory: `docker cp` the Release DLLs, then restart), run the script → passes; take one screenshot to `harness/screens/account-peer.png`.
- [ ] **Step 4:** Commit `feat: account peers on the admin page`.

### Task 7: End-to-end, docs

**Files:**
- Modify: `harness/e2e.py` (new section), `harness/setup.py` if users are created there, `README.md`, `CHANGELOG.md`, `docs/spike-results.md`
- Test: `harness/e2e.py`

E2E section "account peers" (bob is the friend; alice consumes):
1. On bob create user `friend` with a password via the admin API, access to one library only.
2. On alice add an account peer to bob with `friend`, `MaxStreams=1`, `Refuse`; run sync; assert a bob-only title appears on alice with a placeholder and a title from the other library does not.
3. Assert no file under alice's placeholder roots and no line in alice's log contains the access token or the password.
4. Start playback of title A through alice (PlaybackInfo + stream request as in the existing e2e), then PlaybackInfo for title B → `ErrorCode == "RateLimitExceeded"`.
5. Switch the peer to `StopOldest`; PlaybackInfo for B succeeds and A's relay session is released.
6. Change `friend`'s password on bob; sync on alice → peer shows `SignInFailed`; sign in again with the new password through `PUT admin/peers/{id}` → sync ok.
7. Clean up the peer and the user.

- [ ] **Step 1:** Write the e2e section; run it against the current build → fails only where features are missing (should all pass once Tasks 1–6 are in).
- [ ] **Step 2:** Run `py harness/e2e.py` (both servers) → `ALL PASS`, twice.
- [ ] **Step 3:** README: new "Account peers" section (what the friend does, what you enter, the stream limit setting, bitrate limit and seek shortcut, "titles stay 7 days after sign-in fails", security notes: the stored access token is a full Jellyfin login for that account — the friend should give a dedicated user with only the libraries they want to share). CHANGELOG entry under Unreleased.
- [ ] **Step 4:** Commit `test: account peers end to end` and `docs: account peers`.
