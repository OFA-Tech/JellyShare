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
