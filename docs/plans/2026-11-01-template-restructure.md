# JellyShare — restructure to the OFA-TECH WebApi template

**Goal:** same product, same behaviour, new shape. Follow `D:\Development\PERSONAL\ProjectTemplate-WebApi`
(read its README, Directory.Build.props, Directory.Packages.props, .editorconfig, Tests/ArchitectureTests)
so that a Jellyfin update only touches **one** project: `JellyShare.Jellyfin`.

**Spec (binding for behaviour):** `docs/specs/2026-10-31-jellyfin-federation-plugin-design.md`,
plus `docs/spike-results.md` (plan corrections 1–21). Nothing user-visible changes except names listed below.

## Global constraints
- Behaviour-preserving refactor. Every existing test keeps an equivalent (moved/renamed is fine, dropped is not).
  `harness/e2e.py` must print `ALL PASS` at the end of the work.
- Never tokens/API keys/passwords in URLs, .strm, logs or commits. Plugin writes only in its placeholder roots + data dir.
- Only `JellyShare.Jellyfin` (and its tests) may reference `Jellyfin.*`/`MediaBrowser.*` packages or namespaces.
  Only `JellyShare.Jellyfin` may use `Microsoft.AspNetCore.*` (controllers, filters, IActionResult, HttpContext).
  Enforced by architecture tests (NetArchTest.Rules, as in the template).
- No new third-party **runtime** dependencies in shipped DLLs (Jellyfin loads the plugin folder; extra DLLs/versions
  conflict). `Microsoft.Extensions.*` abstractions are referenced with `ExcludeAssets="runtime"` (or equivalent) —
  Jellyfin's host provides them. The release zip contains only `JellyShare.*.dll`.
- Wire protocol unchanged: routes `JellyShare/v1/...`, header `X-JellyShare-Token`, `.jellyshare` marker,
  data file names, plugin GUID `8a55d113-5072-47cf-9827-f3c49f1c3971`, plugin name `JellyShare`, targetAbi 12.0.0.0.
- Repo: `D:\Development\OFA-TECH\JellyShare` (remote OFA-Tech/JellyShare), branch `refactor/template-layout`.
  Do NOT push. GitHub URLs become `OFA-Tech/JellyShare` (manifest raw URL on main, release assets).

## Target layout (names are binding)
```
JellyShare.slnx
global.json  Directory.Build.props  Directory.Build.targets  Directory.Packages.props  .editorconfig  Nuget.config  .gitattributes
Source/
  Domain/                      JellyShare.Domain        -> (no project refs)
  Application/                 JellyShare.Application   -> Domain
  Infrastructure/
    Api/Data.Peers/            JellyShare.Data.Peers    -> Domain            (outbound peer HTTP client)
    Data/Data.Files/           JellyShare.Data.Files    -> Domain            (atomic files, json stores, ledger, placeholder writer)
    Platform/Media/            JellyShare.Media         -> Domain            (ffmpeg process runner)
    Platform/Jellyfin/         JellyShare.Jellyfin      -> all of the above  (THE plugin entry DLL + composition root)
Tests/
  UnitTests/                   JellyShare.UnitTests     (Domain, Application, Data.*, Media)
  JellyfinTests/               JellyShare.JellyfinTests (everything that needs Jellyfin/ASP.NET types: controllers, filter, adapters, HTTP tests)
  ArchitectureTests/           JellyShare.ArchitectureTests
build/  .github/workflows/release.yml  harness/  docs/
```
Namespaces = assembly name + folder (e.g. `JellyShare.Application.Features.Sync`), file-scoped. Infrastructure group
folders (`Api/`, `Data/`, `Platform/`) are not part of namespaces (as in the template).

## What goes where
- **Domain**: protocol DTOs (`Models/Protocol`), plugin settings as plain POCOs (`Models/Configuration`:
  `JellyShareSettings`, `PeerShare`, `RemotePeer` — no Jellyfin base class), admin request/response DTOs, all ports
  under `Interfaces/<Area>/I*.cs` (ILocalItemSource, ILocalLibrary, ILocalItemEvents, IAdminLibraries,
  IEncoderSettingsSource, IServerIdentity, IScanReporter, IRelayBase, IPlaceholderLookup, ISettingsProvider/ISettingsStore
  (was IConfigProvider/IConfigSaver), IPeerClient, IFfmpegRunner, stores...), `Results/{Result,Error}` (template shape),
  messaging interfaces (`ICommand`, `IQuery`, handlers, `ISender`) copied from the template, keys/merge logic
  (`MediaKey`, `MergeIndex`) as domain logic, enums, `Interfaces/Modules/IInfrastructureModule` (template shape, adapted:
  context = settings snapshot + data dir).
