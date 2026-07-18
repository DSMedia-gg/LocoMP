# SESSION log — LocoMP

Append one entry per work burst (newest at top). Pair with `STATE.md` (current state) — this file is the
narrative history. See `../CLAUDE.md` for the discipline.

---

## 2026-07-18 — M0 push (scaffold → GitHub)

**Goal:** land the M0 scaffold on `DSMedia-gg/LocoMP` (pipeline exit 1), Cody-gated per hard rule 7.

**Done:**
- Audited the staged set (46 files) for hard-rule-2 leaks → clean (no game `.dll`, no `Directory.Build.targets`,
  no zip/bin/obj/dist). Re-ran `LocoMP.NoGame.slnf` = **5/5** before committing.
- Committed `9cc1285` (scaffold) with **DCO sign-off + AI `Co-Authored-By`**, pushed on Cody's go.
- Post-push CI review: `pr.yml` PR-only (didn't run, correct). `build.yml` failed at the Steam step — expected,
  secrets unwired. `release.yml` **startup-failed** — real bug: a single-line `run:` with `": "` in a YAML plain
  scalar ("TODO: trigger …"). Fixed to a `run: |` block scalar in `16d2d37`, pushed; confirmed no Release run
  fires (tag-only + now parses). All four workflows parse clean (pyyaml check).

**Learned / notes:**
- The build SDK is the **user-profile** install `C:\Users\User\.dotnet` (8.0.423); the system dotnet is
  runtime-only, so `dotnet` off PATH can't build. Set `DOTNET_ROOT` + PATH to it (same quirk as SwarmUI).
- GitHub parses **every** workflow file on any push; a malformed one produces a phantom startup-failure run even
  when its trigger wouldn't match — that's how the tag-only `release.yml` surfaced on a branch push.

**Next:** wire CI Steam/Nexus secrets (exit 2) → in-game Shim run (exit 3). See `STATE.md` → Next.

---

## 2026-07-18 — M1.2 (real LiteNetLib UDP transport)

**Goal:** make the M1.1 session stack run over real UDP by finishing `LiteNetLibTransport`, and prove it
with a localhost integration test (two NetManagers in one process — no second PC).

**Done (game-free, uncommitted):**
- **Reflected the pinned LiteNetLib 1.3.5 assembly** (clean-room safe — third-party MIT lib, metadata only)
  to nail exact API: event delegate signatures, `NetPeer` has NO `Id` (only `RemoteId`) → I assign my own
  peer ids; `NetPacketReader.GetRemainingBytes()` (owned copy) + `Recycle()` (no `AutoRecycle` property in
  1.3.5); LiteNetLib `DeliveryMethod` = ReliableOrdered(2)/Sequenced(1)/ReliableUnordered(0).
- **`LiteNetLibTransport`** implemented: `StartServer(port,key)` / `ConnectClient(host,port,key)`; connect-key
  gate via `AcceptIfKey`; peer-id assignment (server 1..N, client = `ServerPeer` 0 — same id space as the
  Loopback hub); Core→LiteNetLib `DeliveryMethod` mapping; payload copied out of the pooled reader; events
  raised on the Poll thread (no locks). Killed the `DeliveryMethod` enum collision with a `using`-alias.
- **2 integration tests** over real localhost UDP: two clients connect → mutual visibility → pose relay →
  graceful leave; and a wrong-connect-key rejection. **19/19 total, stable across 3 runs, 0 warnings.**

**Learned / notes:**
- The seam held: ZERO new session logic for UDP — the exact M1.1 `NetServer`/`NetClient` drove real sockets
  after only the enum-alias fix. 03 §2 transport-swap is real.
- Two credential layers proven end-to-end: connect-key at the transport (LiteNetLib refuses the socket) vs.
  session password at the app layer (server refuses the JoinRequest). Each fails at its own layer, each tested.
- LiteNetLib default mode queues socket events on a bg thread and raises them only in `PollEvents()`, so our
  id/peer dictionaries are single-threaded from Core's view.

**Next:** M1.3 — Shim presence (needs game): map `Pose`↔UnityEngine at the boundary, spawn remote avatars +
name-tag billboards from `NetClient` events, capture local pose → `SendPose`, embed host = client #1 via
Loopback, apply server time, compute the real `modListHash`. Verify in a friend session (M1 exit game half).

---

## 2026-07-18 — M1.1 (game-free session core)

**Goal:** the harness half of M1 (07 §M1) — a Core netstate that does handshake + presence over
`ITransport`, driven by the Loopback rig, retiring the "8-client join/leave storm, no leaks" exit test.

**Done (all game-free, uncommitted):**
- **Protocol:** `PacketWriter`/`PacketReader` — hand-rolled little-endian, varint/int64/single/string,
  bounds-checked as untrusted input (truncation throws, string length capped, 03 §9). `MessageType` (stable
  wire enum). `PresenceCodec` (Pose + PlayerState (de)serialization).
- **Presence:** `Pose` (game-free readonly struct — Shim maps to UnityEngine at the edge), `PlayerState`.
- **Session:** `NetServer` (handshake admission → roster → pose relay → evict-on-disconnect), `NetClient`
  (connect → handshake → roster mirror + time offset), `ServerConfig`, `IClock`/`SystemClock`/`ManualClock`,
  `NetProtocol.ServerPeer=0`.
- **Handshake v1 (03 §10):** extended `HandshakeRequest`/`VersionHandshake` to check protocol + build +
  modVersion + modListHash (pure, in Core); password + capacity enforced in `NetServer`.
- **Transport:** `ITransport` gained `PeerConnected`/`PeerDisconnected`; new **`LoopbackNetwork`** multi-peer
  hub (one server ↔ N clients, like a LiteNetLib NetManager), deterministic (delivers on Poll). Kept the 1:1
  `LoopbackTransport` for host = client #1.
- **Tests: 17/17** (5 M0 + 12 new). Marquee: `JoinLeaveStormTests` — **8 clients × 25 waves**, asserting
  `server.PlayerCount` and `hub.ClientCount` return to 0 every wave (the M1 exit harness criterion). Full
  solution still builds 0-warnings.

**Design decisions (implementation-level):**
- `ITransport` is the **multi-peer** abstraction (Send/Received carry peerId) — matches LiteNetLib's one-
  manager-many-peers model. The N-client test rig is a hub, not N 1:1 pairs.
- **Server authority on pose:** the server discards the client-supplied id in a pose packet and stamps its
  own peer id before relaying. A client can't move another's avatar. (Same law as economy later, 03 §3/§9.)
- Avoided C# `record`/`init` (need `IsExternalInit` on netstandard2.0) — ctor + get-only, matching the scaffold.

**Next:** M1.2 — finish `LiteNetLibTransport` (connect/listen/send, `DeliveryMethod` mapping, raise events) +
a localhost-UDP integration test (runs on this one PC). Then M1.3 Shim presence (avatars/name tags, in-game).

---

## 2026-07-18 — M0 closed (in-game Shim run)

**Goal:** the local proof for M0 exit 3 — enable the mod in DV and confirm the world-state log.

**Decision (Cody, implementation-level):** **defer cloud CI** until there are contributors. Cloud `build.yml`
exists only so a game-less runner can build; Cody has the game, so the local build is the dev rig. A red
`build.yml` on each push is accepted noise; Steam/Nexus secrets get wired when contributors arrive.

**Done:**
- Rebuilt the full solution in Release (0 warnings), verified the mod output carries no game/UMM/Harmony DLLs,
  staged a clean payload into `…/Derail Valley/Mods/LocoMP/`.
- Cody launched DV, enabled LocoMP in UMM, loaded a session, threw junctions. `Player.log` confirmed **every**
  marker: `shim spike loaded — protocol v1`; `junction hook installed (2 Switch overloads)`; 84 live cars
  streaming positions/speed every 5 s; **21** `[world] junction switched` events across 2 junction ids; a clean
  `disabled`→`enabled` toggle; **zero LocoMP exceptions**. **M0 complete.**

**Learned / banked for M1:**
- B99.7's `Junction.Switch` exposes exactly **2 non-static overloads** (both patchable).
- Each junction id logged **3–4×** consecutively — but Cody threw each switch several times by hand, so this
  does NOT prove per-throw multi-fire. Open M1 question: do the 2 overloads double-emit on a single throw?
  Settle with a controlled single-throw test. Debounce is worth doing regardless, but must coalesce only true
  duplicates (same resulting state within a tiny window) — **never rate-limit distinct real throws** (Cody).
- Toggling off then on proves the `_active` gate halts the update loop without unpatching Harmony — no hook
  leak — which M1's host/join lifecycle can rely on.

**Next:** M1 — networked handshake + first synced entity (`07-ROADMAP.md`). Seeds in place: `VersionHandshake`,
`LiteNetLibTransport` stub, `ITransport`, Loopback rig.

---

## 2026-07-18 — M0 walking skeleton (scaffold)

**Goal:** stand up the repo per `../05-PIPELINE.md` §1, get the game-free build+test green, author the CI
pipeline, and prove the Shim seam compiles against Derail Valley B99.7.

**Done:**
- Cloned `DSMedia-gg/LocoMP` into `repo/` (kept the planning corpus private in the parent — layout Option A).
- Seven layered projects; layering enforced by target frameworks. Trivial-but-real Core unit
  (`VersionHandshake`) + Loopback transport test → **5/5 green**, no game needed.
- Central package pinning (LiteNetLib **=1.3.5**); single version source; `net48` compiles on any OS via
  `Microsoft.NETFramework.ReferenceAssemblies`.
- Four CI workflows. `pr.yml`'s build+test verified locally (identical to the CI command).
- Shim spike: `Main.Load` (UMM) → `WorldStateSpike` logs `TrainCar` positions + `Junction.Switch` throws.
  **Full solution builds against the local B99.7 install**, mod output carries no game assemblies.
- Repo hygiene: MIT, DCO, clean-room CONTRIBUTING, AI-disclosure README, issue/PR templates, CHANGELOG.

**Decisions this session (implementation-level; none change 00's D1–D12):**
- Repo lives at `…/LocoMP/repo/`; planning corpus stays in the parent, out of the public repo (Cody chose Option A).
- Game refs isolated behind `LocoMpGameProject=true` + a git-ignored `Directory.Build.targets`; the reference
  list is committed as `.EXAMPLE`. Only `$(DvInstallDir)` is per-contributor.
- Version 0.0.2 (0.0.1 was the Nexus bootstrap). Protocol version 1, tracked separately.

**Learned / notes:**
- LiteNetLib ships its own `DeliveryMethod` enum → collides with Core's; the adapter must translate, never
  import both. Fixed by fully-qualifying LiteNetLib types in the transport adapter.
- `Junction` is in `DV.RailTrack.dll` (not Assembly-CSharp); `CarSpawner` raises `CarSpawned`/`CarAboutToBeDeleted`
  and is a `DV.Utils.SingletonBehaviour<>`. Verified by reflection-only load (metadata only — clean-room safe).
- The Shim's compile-time refs (UnityModManager + 0Harmony) are NOT in the Steam depot → `build.yml` must
  also fetch the UMM distribution (bundles both). Parameterized as the `UMM_ZIP_URL` repo variable.

**Next:** push (gated) → wire Steam/Nexus secrets → in-game Shim run. See `STATE.md` → Next.
