# STATE — LocoMP (implementation)

**Updated:** 2026-07-18 (M2.1 train-core session) · This is the **implementation** memory (burst cadence, D8).
The **planning corpus** lives one level up at `../` (00–09, INDEX, research/) — strategic, kept private.
Cold-starting? Read `../CLAUDE.md` (hard rules) → this file → the current milestone in `../07-ROADMAP.md`.

## Where things stand

- **Milestone:** **M2 — Trains: IN PROGRESS** (started 2026-07-18). **M2.1 (game-free train core) DONE,
  committed** — the 03 §4 consist-transaction protocol implemented and fuzz-proven headless:
  - `TrainsetRegistry` (server authority): register / couple (all four end-orderings) / uncouple /
    derail / rerail / park / claim. Epoch rules: membership changes retire parent ids + mint fresh ones
    with epoch = max(parents)+1; derail/rerail keep the id, bump the epoch. Guards: 2 s settle window
    (Open Rails), 10 m/s relV cap, end-car adjacency, owner-only proposals (rerail = any player).
  - `TrainsetView` (client mirror): snapshot admission = known id + EXACT epoch + car count; discard
    counters are the fuzz oracle. `BogieState` spline-space encoding (edgeId, s, v); derailed cars
    stream a 6-DOF `Pose` instead (`CarSnapshot`).
  - `ServerTrains`/`ClientTrains` modules wired into NetServer/NetClient: snapshot relay behind the
    owner+epoch admission check, world burst on admit, park + grant-release on disconnect, junctions
    (duplicate-coalesce ONLY on same-resulting-state — distinct throws always commit, per the M0 note),
    turntables (last-writer-wins), control grants + input routing to the sim owner (grant ≠ ownership,
    multi-crew shape), `ResyncRequest` escape hatch. **Protocol v2** (MessageType 9–28).
  - `WorldTopology` + `TopologyCodec` ("LMPW" versioned binary via PacketWriter/Reader, zero new deps)
    — the extractor contract; Core loads a synthetic world game-free (M2.2 writes real files).
  - **M2 exit fuzz passed: 1,000 random couple/uncouple/derail/rerail transactions, each chased by a
    stale-stamped snapshot down the same link — zero stale applications (server drops all 1,000 at the
    door; independent per-client epoch-shadow oracle never fired), all 3 client mirrors converge exactly
    to the registry, car conservation holds. 59/59 tests, stable ×3, full sln 0 warnings.**
- **Milestone:** **M1 — Presence: COMPLETE + PUSHED** (2026-07-18, `16d2d37..ce41556`): session core +
  handshake v1 + roster + pose relay + time sync (`23e9929`), LiteNetLib UDP transport + bot harness
  (`c443a5b`), Shim presence in-game — host/join UMM panel, avatars + name tags, absolute-coordinate
  capture via OriginShift (`ce41556`). Verified live via the one-PC bot rig; friend session later
  upgrades to the official exit wording. Runtime version string banked: **`Application.version` =
  `99-build2702`** (handshake build still hard-coded "B99.7" → M2.3 supported-build gate).
- **Milestone:** **M0 — walking skeleton: COMPLETE** (2026-07-18). Scaffold pushed (`9cc1285` + `16d2d37`)
  **and the in-game Shim run passed** — mod loaded, 2 `Junction.Switch` overloads patched, 84 live cars
  streamed w/ positions, 21 junction throws captured across 2 junctions, clean toggle off→on, no exceptions.
  The one unmet exit — cloud CI (build.yml/canary buildid) — is **deferred by Cody's decision**: cloud CI only
  matters once there are contributors; local build is the dev rig, and a red build.yml on push is acceptable.