- **Application**: use cases. `Features/<Area>/` — Sync (SyncService, LocalIndex, watcher logic, presence sweep),
  Catalog (CatalogBuilder, ShareTokenService), Streaming (StreamLimiter, CappedStreamArgs, capped transcode orchestration
  WITHOUT IActionResult — return a plain "stream plan"/writer delegate; the HTTP result type lives in Jellyfin),
  Relay (SourceSelector, RelaySessions, RelayUrl, RelayBase.Resolve), Placeholders (planner, NfoWriter, PathNaming,
  PlaceholderPaths, LocalSeries), Admin (the logic now inside AdminController: validation, copy-on-write updates,
  token handling → command/query handlers returning `Result`), Peers (ping/test). `Dispatching/Sender` (template's
  reflection registration), `Behaviors/` — Logging + Validation only (no telemetry/caching/transaction/idempotency;
  no FluentValidation/Mapster unless shipped DLL rule allows — prefer hand-written validators), `Setup/AddApplicationSetup`.
  `TimeProvider` stays the clock abstraction.
- **Infrastructure modules**: each has `Modules/<X>Module.cs` (IInfrastructureModule) and `Setup/Add<X>Setup.cs`;
  `JellyShare.Jellyfin` has `Modules/InfrastructureModuleLoader` with an explicit list (template pattern).
- **JellyShare.Jellyfin** (presentation + Jellyfin adapters + composition root): `Plugin.cs`,
  `PluginServiceRegistrator` → calls `AddDomainSetup/AddApplicationSetup/AddModulesSetup/AddJellyfinSetup`,
  `Configuration/PluginConfiguration : BasePluginConfiguration` (XML-serialized by Jellyfin) + adapter mapping it to
  `JellyShareSettings` (ISettingsProvider/ISettingsStore), `Controllers/V1/{Federation,Relay,Admin}Controller` (thin:
  auth/HTTP mapping, call ISender/services, map `Error` → status codes exactly as today), `Filters/PlaybackInfoFilter` +
  target check, `Tasks/SyncTask`, `Adapters/Jellyfin*` (library, item source, events, admin libraries, encoder settings,
  server identity, scan reporter, relay base, placeholder lookup), `Web/configPage.{html,js}` embedded resources.
- Assembly/plugin naming: entry DLL `JellyShare.Jellyfin.dll`. Jellyfin names the plugin data folder/config XML after the
  assembly; that is acceptable (unreleased) — update harness paths and docs.

## Build & quality (template rules)
- Copy template `Directory.Build.props` (Product `JellyShare`), `.editorconfig`, `global.json`, `Nuget.config`,
  `.gitattributes`; central package management with only the packages we use (Jellyfin.Controller/Model 12.0.0 only
  in JellyShare.Jellyfin with `ExcludeAssets=runtime`; analyzers NetAnalyzers + SonarAnalyzer as GlobalPackageReference;
  tests: xunit 2.9.3, Shouldly, NSubstitute, NetArchTest.Rules, Microsoft.NET.Test.Sdk, coverlet).
- `TreatWarningsAsErrors` on: fix warnings properly; suppress only with a justified `[SuppressMessage]` or project
  NoWarn when the rule is wrong for a Jellyfin plugin (document each in Directory.Build.props comments-free style:
  list in the plan/report). `InvariantGlobalization` does not apply to a class library — omit.
- Tests: Shouldly assertions (convert from xUnit Assert), `sealed *Tests` classes, `Method_Behaviour_WhenCondition`
  names where renaming is cheap (don't churn names just for style if it loses meaning).
- Architecture tests: layer dependencies (Domain → nothing; Application → Domain; Infra modules → Domain only;
  only Jellyfin → Jellyfin/MediaBrowser/AspNetCore), contracts (interfaces in Domain), setup-file naming convention,
  namespaces match folders.

## Phases (one implementer + review each; commit per logical step)
1. **Scaffold + move**: build files, slnx, folders; move ALL current code into `JellyShare.Jellyfin` and tests into
   `Tests/JellyfinTests` with new namespaces/assembly names; release/build scripts, harness (plugin dir/DLL name), README
   updated. Green build + all 643 tests + e2e ALL PASS. (Pure rename/move — no logic change.)
2. **Domain + Application extraction**: settings POCO split from BasePluginConfiguration; move pure code + ports to
   Domain/Application; ASP.NET leaks removed from Application (IPlaceholderLookup without HttpContext, capped stream
   without IActionResult); tests split into UnitTests vs JellyfinTests; ArchitectureTests added.
3. **Infrastructure modules + dispatcher**: Data.Peers, Data.Files, Media projects with Module/Setup; module loader;
   Result/Error + Sender + handlers for admin and federation request/response use cases; thin controllers.
4. **Template strictness + docs**: analyzers/TreatWarningsAsErrors clean, Shouldly conversion, editorconfig clean,
   release workflow + build scripts (zip all JellyShare.*.dll, build.yaml artifacts), README (layout, "updating for a new
   Jellyfin version: only touch Source/Infrastructure/Platform/Jellyfin"), e2e ALL PASS, harness docs.
