# STATE — LocoMP (implementation)

**Updated:** 2026-07-18 (M0 scaffold session) · This is the **implementation** memory (burst cadence, D8).
The **planning corpus** lives one level up at `../` (00–09, INDEX, research/) — strategic, kept private.
Cold-starting? Read `../CLAUDE.md` (hard rules) → this file → the current milestone in `../07-ROADMAP.md`.

## Where things stand

- **Milestone:** **M1 — Presence: IN PROGRESS** (started 2026-07-18). **M1.1 + M1.2 + bot DONE** (committed,
  unpushed); **M1.3 CODE-COMPLETE, uncommitted — awaiting the in-game bot run** (Cody at the PC). Built:
  `CompositeTransport` (host serves Loopback + UDP under one id space; 3 tests), Shim presence
  (`PresenceShim` absolute-coordinate pose capture via OriginShift — sign verified from IL: **absolute =
  position − currentMove**; `RemoteAvatar` capsule + billboarded name tag; `AvatarManager`), mod
  `SessionController` (Host/Join/Leave UMM panel, host = client #1 over Loopback, 20 Hz pose, 5 s time
  sync) + reworked `Main`. Handshake build hard-coded "B99.7" for now (runtime `Application.version`
  logged at host for discovery; TODO M2 supported-build gate). modListHash = "" both sides until the Mod
  API era (04). New game refs: DV.OriginShiftInfo + Physics/TextRendering/IMGUI modules (targets ×2 + CI
  heredocs ×2). **26/26 tests, full sln 0-warnings, fresh build staged in `Mods/LocoMP/`.**
  **In-game run:** enable LocoMP → Ctrl+F10 → Host session → log prints `--at x,y,z` → run
  `dotnet run --project tools/LocoMP.Bot -- --at x,y,z` → bot avatar orbits you.
  M1.1 = game-free session core: `NetServer`/`NetClient` over `ITransport` — handshake v1
  (protocol+build+modVersion+modListHash + password), roster, pose relay (server-authoritative id), server
  time offset, hand-rolled packet codec, multi-peer `LoopbackNetwork` hub. M1.2 = real transport:
  `LiteNetLibTransport` fully implemented (server/client roles, connect-key gate, own peer-id assignment,
  DeliveryMethod mapping, pooled-reader copy) + 2 localhost-UDP integration tests — the SAME session stack,
  now over real sockets. **19/19 tests, stable ×3, full solution 0-warnings.**
  Remaining M1: **M1.3** Shim presence (remote avatars + name tags, capture local pose, host = client #1 via
  Loopback, server time, real modListHash) — the in-game/friend-session half of M1's exit.
- **Milestone:** **M0 — walking skeleton: COMPLETE** (2026-07-18). Scaffold pushed (`9cc1285` + `16d2d37`)
  **and the in-game Shim run passed** — mod loaded, 2 `Junction.Switch` overloads patched, 84 live cars
  streamed w/ positions, 21 junction throws captured across 2 junctions, clean toggle off→on, no exceptions.
  The one unmet exit — cloud CI (build.yml/canary buildid) — is **deferred by Cody's decision**: cloud CI only
  matters once there are contributors; local build is the dev rig, and a red build.yml on push is acceptable.
- **M1 design note banked from the run:** each junction id logged 3–4× consecutively — but that's mostly Cody
  throwing each switch several times by hand, so the log does NOT prove the hook multi-fires per throw. Open
  question for M1: do the 2 `Switch` overloads double-emit on a single throw? Settle with a controlled
  single-throw test. Debounce is good practice regardless, but must coalesce only true duplicates (same
  resulting junction state within a tiny window) — **never rate-limit distinct real throws** (Cody, 2026-07-18).