- **Junction double-fire: CONFIRMED 2026-07-18** (controlled single throw during the M2.1 regression run:
  one player throw → the same junction id logged exactly 2×, i.e. the two patched `Switch` overloads chain).
  Game-internal junction sets earlier in the same log hit only ONE overload (single lines). **M2.3: hook the
  inner overload only, or dedupe same-frame repeats in the Shim**; the server-side coalesce (same-resulting-
  state only) already makes the wire safe either way — and it never rate-limits distinct real throws
  (Cody's constraint, honored).
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

## Next — M2 in progress

1. ~~**M2.1 — game-free train core.**~~ **DONE 2026-07-18** (see Where things stand) — registry, epochs,
   codecs, session modules, topology contract, 1,000-transaction fuzz green.
2. **M2.2 — world extractor (Shim-side, needs the game):** walk the live `RailTrack` graph + junctions,
   assign the stable edge numbering the Shim will also use for `BogieState.EdgeId`, write the LMPW file;
   exit = Core loads the REAL extracted output in a test (criterion is "extractor output loads in a Core
   test"). Prototype-level is fine (03 §6 flags it as the riskiest dedicated-server dependency).
3. **M2.3 — Shim train integration (in-game half):** capture owned consist bogies in spline space; apply
   remote snapshots with interpolation (12/s lerp precedent from avatars); Harmony hooks: coupler contact →
   `ProposeCouple` (translate physical coupler → trainset-end form), uncouple → `ProposeUncouple`, derail →
   `ReportDerail`; junction hook → `ThrowJunction` — **hook the inner `Switch` overload only (double-fire
   confirmed, see note above)**; grants on cab entry/exit; **supported-build gate**:
   handshake sends runtime `Application.version` (`99-build2702`), mod refuses unknown builds with a friendly
   message instead of the hard-coded "B99.7". Bot: `--consist` mode driving a synthetic consist along
   extracted topology = the one-PC "ghost train" rig.
4. **M2 exit (in-game):** two consists driven (Cody + bot), couple into one, both crew it, uncouple,
   derail + rerail — no snap-back. Friend session upgrades wording when available.
5. **Deferred until contributors (Cody, 2026-07-18):** wire CI Steam/Nexus secrets; set `.ci/depot.json` manifest. Red `build.yml` on push accepted until then.
6. **Repo residuals, whenever** (05 §7): branch protection, DCO app (optional), repo topics.

## Push state
- **All M2.1 work PUSHED 2026-07-18** (`ce41556..cc615d2`: train core `1bc5a96`, UDP integration test `1ece517`, tag-shadow fix + banked findings `cc615d2`; Cody's go after the in-game regression passed). Post-push CI as expected: `build.yml` red at the Steam step (accepted until contributors).
- Staged payload in the game's `Mods/LocoMP/` = current (incl. the tag-shadow fix, restaged 2026-07-18 after the game closed). **Visually re-check the tag on the next game run** — that's the only unverified change.

## Blockers
- None. M2.1 verified headless; M2.2/M2.3 need the game (Cody at the PC), M2 exit ideally a friend session.

## Session log
- **2026-07-18** — **M2.1 in-game regression PASSED + pushed.** Fresh payload staged; Cody hosted on the
  v2 protocol, bot joined/left twice over UDP, avatars clean, zero exceptions in Player.log. Added
  `TrainUdpIntegrationTests` (register → relay → couple → stale-stamp drop → re-baseline over REAL
  localhost UDP; the stale send happens only after merge convergence because transactions and snapshots
  ride different UDP channels with no cross-channel ordering — the in-flight race stays the Loopback
  fuzz's job). **60/60.** Controlled single junction throw settled the M0 question: player throw = 2 hook
  fires (overloads chain); game-internal sets = 1. Name-tag shadow offset was still too big in the field
  (parallax doubling up close) → tightened 0.048/0.03 → 0.012/0.004; **visual re-check next game run.**
- **2026-07-18** — **M2.1 built (game-free train core), committed.** New Core: `Trains/` (BogieState
  spline-space, CarDef/TrainsetDef with epochs, CarSnapshot railed|6-DOF, TrainsetTransaction,
  TrainsetRegistry = ALL epoch rules in one place, TrainsetView = exact-epoch admission + discard
  counters), `Protocol/TrainCodec` (hand-rolled, count-capped untrusted reads), `World/`
  (WorldTopology + LMPW TopologyCodec — extractor contract), `Session/ServerTrains` + `ClientTrains`
  wired into NetServer/NetClient (train traffic only from ADMITTED peers; world burst on admit; park +
  grant release on leave). Protocol v1→2 (MessageType 9–28 appended). Design choices worth remembering:
  (a) membership transactions mint FRESH trainset ids and retire parents — a stale snapshot can't even
  name a live set after a merge/split; epoch bump covers same-id derail/rerail; (b) client epoch check
  is EXACT equality because snapshots (sequenced-unreliable) can outrun transactions (reliable-ordered)
  cross-channel — a future-epoch snapshot is dropped and the owner's next one re-baselines;
  (c) `CoupleEnd` is defined against the TRAINSET (Front = index-0 side), not car couplers, so Core
  validates/orders merges without knowing car orientation — Shim translates at the boundary;
  (d) junction duplicate-coalesce = same-resulting-branch only (never rate-limits distinct throws —
  Cody's M0 constraint); (e) snapshot relay reuses the sender's original bytes (no re-encode).
  `InternalsVisibleTo(LocoMP.Core.Tests)` added for codec edge tests. **Tests 26→59** (codec 5, registry
  14 incl. all 4 merge-end orderings, topology 3, session integration 8, fuzz 3): the 07 §M2 exit fuzz =
  1,000 random transactions each chased by a stale-stamped snapshot on the same link — 0 stale applies,
  server dropped all 1,000, mirrors converge, cars conserved. Stable ×3, full sln 0 warnings.
  **Next: M2.2 extractor (needs game) → M2.3 Shim trains + supported-build gate.**
- **2026-07-18** — **M1.3 built (Shim presence + host embed), uncommitted.** `CompositeTransport` multiplexes Loopback + UDP under one outer peer-id space (both inners number from 1 → remap required; 3 tests prove cross-link roster/pose/evict). Shim: `PresenceShim` captures the local pose in ABSOLUTE coords — **DV floating origin**: `OriginShift.currentMove` in `DV.OriginShiftInfo.dll` (found via assembly sweep; `WorldMover` lives in `WorldStreamer.dll`); sign verified by IL inspection of `AbsolutePosition(Transform)` = `position − currentMove` (my first draft had it backwards — would have scattered avatars); did the arithmetic directly instead of calling the helper because its overloads drag Unity.Mathematics/Unity.Transforms into compile-time resolution. `RemoteAvatar` = capsule (collider destroyed) + TextMesh name tag (needs `Arial.ttf` builtin font AND `font.material` on the renderer or it renders nothing) billboarded to `PlayerManager.ActiveCamera`; 12/s lerp, 50 m teleport-snap. Mod: `SessionController` (Idle/Hosting/Joined; host = NetServer over composite + own NetClient on the hub; 20 Hz pose, 5 s BroadcastTime; UMM OnGUI panel Host/Join/Leave; logs `--at x,y,z` for the bot on host). `Main` reworked (session wired; M0 car-spam removed; junction hook kept). `UnityEngine.Pose` name-collides with Core's → using-alias (same idiom as DeliveryMethod). New refs in targets ×2 + CI heredocs ×2: DV.OriginShiftInfo, UnityEngine.{Physics,TextRendering,IMGUI}Module. Handshake build hard-coded "B99.7"; `Application.version` logged for discovery; modListHash "" both sides (Mod API era computes real one). 26/26, 0-warnings, staged to `Mods/LocoMP/`. **Next: Cody's in-game bot run, then commit.**
- **2026-07-18** — M0 scaffold. Cloned repo into `repo/` (Option A layout). Built + verified game-free (5/5) and full solution (Shim compiles vs B99.7). Authored 4 CI workflows. Fixed a `DeliveryMethod` name clash with LiteNetLib. DV API for the spike verified by reflection-only inspection (TrainCar/Bogie/CarSpawner in Assembly-CSharp; Junction in DV.RailTrack). Awaiting Cody for push + secrets + in-game run.
- **2026-07-18** — **Pushed the scaffold** (Cody's explicit go, twice). `9cc1285` scaffold → `16d2d37` release.yml fix. Re-ran `LocoMP.NoGame.slnf` before push = 5/5 (SDK is user-profile `C:\Users\User\.dotnet`, 8.0.423 — system dotnet is runtime-only). Post-push CI: caught + fixed a `release.yml` startup failure (YAML `": "` in a plain-scalar `run:`); confirmed the fix (no Release run on the tag-only workflow). `build.yml` red = no Steam secrets, expected. M0 now down to secrets + in-game run.
- **2026-07-18** — **M0 CLOSED via in-game Shim run.** Cody deferred cloud CI (only needed for contributors; red build.yml accepted). Rebuilt full solution (0 warnings), staged a clean payload to `Mods/LocoMP/`. `Player.log` confirmed all markers: load @protocol v1, `junction hook installed (2 overloads)`, 84 live cars streaming, 21 junction throws across 2 ids, toggle off→on, zero exceptions. Banked: `Junction.Switch` = 2 overloads on B99.7; each id logged 3–4× but Cody threw each switch several times by hand, so per-throw multi-fire is unconfirmed (test in M1). Debounce good practice but must not drop distinct real throws (Cody). Next: M1.
- **2026-07-18** — **M1.1 built (game-free session core).** New Core: `Protocol` (PacketWriter/Reader hand-rolled LE + bounds-checked, MessageType, PresenceCodec), `Presence` (Pose struct, PlayerState), `Session` (NetServer, NetClient, ServerConfig, IClock/System/Manual, NetProtocol). Extended `HandshakeRequest`/`VersionHandshake` (modVersion + modListHash) and `ITransport` (PeerConnected/PeerDisconnected). New `LoopbackNetwork` multi-peer hub (1:1 LoopbackTransport kept for host=client#1). Handshake v1 = protocol+build+modVersion+modListHash checked pure in Core, password+capacity at NetServer. Pose relay stamps server-authoritative id (client-supplied id discarded). **17/17 game-free tests** (5 M0 + 12 new): codec round-trip/truncation/cap, 2-client mutual visibility, pose relay, password/build/full rejects, time-offset, graceful leave, and the **M1-exit 8-client join/leave storm × 25 waves, zero leaks**. Full solution 0-warnings. Uncommitted. Next: M1.2 (LiteNetLib UDP + localhost test).
- **2026-07-18** — **Bot harness built (`tools/LocoMP.Bot`)** — the one-PC "second player" (Cody: 2 Steam accts but 1 PC, no friends available; two rendered instances on one Win11 session isn't viable → headless bot is the daily rig, per 03 §11's soak-bot plan pulled forward). net8 console over Core+Transport only: `BotClient` (injected transport factory + clock → lifecycle unit-tested over Loopback; connect timeout → backoff retry; rejection → hard stop; `--churn` leave/rejoin), behaviors orbit/wander(seeded)/idle behind `IBotBehavior`, swarm `--count`, mismatch flags (`--build`/`--mod-version`/`--password`), stats lines. Added `NetDefaults` (Core): canonical port **8877** + protocol-versioned connect key `LocoMP:1` — host UI (M1.3) and dedicated server (M6) reuse it. Fixed `LoopbackNetwork` endpoint `Dispose` to raise PeerDisconnected on the far side (now matches UDP semantics). In sln + NoGame.slnf (CI-compiled), 4 new lifecycle tests → **23/23**, CHANGELOG Unreleased entry, tool README. **Smoke-proven end-to-end over real UDP**: scratch host (temp, deleted) + 3 bots × 4 churn cycles = 12 joins, 963 poses, roster → 0 every cycle, graceful Ctrl+C/duration exit. Uncommitted.
- **2026-07-18** — **M1.2 built (real LiteNetLib UDP transport).** Reflected the pinned 1.3.5 API (clean-room; third-party lib) to nail exact signatures: no `NetPeer.Id` (→ own peer-id map), `NetPacketReader.GetRemainingBytes()`+`Recycle()` (no AutoRecycle in 1.3.5), LiteNetLib `DeliveryMethod` values (ReliableOrdered=2/Sequenced=1/ReliableUnordered=0). Implemented `LiteNetLibTransport.StartServer/ConnectClient` — connect-key gate (`AcceptIfKey`), peer-id assignment (server 1..N, client=ServerPeer 0), Core→LiteNetLib delivery mapping, events raised on the Poll thread. Killed the `DeliveryMethod` enum collision with a `using`-alias to Core's. **2 localhost-UDP integration tests** (2-client connect→relay→leave; wrong-key reject) — SAME NetServer/NetClient stack over real sockets, no code change above the seam. **19/19 tests, stable ×3, 0-warnings.** Uncommitted. Next: M1.3 (Shim presence — needs game).
