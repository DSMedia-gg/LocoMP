# SESSION log — LocoMP

Append one entry per work burst (newest at top). Pair with `STATE.md` (current state) — this file is the
narrative history. See `../CLAUDE.md` for the discipline.

---

## 2026-07-18 — M2.1 (game-free train core) — the hard problem, fuzz-proven

**Goal:** implement 03 §4 (consist transactions with epochs) headless, plus the world-topology
contract, and prove the M2 exit fuzz criterion before any Shim work.

**Done:**
- `LocoMP.Core.Trains`: `BogieState` (spline-space), `CarDef`/`TrainsetDef`/`CarSnapshot`/
  `TrainsetSnapshot`, `TrainsetTransaction`, `TrainsetRegistry` (the server authority — every epoch
  rule lives here), `TrainsetView` (client mirror — exact-epoch snapshot admission; discard counters
  double as the fuzz oracle).
- `ServerTrains`/`ClientTrains` session modules wired into NetServer/NetClient: snapshot relay behind
  the owner+epoch admission check, world burst on admit, park + grant release on disconnect,
  junctions, turntables, control grants + input routing to the sim owner, `ResyncRequest`.
  Protocol v1 → v2 (MessageType 9–28 appended).
- `LocoMP.Core.World`: `WorldTopology` + "LMPW" versioned binary codec on the PacketWriter/Reader
  primitives (zero new dependencies) — the extractor ↔ dedicated-server contract, loads game-free.
- Tests 26 → 59: codec edges (hostile counts, truncation), registry (all four merge end-orderings,
  settle guard, relV cap, epoch rules), 8 session integration flows, and the 07 §M2 exit fuzz —
  **1,000 random couple/uncouple/derail/rerail transactions, each chased by a stale-stamped snapshot
  down the same link: zero stale applications**, all three client mirrors converge exactly to the
  registry, car conservation holds. Stable ×3; full solution 0 warnings.