- **CI observed post-push:** `pr.yml` is PR-only (correct — didn't run on push). `build.yml` (full) fires on
  push and **fails at the Steam download** — expected, no secrets yet (that's exit 1 below). `release.yml`
  startup-failed on the first push (a YAML plain-scalar `": "` bug in the final TODO step) → fixed in `16d2d37`
  as a `run: |` block scalar; it now parses (produced no run on the fix push, as it's tag-only).
- **Local proof (this machine, user SDK 8.0.423):**
  - `dotnet test LocoMP.NoGame.slnf` → **5/5 tests pass**, builds `netstandard2.0` + `net48` + `net8.0`, game-free. This is what `pr.yml` runs.
  - `dotnet build LocoMP.sln -c Release` → **full solution builds, incl. Shim + mod**, against the local B99.7 install. 0 warnings.
  - Mod output = only `LocoMP*.dll` + `LiteNetLib.dll` + `Info.json`; **no game/UMM/Harmony assemblies** (hard rule 2 verified).
  - A ready-to-load mod folder is staged at `dist/LocoMP/` (git-ignored).

## What was built (M0)

- **Layered projects** (`src/`), TFMs enforce the layering: Core/Transport/Api = `netstandard2.0;net48` (no game refs);
  Shim/mod = `net48` (opt into game refs via `LocoMpGameProject=true`); Server/Tests = `net8.0`.
  - `LocoMP.Core`: `ProtocolVersion` (=1) + `VersionHandshake` (protocol+build check, seeds M1 handshake) + `ITransport` port.
  - `LocoMP.Transport`: `LoopbackTransport` (test harness) + `LiteNetLibTransport` (M1 stub, proves the 1.3.5 pin).
  - `LocoMP.Shim`: `WorldStateSpike` — Harmony-patches `Junction.Switch` (DV.RailTrack) + logs live `TrainCar` positions.
  - `LocoMP` (mod): `Main.Load` UMM entry driving the spike. `LocoMP.Server`: net8 headless stub.
- **Version/packaging:** single source `Directory.Build.props` (0.0.2); central pins `Directory.Packages.props`
  (LiteNetLib **exactly 1.3.5**); `repository.json`; `Info.json`; game refs via git-ignored `Directory.Build.targets`
  (committed `.EXAMPLE`); `LocoMP.NoGame.slnf` = the game-free subset CI builds.
- **CI (`.github/workflows/`):** `pr.yml` (game-free build+test+DCO — locally proven), `build.yml` (DepotDownloader+TOTP → full build → API-compat placeholder), `release.yml` (tag/version+CHANGELOG gate → zip → GH Release → repository.json → Nexus), `canary.yml` (nightly buildid watch → records first buildid, PRs on change).
- **Repo hygiene:** MIT LICENSE, README (AI disclosure + not-affiliated), CONTRIBUTING (DCO + clean-room), CHANGELOG, issue/PR templates, `.editorconfig`, `NuGet.config` (source-pinned), `global.json`.

## Next — M1 in progress

1. ~~**M0: push + in-game Shim run.**~~ **DONE 2026-07-18** — `9cc1285` + `16d2d37`; log confirmed load/84 cars/21 junction throws/clean toggle.
2. ~~**M1.1 — game-free session core.**~~ **DONE 2026-07-18** (in `23e9929`) — packet codec, NetServer/NetClient, handshake v1 (+password/modhash), roster, pose relay, time offset, LoopbackNetwork hub. 17/17 tests incl. 8-client storm.
3. ~~**M1.2 — real transport.**~~ **DONE 2026-07-18** (in `23e9929`) — `LiteNetLibTransport` server/client roles, connect-key gate, own peer-id map, Core→LiteNetLib `DeliveryMethod` mapping (using-alias to kill the enum collision), pooled-reader copy+recycle. 2 localhost-UDP integration tests (connect/relay/leave + wrong-key reject). 19/19, stable ×3.
4. ~~**M1.3 — Shim presence.**~~ **VERIFIED IN-GAME 2026-07-18** (uncommitted) — Cody hosted, the bot joined over UDP, avatar orbited with name tag, "looks good". Log banked: **runtime `Application.version` = `99-build2702`** (M2 supported-build gate string); host self-join offset 0 ms; absolute capture exercised ~8 km from origin (`--at 7905,132,7353`). Cosmetic fix applied after the run (grey tag text + black drop-shadow copy — white glared against the skybox); rebuilt + restaged, needs a game restart to see. **Then commit M1.3.** Friend session upgrades verification to M1's official exit wording when available. Junction-debounce note lands with M2.
5. **Deferred until contributors (Cody, 2026-07-18):** wire CI Steam/Nexus secrets; set `.ci/depot.json` manifest. Red `build.yml` on push accepted until then.
6. **Repo residuals, whenever** (05 §7): branch protection, DCO app (optional), repo topics.

## Local commits not yet pushed (push gated on Cody — hard rule 7)
- **`23e9929` feat: M1 presence — session core + UDP transport** (M1.1 + M1.2, 22 files, DCO-signed).
- **`c443a5b` feat: LocoMP.Bot — headless test-player harness** (15 files, DCO-signed).
- **Uncommitted: M1.3** — `CompositeTransport` + 3 tests, Shim presence (PresenceShim/RemoteAvatar/AvatarManager), mod SessionController + Main rework, game-ref additions (targets ×2 + CI heredocs ×2), doc deltas. 26/26 tests, 0-warnings. **Commit after the in-game bot run passes.**
- `Mods/LocoMP/` re-staged with the M1.3 build (dev artifact, outside the repo).

## Blockers
- None. M1.1 + M1.2 verified locally (incl. real UDP). M1.3 needs the game + ideally a friend session.

## Session log
- **2026-07-18** — **M1.3 built (Shim presence + host embed), uncommitted.** `CompositeTransport` multiplexes Loopback + UDP under one outer peer-id space (both inners number from 1 → remap required; 3 tests prove cross-link roster/pose/evict). Shim: `PresenceShim` captures the local pose in ABSOLUTE coords — **DV floating origin**: `OriginShift.currentMove` in `DV.OriginShiftInfo.dll` (found via assembly sweep; `WorldMover` lives in `WorldStreamer.dll`); sign verified by IL inspection of `AbsolutePosition(Transform)` = `position − currentMove` (my first draft had it backwards — would have scattered avatars); did the arithmetic directly instead of calling the helper because its overloads drag Unity.Mathematics/Unity.Transforms into compile-time resolution. `RemoteAvatar` = capsule (collider destroyed) + TextMesh name tag (needs `Arial.ttf` builtin font AND `font.material` on the renderer or it renders nothing) billboarded to `PlayerManager.ActiveCamera`; 12/s lerp, 50 m teleport-snap. Mod: `SessionController` (Idle/Hosting/Joined; host = NetServer over composite + own NetClient on the hub; 20 Hz pose, 5 s BroadcastTime; UMM OnGUI panel Host/Join/Leave; logs `--at x,y,z` for the bot on host). `Main` reworked (session wired; M0 car-spam removed; junction hook kept). `UnityEngine.Pose` name-collides with Core's → using-alias (same idiom as DeliveryMethod). New refs in targets ×2 + CI heredocs ×2: DV.OriginShiftInfo, UnityEngine.{Physics,TextRendering,IMGUI}Module. Handshake build hard-coded "B99.7"; `Application.version` logged for discovery; modListHash "" both sides (Mod API era computes real one). 26/26, 0-warnings, staged to `Mods/LocoMP/`. **Next: Cody's in-game bot run, then commit.**
- **2026-07-18** — M0 scaffold. Cloned repo into `repo/` (Option A layout). Built + verified game-free (5/5) and full solution (Shim compiles vs B99.7). Authored 4 CI workflows. Fixed a `DeliveryMethod` name clash with LiteNetLib. DV API for the spike verified by reflection-only inspection (TrainCar/Bogie/CarSpawner in Assembly-CSharp; Junction in DV.RailTrack). Awaiting Cody for push + secrets + in-game run.
- **2026-07-18** — **Pushed the scaffold** (Cody's explicit go, twice). `9cc1285` scaffold → `16d2d37` release.yml fix. Re-ran `LocoMP.NoGame.slnf` before push = 5/5 (SDK is user-profile `C:\Users\User\.dotnet`, 8.0.423 — system dotnet is runtime-only). Post-push CI: caught + fixed a `release.yml` startup failure (YAML `": "` in a plain-scalar `run:`); confirmed the fix (no Release run on the tag-only workflow). `build.yml` red = no Steam secrets, expected. M0 now down to secrets + in-game run.
- **2026-07-18** — **M0 CLOSED via in-game Shim run.** Cody deferred cloud CI (only needed for contributors; red build.yml accepted). Rebuilt full solution (0 warnings), staged a clean payload to `Mods/LocoMP/`. `Player.log` confirmed all markers: load @protocol v1, `junction hook installed (2 overloads)`, 84 live cars streaming, 21 junction throws across 2 ids, toggle off→on, zero exceptions. Banked: `Junction.Switch` = 2 overloads on B99.7; each id logged 3–4× but Cody threw each switch several times by hand, so per-throw multi-fire is unconfirmed (test in M1). Debounce good practice but must not drop distinct real throws (Cody). Next: M1.
- **2026-07-18** — **M1.1 built (game-free session core).** New Core: `Protocol` (PacketWriter/Reader hand-rolled LE + bounds-checked, MessageType, PresenceCodec), `Presence` (Pose struct, PlayerState), `Session` (NetServer, NetClient, ServerConfig, IClock/System/Manual, NetProtocol). Extended `HandshakeRequest`/`VersionHandshake` (modVersion + modListHash) and `ITransport` (PeerConnected/PeerDisconnected). New `LoopbackNetwork` multi-peer hub (1:1 LoopbackTransport kept for host=client#1). Handshake v1 = protocol+build+modVersion+modListHash checked pure in Core, password+capacity at NetServer. Pose relay stamps server-authoritative id (client-supplied id discarded). **17/17 game-free tests** (5 M0 + 12 new): codec round-trip/truncation/cap, 2-client mutual visibility, pose relay, password/build/full rejects, time-offset, graceful leave, and the **M1-exit 8-client join/leave storm × 25 waves, zero leaks**. Full solution 0-warnings. Uncommitted. Next: M1.2 (LiteNetLib UDP + localhost test).
- **2026-07-18** — **Bot harness built (`tools/LocoMP.Bot`)** — the one-PC "second player" (Cody: 2 Steam accts but 1 PC, no friends available; two rendered instances on one Win11 session isn't viable → headless bot is the daily rig, per 03 §11's soak-bot plan pulled forward). net8 console over Core+Transport only: `BotClient` (injected transport factory + clock → lifecycle unit-tested over Loopback; connect timeout → backoff retry; rejection → hard stop; `--churn` leave/rejoin), behaviors orbit/wander(seeded)/idle behind `IBotBehavior`, swarm `--count`, mismatch flags (`--build`/`--mod-version`/`--password`), stats lines. Added `NetDefaults` (Core): canonical port **8877** + protocol-versioned connect key `LocoMP:1` — host UI (M1.3) and dedicated server (M6) reuse it. Fixed `LoopbackNetwork` endpoint `Dispose` to raise PeerDisconnected on the far side (now matches UDP semantics). In sln + NoGame.slnf (CI-compiled), 4 new lifecycle tests → **23/23**, CHANGELOG Unreleased entry, tool README. **Smoke-proven end-to-end over real UDP**: scratch host (temp, deleted) + 3 bots × 4 churn cycles = 12 joins, 963 poses, roster → 0 every cycle, graceful Ctrl+C/duration exit. Uncommitted.
- **2026-07-18** — **M1.2 built (real LiteNetLib UDP transport).** Reflected the pinned 1.3.5 API (clean-room; third-party lib) to nail exact signatures: no `NetPeer.Id` (→ own peer-id map), `NetPacketReader.GetRemainingBytes()`+`Recycle()` (no AutoRecycle in 1.3.5), LiteNetLib `DeliveryMethod` values (ReliableOrdered=2/Sequenced=1/ReliableUnordered=0). Implemented `LiteNetLibTransport.StartServer/ConnectClient` — connect-key gate (`AcceptIfKey`), peer-id assignment (server 1..N, client=ServerPeer 0), Core→LiteNetLib delivery mapping, events raised on the Poll thread. Killed the `DeliveryMethod` enum collision with a `using`-alias to Core's. **2 localhost-UDP integration tests** (2-client connect→relay→leave; wrong-key reject) — SAME NetServer/NetClient stack over real sockets, no code change above the seam. **19/19 tests, stable ×3, 0-warnings.** Uncommitted. Next: M1.3 (Shim presence — needs game).