**Decisions this session (implementation-level; none change 00's D1–D12):**
- Membership transactions mint FRESH trainset ids (parents retired); product epoch = max(parents)+1.
  Derail/rerail keep the id and bump the epoch. Strongest form of the 03 §4 invariant: after a
  merge/split a stale snapshot cannot even *name* a live trainset.
- Client snapshot admission is EXACT epoch equality: snapshots (sequenced-unreliable) can outrun
  transactions (reliable-ordered) cross-channel, so a future-epoch snapshot is dropped too; the
  owner's next snapshot re-baselines. Brief gap, zero corruption.
- `CoupleEnd` is defined against the TRAINSET (Front = index-0 side), not the car's own couplers —
  Core validates and orders merges without knowing car orientations; the Shim translates a physical
  coupler contact into trainset-end form at the boundary.
- Junction duplicate-coalesce: only a throw producing the SAME resulting branch is swallowed (hook
  double-fire case); distinct real throws always commit (Cody's M0 constraint, honored server-side).
- Rerail placement is delegated to the owner's first new-epoch snapshot for now; a server-chosen
  spline pose needs topology and lands with the extractor era.
- Registration correlation token: the server assigns ALL ids; the registering client maps its local
  cars by (token, car order) from the echoed create.

**Learned / notes:**
- Snapshot relay reuses the sender's original packet bytes after validation (no re-encode on the hot
  path); recipients need no sender id — the trainset's owner is authoritative by definition.
- `InternalsVisibleTo` (Core → Tests) added so `TrainCodec`'s untrusted-input edges are directly
  testable; PresenceCodec precedent had kept the codecs internal.

**Next:** M2.2 world extractor (Shim walks the live RailTrack graph, writes LMPW; Core loads the REAL
file = exit criterion) → M2.3 Shim train integration + supported-build gate (`99-build2702`) + bot
`--consist` ghost-train rig → in-game M2 exit. See `STATE.md` → Next.

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

## 2026-07-18 — M1.3 (Shim presence + host embed) — code-complete

**Goal:** the game half of M1: avatars + name tags for remote players, local pose capture, and the
host-embedded server (host = client #1), developed against the bot harness.

**Done (uncommitted — commit after the in-game bot run):**
- **`CompositeTransport`** (Transport, game-free): one NetServer serves the host's own player over the
  Loopback hub AND remote players over UDP. Each inner transport numbers peers from 1, so the composite
  remaps to a unified outer id space. 3 tests: cross-link mutual visibility, cross-link pose relay,
  single-link eviction. **26/26 total.**
- **Shim presence:** `PresenceShim` (absolute-coordinate pose capture), `RemoteAvatar` (capsule +
  billboarded TextMesh name tag, 12/s lerp, 50 m teleport-snap), `AvatarManager` (id → avatar registry).
  Nothing above the Shim touches a GameObject.
- **Mod:** `SessionController` — Idle/Hosting/Joined state machine behind a UMM OnGUI panel (name, port,
  password, address; Host/Join/Leave; live player list). Hosting = NetServer over the composite + own
  NetClient on the hub; 20 Hz pose send; 5 s BroadcastTime. On host it logs `--at x,y,z` ready to paste
  into the bot. `Main` reworked: session wired to OnToggle/OnUpdate/OnGUI; M0's 5 s car-position log
  removed; junction hook kept (quiet, event-driven).

**Learned (B99.7 API intel, clean-room):**
- **Floating origin:** the sync-correctness landmine. `DV.OriginShift.OriginShift` (static, in
  `DV.OriginShiftInfo.dll`) exposes `currentMove` + `AbsolutePosition(Transform)`; `WorldMover` itself
  is in `WorldStreamer.dll`. **Sign verified by IL inspection: absolute = position − currentMove** (the
  draft had it backwards — avatars would have scattered by the accumulated shift). We compute with
  `currentMove` directly: `AbsolutePosition`'s float3/Translation/LocalToWorld overloads drag
  Unity.Mathematics + Unity.Transforms into overload resolution for one Vector3 subtraction.
- `UnityEngine.Pose` collides with Core's `Pose` → using-alias, same idiom as the LiteNetLib
  `DeliveryMethod` collision. Third instance of the pattern; it's now house style.
- Raw `TextMesh` renders NOTHING without both a font (`Resources.GetBuiltinResource<Font>("Arial.ttf")`)
  and that font's material on the MeshRenderer.
- New game refs needed (added to both targets files + both CI heredocs): `DV.OriginShiftInfo`,
  `UnityEngine.PhysicsModule` (Collider destroy), `UnityEngine.TextRenderingModule` (TextMesh),
  `UnityEngine.IMGUIModule` (GUILayout).
- Deliberate M1 simplifications, documented in code: handshake game build hard-coded `"B99.7"` (runtime
  `Application.version` logged at host start = discovery for the M2 supported-build gate); modListHash
  `""` on both sides until the Mod API era (04) — matches the bot's defaults so the daily rig just works.

**In-game run: PASSED (same day).** Cody hosted, the bot joined over real UDP, the avatar orbited with
its name tag — "looks good". Banked from the log: **`Application.version` = `99-build2702`** (the string
for M2's supported-build gate), host self-join offset 0 ms (loopback host=client#1 proof), absolute
capture exercised ~8 km from Unity origin. One cosmetic fix applied after the run: name tag was hard to
read against the skybox → text now soft grey with a black drop-shadow copy (a second TextMesh nudged
down-right/behind — TextMesh has no native shadow). Note: `dotnet run` failed for Cody (SDK is
user-profile only, runtime is system-wide) → run the built exe directly; bot README now leads with that.

**Next:** eyeball the new tag on next game start, then commit M1.3.

---

## 2026-07-18 — Bot harness (tools/LocoMP.Bot): the one-PC second player

**Goal:** solve M1.3's testing constraint — Cody has two Steam accounts but one PC and no friend
testers available. Answer: the "second player" doesn't need the game. A headless bot client joins the
hosted session over localhost UDP and streams synthetic avatar poses; Cody watches its avatar in his
one game instance. (Two rendered instances on one Win11 PC isn't viable — one Steam client per user
session, second session gets no GPU. Second account stays reserved for the CI depot + occasional
borrowed-hardware checks.) Built to serve ALL future plans, not just M1.3 — this is 03 §11's
"scripted bot clients" pulled forward from M6.

**Done (uncommitted):**
- **`tools/LocoMP.Bot`** — net8 console over Core+Transport only (runs on Linux for M6 SVHost soaks):
  - `BotClient`: injected transport factory + clock → the whole lifecycle is unit-tested over
    Loopback. Connect timeout (10 s) → retry with backoff (survives server restarts, soak-grade);
    handshake rejection → hard stop (config mismatch, don't spam); `--churn N` = leave/rejoin cycle
    (live-fire join/leave storm).
  - Behaviors behind `IBotBehavior` (pure pose math, no networking): orbit (faces direction of
    travel), wander (seeded → replayable), idle. Swarm via `--count` (spread start angles/seeds).
  - Mismatch testing built in: `--build WRONG`, `--mod-version`, `--password` exercise every
    rejection screen the host will grow in M1.3.
- **`NetDefaults` (Core):** canonical port **8877** + connect key **`LocoMP:<protocol>`** — stale
  protocol refused at the socket before any handshake; host UI (M1.3) and dedicated server (M6)
  will consume the same constants.
- **`LoopbackNetwork` fix:** endpoint `Dispose()` now deregisters from the hub and raises
  `PeerDisconnected` on the far side — Loopback drop semantics now match UDP socket close.
- Wired into `LocoMP.sln` + `LocoMP.NoGame.slnf` (pr.yml compiles it forever), 4 lifecycle tests
  (join+orbit-on-circle, churn rejoin, reject-stops, no-server-retries) → **23/23**; CHANGELOG
  Unreleased entry; `tools/LocoMP.Bot/README.md` with the one-PC workflow + future-use table.

**Verified:** unit suite 23/23 (stable) + full sln 0-warnings + live end-to-end over real UDP:
scratch host (temp project outside the repo, deleted after) + `--count 3 --churn 6 --duration 22` →
12 joins across 4 churn cycles, 963 poses, mutual visibility correct after every rejoin, server
roster returned to 0 each cycle, graceful shutdown. Also smoke-tested `--help` and the no-server
timeout path via the shipped exe.

**Decisions (implementation-level):**
- Tool lives under `tools/` (new root) — structurally obvious it never ships in the mod zip.
- LangVersion 10 gotcha: raw string literals are C# 11 → verbatim strings in tool code.
- Testing posture for M1.3 recorded: bot = daily rig; friend session = the milestone's official
  exit verification when next available.

**Next:** M1.3 — Shim presence, developed against the bot: host in-game → run
`LocoMP.Bot --at <your coords>` → watch the avatar orbit you.

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
