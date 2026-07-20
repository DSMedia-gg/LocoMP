# SESSION log — LocoMP

Append one entry per work burst (newest at top). Pair with `STATE.md` (current state) — this file is the
narrative history. See `../CLAUDE.md` for the discipline.

---

## 2026-07-20 — Real careers on the dedicated server (M6-B): the `--config` loader 📋

**Goal:** Cody: "let's do the next task." Offered the three cold-start candidates framed by testability;
Cody picked the **real career `--config` loader** — the game-free HALF of giving the dedicated server a
real DV career (real yards/jobs/licenses) instead of the Alpha/Bravo placeholder. The in-game EXPORTER
(Shim reads the live world) stays a later PC slice.

**Two design calls:**
- **Model the WHOLE `CareerConfig`, not just what the D13 builder emits.** The Shim `CareerConfigBuilder`
  skips job shapes (on a host, jobs come from native capture). But a dedicated server's core GENERATOR is
  the job source — so a `.lmpc` MUST carry `JobTypes`. The codec covers the full config surface so the
  eventual exporter has a complete target.
- **Binary `.lmpc`, not the JSON the stub hinted at.** A hand-rolled binary codec (magic "LMPC" + schema
  version over Core's PacketWriter/Reader) is zero-dep AND symmetric across the assembly boundary: the net8
  server reads it, the net48 Shim exporter writes it — exactly how `TopologyCodec`/`.lmpw` already serves
  both sides. JSON would need a parser net48/netstandard2.0 doesn't ship.

**Built:** `CareerConfigCodec` (Core/Protocol) — full round-trippable Write/Read, foreign/future/truncated
→ InvalidDataException. Server `--config <path>` loads it (file authoritative, incl. preset; unreadable →
notice + built-in default, never a crash) + `--dump-config <path>` writes the default `.lmpc` seed and
exits. `ServerOptions` + help updated.

**Verified (game-free):** `CareerConfigCodecTests` — full-field round-trip, a **round-tripped config
generates an IDENTICAL board** (same seed + config ⇒ same stream, the functional proof), and all three
rejection paths. Suite **172 → 177 ×3**, full sln **0 warnings** (Shim vs B99.7 — no Shim change).

**Snag (env, worth remembering):** a `LocoMP.Server` exe I'd launched detached in an EARLIER burst outlived
its shell and locked `LocoMP.Core.dll` in the Server's `bin` — which blocked EVERY build that touches the
server, including Core.Tests (it references the server for the integration tests), so the suite kept running
stale at 172. The Galleon gate blocks process-kill and I didn't substitute; Cody stopped it with
`taskkill //F //PID 34068` (Git Bash needs the `//` to stop MSYS mangling `/F`→`F:/`). Then the full build
went green. Lesson banked in STATE: use self-terminating exe runs (`--dump-config` exits) or stop them.

**Docs:** CHANGELOG entry, Server README (`--config`/`--dump-config` section + limitation), DefaultCareer
comment refreshed, STATE + SESSION. **PUSHED** `a47e080..5a396d5` (Cody's go — 3 dependency-ordered
commits: core+test → server → docs).

**Next:** the natural follow-on is the in-game EXPORTER (Shim: live DV world → `.lmpc`) — the other half of
a real dedicated-server career; needs the game.

---

## 2026-07-20 — Drivable server trains (M6-B.3): borrow the server's train and drive it 🕹️🚂

**Goal:** Cody — "continue on locoMP" with the constraint "I can't test in person this session; append the
runbook and verify what you can." Offered three game-free-verifiable slices; Cody picked **drivable server
trains** — the highest solo-testing payoff (you can *interact* with the server's trains, not just watch
them roll past). Prior work (M6-B.1/B.2) is all pushed; tree was clean at start (171/171).

**The design realization (why this was small):** M6-B.2 registered server trains under a sentinel owner
`ServerOwnerId = int.MaxValue`. That one choice pre-built the hard half of "let a player take one over":
- `PushServerSnapshot` already guards on `OwnerId != ServerOwnerId` → **the server stops driving the instant
  ownership flips**, no explicit stop.
- `HandleSnapshot`'s `IsCurrentFromOwner` already admits + relays the owner's snapshots → once the claimer
  owns the set, **driving is the existing owner-stream path, zero new code.**
- Ownership isn't a membership change (`WithOwner` keeps the epoch) → **hard rule 5's stale-snapshot
  invariant can't trip.** So the whole feature is four surgical changes + the headless proof.

**Built (Core/Server/Bot — no Shim, no game ref, no staging):**
- **Core:** `TrainsetRegistry.TryClaim` now admits a set owned by the server sentinel (still refuses a
  DIFFERENT real player's — no theft) + `SetOwner(id, owner)` for the park/reclaim leg TryClaim can't
  express. `ServerTrains`: `_serverOwnedSets`, `IsServerDriven(id)` (the freeze gate), `OnPlayerRemoved`
  reclaims a borrowed server train to the server rather than parking it dead, `HandleOwnershipRelease`
  (msg 63). `ClientTrains.ReleaseOwnership`. Protocol **v9 → v10**.
- **Server:** `ServerKinematicTrain.Tick` freezes while `!IsServerDriven` — on hand-back the train resumes
  from where it was borrowed (not a schedule position that ran on in parallel). The tidy discontinuity —
  adopting the driver's final pose — needs spline→parametric inversion; banked.
- **Bot:** `ClaimDriver` + `--claim-server-train` — join a `--spawn-trains` server, claim an ambient train,
  drive it along the topology (throwing junctions), release after `--drive-seconds` (Ctrl+C also hands it
  back via disconnect). The one-PC rig Cody watches from his own game.

**Verified (game-free, the point given no in-person testing):** `ServerOwnedTrainTests` reworked — the old
B.2 "a player CANNOT claim" test replaced by a full round-trip (`claim → server stops → the driver's
snapshot moves a WATCHER's replica → a 2nd player can't steal it → release → server resumes`) plus a
reclaim-on-disconnect test. Suite **171 → 172 ×3**, full sln **0 warnings** (Shim compiled vs real B99.7 —
no Shim change, but the gate held). The **server exe booted on protocol v10 driving 2 trains** clean.

**Blocked (flagged, not worked around):** the real-exe bot+server live smoke — the **Galleon hardware gate
denied launching the exes** (and per the no-bypass rule I didn't substitute a kill/relaunch). The headless
integration test drives the identical `RequestOwnership → SendSnapshot → ReleaseOwnership` wire path, so
coverage isn't lost; the live-exe smoke is offered to Cody to approve.

**Docs (the "append the runbook" ask):** new **`RUNBOOK-M6B-SERVER.md`** — the first structured in-game
smoke plan for the whole dedicated-server track (B.1 join/persistence, B.2 trains roll, B.3 claim/drive/
release + reclaim-on-disconnect), with a results table. Server README limitation + verification updated;
CHANGELOG entry added; STATE refreshed. **PUSHED** `5e57156..5d29963` (Cody's go — 3 dependency-ordered
commits: core+test → server/bot → docs).

**Next:** the in-game M6-B smoke pass when the game's up; then the natural follow-on — the **Shim UX** for a
real player to claim/drive a server train from inside DV (turns B.3's wire path into a game action).

---

## 2026-07-20 — Server-owned kinematic trains (M6-B.2): a fresh server drives its own trains 🚂

**Goal:** Cody: "keep working." After M6-B.1's dedicated server (trains came from a bot client), the
next step was to give the server its OWN trains so solo testing needs no bot. Offered three options;
Cody picked the **clean, server-authoritative** build (trains owned by the server's registry — no ghost
"player" artifact) over the quick internal-loopback-client reuse.

**Design (validated by reading ServerTrains/TrainsetRegistry):** the server registers a consist under a
sentinel owner `ServerTrains.ServerOwnerId` (`int.MaxValue` — never a real peer, never 0/parked). That
one choice does the heavy lifting: `TrainsetRegistry.TryClaim` refuses any set whose owner is non-zero
and not the claimer, so **no player can hijack a server train**; the join burst already sends every
registered set, so it reaches clients for free. Snapshots go through a new `PushServerSnapshot` that
bypasses the owner admission check (the server is the authority). A stray player couple/comms on a server
train routes to the dead sentinel peer — a harmless no-op (both transports ignore unknown peers).

**Built:**
- **Core (small):** `ServerOwnerId` + `SpawnServerOwned(cars)` + `PushServerSnapshot(snap)` on
  `ServerTrains`. NO new message types (reuses TrainsetCreate/TrainsetSnapshot), NO handler changes.
- **Server:** `ServerKinematicTrain` — walks the extracted topology with `TopologyWalker` and publishes
  spline-space snapshots via `PushServerSnapshot` (geometry mirrors the bot's ConsistDriver but
  server-shaped, driving `ServerTrains` directly, not a NetClient). `Program` gains `--spawn-trains N`
  (+ `--train-cars`/`--train-speed`/`--train-livery`); `--world` or a tests/data probe supplies the
  `.lmpw`; each train ticks with real dt in the loop.

**Verified (game-free — the point):**
- `ServerOwnedTrainTests` (loopback): a server train reaches a client and its pushed snapshots move the
  replica; it rides the join burst for a newcomer; a player's claim is refused (owner stays the server).
- Real-UDP integration test: a client joins and sees a server-owned train **visibly move** (front bogie
  changes edge / advances >1 m). Stable ×3.
- Suite **167→171**, full sln **0 warnings**. **Real-exe smoke:** `LocoMP.Server --spawn-trains 2` logs
  `driving 2 server-owned train(s) of 3 car(s) at 10 m/s along world-99-build2702.lmpw (2073 edges)`; a
  real bot client joined clean.

**Solo recipe is now bot-free:** `LocoMP.Server --port 8877 --spawn-trains 3`, then Direct-connect from
DV — trains roll through the valley, no bot. (README/CHANGELOG updated.)

**Deferred (documented):** player claim/couple of a server train (ambient for now); junction-throwing as
a server train crosses switches (movement is snapshot-driven, so it's cosmetic); real DV career export
(`--config`); container deploy. **Committed locally; push awaits Cody's go.**

---

## 2026-07-20 — Dedicated server pulled forward (M6-B.1): multiplayer becomes solo-testable 🖥️

**Goal:** Cody — "can we pull forward the dedicated server work? That would make testing 1 million times
easier as I don't have a friend to test with (won't until ~next weekend)." A headless server he joins
from his own game as a client turns most of the "needs a friend" joined-client surface (world handover,
reconnect grace, persistence, presence, the board) into solo work — and it's the real M6 Track B product,
not the ephemeral bot rig.

**Feasibility (investigated first, plan-mode + approved):** very high. Three of the four hard parts already
existed and were tested — NetServer + persistence run headless; the bot's `--listen` already ran NetServer
over real LiteNetLib UDP; the deterministic job generator is game-free Core (D13 reserved it for exactly
this). Two Explore passes + a Plan agent validated the integration flow and caught the gotchas:
- The core generator fills the board gated ONLY on `Stations>=2 && JobTypes>=1` — it runs on `Poll` with
  **zero players connected**, so a solo joiner gets a populated board.
- "First admitted peer = world source" is **inert** when `AcceptExternalJobs`/`AcceptExternalItems` are
  false (defaults) — assigned but never read. Server owns the board; trains come from a registering client.
- Handshake admits only on EXACT match of protocol/build/modVersion/modListHash — `""` hash is right for a
  bot; exposed `--modlist-hash` for the real game.
- **Trap:** a preset switch between runs throws from `CareerRegistry` restore **inside the NetServer ctor** —
  so the Program guards the preset before constructing and starts fresh on mismatch.

**Built (all game-free, `src/LocoMP.Server/`):** `Program.cs` (was an 8-line stub — the full wiring:
save load/restore with clean fallback on corrupt/foreign/preset-mismatch, config, UDP transport, 30 Hz
loop, autosave, Ctrl+C→SaveNow), `ServerOptions.cs` (mirrors BotOptions), `DefaultCareer.cs` (synthetic
starter board so it runs out-of-the-box), `ConsoleAdmin.cs` (stdin→queue drained on the loop thread;
status/save/stop/help), `README.md` (solo-test recipe + honest limits).

**Verified (game-free, the whole point):**
- `DedicatedServerIntegrationTests` — real LiteNetLib UDP in-process: a solo client joins and receives a
  non-empty board; the world survives a **cold restart** through the save file. Suite **165→167 ×3**
  (UDP tests stable ×3), full sln **0 warnings**.
- **Headless smoke on the built exe:** server binds UDP 18899 → a real `LocoMP.Bot` joins as id 1 over the
  wire → board generates 8 jobs → `status`/`stop` console admin works → graceful save (678 B) → restart
  **reloads** the world ("loaded world … Board: 8 job(s)"). This is exactly what Cody will run.

**Solo-test recipe (README):** `LocoMP.Server --port 8877`, then `LocoMP.Bot --host 127.0.0.1 --consist 3
--livery ...` (bot joins first = world source, provides a train), then join from DV as a second client.

**Deferred (documented):** server-owned kinematic trains (no bot needed — M6-B.2); a real DV career
exported from the game (`--config` is the reserved hook; today warns + uses the built-in default); container
+ SVHost deploy; `kick`/richer admin; interest management. Roadmap: re-sequences M6-B ahead of M5 to unblock
testing; changes no locked decision. **Committed locally; push awaits Cody's go.**

---

## 2026-07-20 — Perf baseline: measuring the §9 budgets (and re-pointing the roadmap) 📊

**Goal:** Cody: "do the next item." M4 scope is code-complete; its only remaining work is the in-game
smoke pass (needs the PC). M5 is fundamentally in-game UX. The genuinely-next thing I could *do and
verify game-free while Cody's away* was the audit's standing §6 recommendation: the §9 performance
budgets had "no measurement harness or recorded numbers." Building the harness also produces the data
that decides whether the audit's flagged M3-scope gap (M3.2 late-join snapshot) is actually pressing —
so it's the disciplined "measure before build" move, not a detour.

**Built (game-free, no production change):**
- **`CountingTransport`** — an `ITransport` decorator over the Loopback hub's server side that tallies
  every byte the server sends, bucketed by recipient peer + delivery method. Pass-through, so a real
  session runs over it unmodified; wire sizes come out deterministic and assertable.
- **`BudgetBench`** — seeds a mature world the real way (world-source host registers N consists with
  snapshots + M world items; the career board auto-generates jobs), then measures: late-join snapshot
  bytes across three world scales, per-message pose/snapshot relay sizes, a derived steady-state
  bandwidth model, and host `Poll()` µs. Records everything via `ITestOutputHelper`; **hard-asserts the
  deterministic size budgets, loose on machine timing.** Results transcribed to `docs/PERF-BASELINE.md`.

**Measured (protocol v9, dev workstation, Release):**
- **Late-join snapshot — 37 KB worst case** (60 trains / 360 cars / 100 jobs / 400 items / 16 players),
  **~270× under the 10 MB budget.** Scales ~linearly; you'd need ~16,000 consists to approach the cap.
- **Steady-state bandwidth — 6–42× OVER the 128 kbps budget** at scale (820 kbps @ 8p → 5,359 kbps @
  32p/200 trains) under the current broadcast-everything model (no interest management yet).
- **Host tick — ~25 µs/tick, ~80× under the 2 ms budget.**
- Per-message: pose relay 30 B, 5-car snapshot 107 B.

**The finding — measuring reversed the intuitive priority:**
- The audit named **M3.2 (join snapshot)** as the M3-scope gap → measured as a **non-issue** (270× under).
  Keep it deferred; only its *phasing* (not compression) is worth doing later, as an M5.1 loading-screen
  hook. A join queue has minor independent value; friend-scale-irrelevant.
- The **real** pressing gap is **interest management (D10)** — bandwidth blows the budget at scale,
  exactly as audit §6 warned. Already scoped as **M6 Track B**; this data says it should LEAD the scaling
  work and precede any 16+ tester session. Friend scale (≤8p ≈ 0.8 Mbps) is tolerable → **M5 alpha not
  blocked.**

**Not done / for Cody:** the ≤1.5 ms/frame *client* cost is game-side (Unity Shim work) — unmeasurable
headless; flagged in the doc for an in-game profiler pass before M5. Committed locally; **push awaits
Cody's go** (hard rule 7). No decision changed unilaterally — this is data feeding the existing plan.

**Verified:** full suite **165/165** (164 + the bench), game-free build **0 warnings**.

---

## 2026-07-20 — M4.6: Locked personal essentials (v9) + hide-native fix + comms perf fix 🔒

**Goal:** Cody: "let's keep working on LocoMP" (away 6 hrs — verify programmatically, collate testing
into the runbook). The working tree held a **complete, uncommitted burst** from the immediately prior
(pre-`/clear`) session; this session **verified it programmatically, documented it, and extended the M4
smoke runbook** — no new code written here. Code authored prior burst; everything below is what that
burst contains + how it was verified.

**Three cohesive threads in the tree (v8→v9, save schema v4→v5):**
1. **Locked "personal essential" world items (the feature).** A DV essential (Map, CommsRadio, wallet,
   Compass, DVGuide) set down in the world now syncs **look-but-don't-touch** — visible to everyone,
   pickup refused for all but its owner (who reclaims it natively, never over the wire). Core:
   `ItemRecord.WorldLocked` + `SpawnInWorld(…, locked)` + `TryPickUp` refusal (`"item N is a personal
   item — only its owner can take it"`) + save codec v5 byte + the register/broadcast wire byte
   (ServerItems/ClientItems). Shim: `ItemSync.IsEssential` (`InventorySpecs.IsEssential`) gates the
   lock, **but `IsJobItem` (JobBooklet/JobOverview/JobReport) is deliberately EXEMPT** — job paperwork
   is shared crew state (anyone reads the brief; the claim follows via career sync, not the paper), so
   it stays a normal shareable item. Lineage: not a corpus decision — an M4 refinement off the item
   recon's `BelongsToPlayer`/`IsEssential` finding (`../research/item-system-recon.md:55,66`). **Flag
   for Cody: confirm this is the intended slice + give the go to commit/push.**
2. **Host-native hide-not-destroy (correctness fix, ItemSync).** When a remote carries off the host's
   REAL item, `DespawnLocal` no longer `Object.Destroy`s it — it `SetActive(false)`s it and tracks it in
   `_hiddenNatives`. Destroying a real DV item fights `RespawnOnDrop` (→ "Cannot set parent while being
   destroyed", and essentials respawn). `ReShowNative` re-shows the SAME object at the new pose if the
   remote drops it back (no duplicate replica); Dispose reactivates any still-hidden natives so the
   host's world is whole on Leave. Replicas WE spawned are still destroyed outright. `_applying` guard
   intact (verified line 279).
3. **CommsRadioSync per-frame-scan fix (perf).** Discovery ran 3× `FindObjectOfType` **every frame**
   (three full-scene scans/frame = host FPS crater). Now throttled to 1 Hz and anchored on the
   always-active `CommsRadioController`, reading its public `rerailControl`/`deleteControl`/
   `crewVehicleControl` fields (populated even while a mode GameObject is inactive), hooking once.

**Verified programmatically this session (no VR / 2nd PC / in-game):**
- **Clean full-solution rebuild** (`dotnet build LocoMP.sln -c Release --no-incremental`, Shim included
  against real B99.7) = **0 errors / 0 warnings**. This confirms every game-API assumption the burst
  makes (`CommsRadioController.*Control`, `InventorySpecs.IsEssential`, `JobBooklet/Overview/Report`,
  `PresenceShim.ToLocalPosition/ToRotation`) — the check the 07-19 audit's subagent couldn't reach.
- **Game-free suite** = **164/164 ×3** (161 at M4.5 + 3 new: locked-pickup-refused, locked-flag
  survives capture/restore, locked-essential visible-but-unpickable over a session). Clean, no flakes.
- **Staged payload proven current by hash:** `LocoMP.Shim.dll` SHA-256 is byte-identical (`D3DD824D…`,
  113 664 B) across the fresh clean-rebuild `bin/`, `dist/LocoMP/`, and the live game `Mods/LocoMP/` —
  deterministic build ⇒ the DLLs Cody will test ARE the verified source. No re-stage needed.

**Not done (needs Cody / a PC):** commit + push (hard rule 7 — awaits go); the in-game smoke of all
three threads (added as **A5** + folded into **A1**/**A3** in `RUNBOOK-M4-SMOKE.md`); confirm intended
scope of the locked-essentials feature. **Open in-game unknown flagged in the runbook:** some DV
essentials auto-return to inventory on drop (RespawnOnDrop) and may never rest as a stable world item —
which essentials actually stay set-down is a live question for the smoke pass.

**Net:** M4.6 is code-complete + fully green headless + staged, byte-verified against the deploy. It sits
in the tree as one reviewable burst (code + these docs) awaiting Cody's review → go.

---

## 2026-07-20 — M4.5: Manual service — the recon that closed a scope item with (almost) no code 🔧

**Goal:** the last M4 scope item (07 §M4). Cody: "let's continue with LocoMP."

**Recon-first, and it paid the best kind of dividend — proving there's nothing to build.** Decompiled
the pit-stop system (`PitStop` / `PitStopStation` / `LocoResourceModule : CashRegisterModule` /
`CashRegisterWithModules`, plus `SimulatedCarPitStopParameters`) → `research/manual-service-recon.md`.
Manual service is the **inverse of comms-radio**: where the radio modes charged a direct
`Inventory.RemoveMoney` that D14's reconcile silently reverted (→ free, the M4.4 fix), manual service
runs the fee through **`CashRegisterWithModules.Buy()`** — one of the exact two `Buy` overrides
WalletMirror already patches (D14). So the refuel/repair fee has been economy-correct since the day
D14 shipped, on a feature not yet written. The full chain: hose flow only accumulates
`Data.unitsToBuy` (no money) → buy button → `Buy()` → `CashRegisterBase.Buy` consumes deposited till
cash → `GetBoughtResource()` realizes the price. And the till subtlety is already handled too — the
player deposits physical cash (`Wallet.TrySpend → RemoveMoney`) and `WalletMirror.Reconcile` guards on
`AnyRegisterHoldsCash()` so it never refunds mid-transaction. License: `GeneralLicenseType.ManualService`
already mirrors via D14 LicenseSync. Resource/damage STATE sync is genuinely out of scope — it lives
in `SimulatedCarPitStopParameters` (the loco-sim layer LocoMP doesn't replicate; remote cars are
kinematic ghosts), so synced fuel/damage has nowhere to land yet (seams banked for that milestone).

**The one edge, and Cody's call.** `PitStopStation.RefillAll()`/`RepairAll()` apply a full service
bypassing the buy button (free). Recon found they have **zero callers** in any B99.7 assembly (the
`Dev_LocoRefill*`/`Dev_LocoRepair*` console commands hit a different path entirely —
`ResourceContainerController`/`DamageController`). I surfaced this as verify-only vs a defensive guard;
Cody chose **guard it** — under D14's ledger-is-truth posture a session should never mint value for
free, even down a dead path.

**Built (defensive, small):**
- **`ManualServiceHook`** — Harmony prefixes on `RefillAll`/`RepairAll` (mirrors CommsRadioHook), armed
  only while a session is live (native = free in SP, as intended). Never suppresses — the guard bills,
  it doesn't block the service.
- **`ManualServiceSync`** — host-only. Sums the equivalent cost straight off the bay's own resource
  modules (`BuyMaxLimit` × `Data.pricePerUnit` over `ResourceTypes.Consumable`/`Damageable`) — so no
  re-implemented price formula, we bill exactly what the metered path would have — and burns it as a
  self-scope `FeeExternal`. Best-effort: an unknown/zero-priced module is skipped + logged.
- Wired into `SessionController` (host arms, client constructs disarmed for a symmetric lifecycle) +
  `Main` install. **NO Core change (reuses D14's `ReportExternalFee`), NO protocol bump, NO new game
  ref** (`PitStop*` in Assembly-CSharp, `ResourceTypes` in DV.ThingTypes — both already referenced).

**Verified:** `dotnet test LocoMP.NoGame.slnf -c Release` = **161/161 ×3** (unchanged — pure Shim, no
headless surface); `dotnet build LocoMP.sln -c Release` = full solution **0 warnings** (the new Shim
files compiled first try against the game assemblies — confirmed every signature before writing).
Payload staged to `Mods/LocoMP/` + `dist/LocoMP/`.

**Net:** this **closes the M4 scope** — items, world items, shops, comms-radio, manual service all
built or proven-covered. The only M4 work left is the batched in-game milestone smoke pass (manual-
service checks added to the M4 smoke section in STATE). Uncommitted; push awaits Cody's go.

**Snag:** none — bash couldn't resolve the SDK until `DOTNET_ROOT=/c/Users/User/.dotnet` +
`DOTNET_ROLL_FORWARD=Major` were set (noted for next session's build/test commands).

---

## 2026-07-20 — M4.4: Comms-radio actions for all players 📻

**Goal:** the last big M4 feature — rerail / delete / summon "for all players (… + fees)" (07 §M4).

**Recon (decompiled the three comms-radio modes + controller → `research/comms-radio-recon.md`):**
`RerailController`, `CommsRadioCarDeleter`, `CommsRadioCrewVehicle` — all in Assembly-CSharp (no new
ref). Two universal findings shaped everything: (1) each fires a public `Action<TrainCar>` success
event (clean capture), and (2) — the load-bearing one — **each charges its fee via a DIRECT
`Inventory.RemoveMoney`, not a cash register.** D14's WalletMirror only hooks register `Buy` and
reconciles native money to the ledger, so it was silently REVERTING these fees: the host rerailed /
deleted / summoned **for free** in a session. So "+ fees" wasn't polish, it was a correctness fix.

**Scoping (asked Cody, M4.2-style):** the feature is really 3 sub-slices and only 1–2 are one-PC
testable (remote initiation needs a real second comms radio). Cody picked **all three**. Built:

- **Sub-slice 1 — host fees.** `CommsRadioHook` (Harmony prefix per mode's `OnUse`, acting only in the
  CONFIRM state — the ChainHook pattern) + `CommsRadioSync`. The prefix snapshots the game's computed
  price (the mode clears it before the event); the success event fires it as `FeeExternal(target 0)`.
  The native `RemoveMoney` still happens but the reconcile reverts it, and the FeeExternal burns it
  once through the ledger — charged exactly once, gated by the native affordability check (which reads
  the mirrored balance). Rerail/delete/summon now cost money.
- **Sub-slice 2 — delete → removal.** A native delete only unbinds locally in TrainSync
  (indistinguishable from a distance stream-out), so the server kept the set and clients kept a ghost.
  `TrainsetRegistry.TryDeleteCar` (last car → `TrainsetRemove`; else survivors re-form a fresh set) +
  a world-source→server `CarDeleteNotice`. The host sends it on `CarDeleted` with the car id
  snapshotted BEFORE the destroy unbinds it (order: `DeleteCar` → `OnCarAboutToBeDestroyed` unbinds →
  `CarDeleted` fires; so the id must be captured in the prefix).
- **Sub-slice 3 — remote initiation.** On a joined client the target is a host-owned kinematic
  replica; the confirm prefix SUPPRESSES the local mutation and sends `CommsActionRequest`. The server
  routes `CommsActionCommand` to the car's sim owner (the M3.5c CoupleRequest pattern), which runs
  `CommsActionCommanded` → performs the real rerail/delete and charges the INITIATOR via `FeeExternal`
  gaining a **target peer** (exactly how D15 extended LicenseGrantExternal). Also generalized
  `WalletMirror` to joined clients (`isHost` param) so a client's money display + comms-radio
  affordability read the LocoMP wallet — it does NOT report its own register buys. Bot `--rerail
  <plate>` / `--clear <plate>` drive the wire path headlessly; the client-side `OnUse` interception is
  friend-session (a bot has no radio). **Remote SUMMON banked** — spawning a new car at a remote
  location with livery/garage resolution is a materially harder problem; host summon works + is
  charged.

**Design notes banked:** prices reimplemented from observed behaviour (clean-room, our own code) —
rerail `RoundToInt(Clamp(500+dist·150,0,RerailMaxPrice))` (HandCar/newbie free), delete
`playerSpawned?0:DeleteCarMaxPrice`, summon garage `summonPrice`. The affordability edge: a real
client is now gated by the wallet mirror, but a bot/mismatch could act then have the fee refused
server-side (the action already happened) — noted for the friend session.

**Verified:** `dotnet test LocoMP.NoGame.slnf` = **161/161 ×3** (156→161: delete-removal ×3, remote
comms routing, targeted external fee); `dotnet build LocoMP.sln -c Release` = **0 warnings**. NO new
game ref (all comms-radio types in Assembly-CSharp). Protocol **v7→v8** (FeeExternal format change).
Payload staged to `Mods/LocoMP/` + `dist/LocoMP/`. In-game verification (Runs A/B/C) rides the batched
M4 smoke pass.

**Snag:** `Object.FindObjectOfType` was ambiguous (UnityEngine.Object vs object) — added the
`using Object = UnityEngine.Object;` alias ItemSync already uses.

**Pushed** (Cody's go): 3 dependency-ordered commits `5f073f4` (core) → `887cd03` (comms feat) →
`f9981f7` (docs), `b83414a..f9981f7`. **Next: manual service** is the last M4 scope item; remote
summon + held-item display are banked follow-ons.

---

## 2026-07-20 — M4.3: Shops — "a client buys a lantern" 🛒

**Goal:** the highest-value remaining M4 slice, Cody's pick over comms-radio / held-item-display /
manual-service. It retires half the M4 exit demo (07 §M4): *a client buys a lantern and the cash
lands in the right wallet.*

**The key realization (recon-first):** the purchase TRANSACTION was already built and fuzzed in M4.1
— `ServerItems.HandlePurchase` does charge-then-mint (`ServerCareer.TryChargeShopPurchase` burns from
the buyer's policy wallet, THEN `Registry.SpawnInPossession` mints), so money + item move together
and a *client's* buy debits the *client's* wallet. `ItemSessionTests` already proved it. So this
slice was never about the transaction — it was the **catalog + front-end + one-PC driver** that makes
it reachable in-game. That reframing kept the burst small (like M4.2, almost no Core work).

**Recon (shop decomp dumps under `../scratch/decomp/`, clean-room STUDY):** `GlobalShopController.
Instance.shopItemsData` is a `List<ShopItemData>`; each has `.item.ItemPrefabName` + `.basePrice`
(dollars) + `unavailableDueToGameMode`/`careerOnly`/`ItemsInStock`. Both native spawners use the same
`Resources.Load(prefabName)` two-liner LocoMP already spawns items with (M4.2). Critically: **no
`DV.Shops.dll` exists — GlobalShopController compiles into Assembly-CSharp (already referenced)**, so
zero new game refs and no CI heredoc edits, same clean bill as M4.2.

**Built:**
- **Core (protocol v6→v7):** `MessageType.ItemShopCatalog` (59) ships the catalog (prefab→cents) in
  the join burst before the item burst — the exact shape `CareerState` uses for the license price
  catalog. `ServerItems.OnPlayerAdmitted` sends it from `ItemConfig.ShopPrices`; `ClientItems.
  ShopCatalog` mirrors it (cleared on `Reset`). +1 test (catalog delivered to every client on join):
  155→**156**. The purchase path itself is untouched.
- **Shim `ShopCatalogBuilder`** (host-only, read-only, NO Harmony): walks the live shops →
  `ItemConfig.ShopPrices`, skipping mode-unavailable + malformed rows, logging the count. Wired into
  the host's `ItemConfig` beside `AcceptExternalItems`. Compiled first try against B99.7 (the recon
  signatures were exact).
- **Panel `DrawShop()`:** a collapsible Shop (Buy per item at its price) + a "Drop here" button on
  each carried item (drops at my pose over the wire). Renders identically on host and client (both
  read `_client.Items.ShopCatalog`; the host joins its own hub). Item refusals feed the panel toast.
- **Bot `--buy <prefab>`** (+ reuses `--drop-after`/`--at`): buys once joined → the mint lands in its
  OWN possession (own wallet charged) → holds → drops at `--at`, where M4.2's host ItemSync
  materializes it. `RemoteActor` catches the mint via `ItemAdded` (possessed + owner == me), logs the
  debited balance. This is the one-PC win-condition driver: bot's wallet drops, host's doesn't, and
  the bought item flows through the already-proven M4.2 world loop back to the host.

**Design call (banked):** a client's purchase is a pure LocoMP mint, independent of the host's real
shop shelf. That's correct for the win condition (server-authoritative, money+item atomic) and keeps
the builder read-only — but the host's shelf stock and the LocoMP catalog can drift. Live stock/
restock replication is a later, non-exit-critical slice. The host buying NATIVELY is already covered
end-to-end (D14 money + M4.2 world capture), so it needed no shop code.

**Verified:** `dotnet test LocoMP.NoGame.slnf` = **156/156 ×3** (incl. the bot compiling); `dotnet
build LocoMP.sln -c Release` = full solution **0 warnings**. Payload staged to the game's `Mods/
LocoMP/` + `dist/LocoMP/`.

**Testing cadence (D8 batched, 2026-07-20):** in-game verification is deferred to the consolidated M4
milestone smoke pass — the shops Run-A checklist is banked in `STATE.md` beside M4.2's.

**Snag:** first full build failed — the `--buy` help text I added to `BotOptions.cs` had embedded
double quotes inside the verbatim usage string (`@"..."`), which need doubling; reworded without
quotes. (The recurring lesson: verbatim strings + quotes.)

**Pushed** (Cody's go): 3 dependency-ordered commits `8fd47af` (core) → `8870cd3` (shops feat) →
`d742960` (docs), `cc72abd..d742960`. **Next**, to close M4: comms-radio actions (summon/rerail/
delete + fees) + manual service. Banked item follow-ons: held-item avatar display (protocol v8),
live shop stock.

---

## 2026-07-20 — M4.2: Shim ItemSync, the world-item loop 🏮

**Goal:** wire M4.1's game-free Items core to DV's real handheld objects — the "drop a lantern,
another player picks it up" win condition, in-game. Cody was tired ("ELI5 please"), so I framed the
M4.2 cut as three furniture-sized options and he picked **Option 1: world-dropped items only**
(held-item avatar display and shop materialization deferred to their own bursts).

**Recon-first (the ~30 decomp dumps under `../scratch/decomp/`, all clean-room STUDY):** confirmed
every seam before writing a line. Headlines that shaped the design:
- Capture is ALL public events — `StorageBase.ItemAdded/ItemRemoved` on the world bucket +
  `ItemBase.AboutToBeDestroyed` (the item analog of `OnCarAboutToBeDestroyed`). **Zero Harmony**
  (Main.cs untouched — a first for a Shim sync class).
- Spawn is the game's own two-liner: `Resources.Load(itemPrefabName)` + Instantiate +
  `StorageController.AddItemToWorldStorage`; `ItemBase.Awake` self-assembles rigidbody/ECS/etc.
- **Keep-alive is FREE**: `ItemDisabler.OnItemDisablePositionUpdated` exempts any item where
  `!isOnDisablingStaticParent && item.BelongsToPlayer()`, and that parent flag is true ONLY on a
  paint station. So a replica spawned `BelongsToPlayer = true` on the normal world parent is
  auto-exempt from distance streaming-off — no proximity band (unlike cars), no patch, no per-frame
  SetActive fight. The recon's "items are SetActive'd, never destroyed" prediction paid off: item
  replication is strictly SIMPLER than the M3.5b car materialization.
- Defining-assembly check (ilspycmd): everything is in already-referenced Assembly-CSharp +
  DV.Inventory. **No new game reference, no protocol bump** — the cleanest possible slice.

**Done (155/155 ×3, full sln 0 warnings, STAGED to Mods/ + dist/):**
- `Shim/ItemSync.cs` — host-native capture (world source, D13 posture; join-time sweep like
  JobCapture) + a reconcile-materialization pass (both roles; 0.5 s + dirty-flag). `_applying`
  reentrancy guard (M2 idiom) so our own spawn/despawn never echoes back as a capture. `_spawnedIds`
  tracks OUR replicas so `Dispose` deletes only those and spares the host's real native items.
- Host `ItemConfig{AcceptExternalItems=true}` — the default gates external items OFF (dedicated-
  server posture) and would have refused the host's own registrations; same fix D13 made with
  `AcceptExternalJobs`. Caught by reading `ServerItems.HandleRegister` before building, not by a
  failed run.
- Host restore deliberately keeps the item store EMPTY (`ServerSaveData` items=null default) — the
  host-native posture (like trainsets): DV's own save persists the host's items, the sweep re-offers
  them. The LocoMP item save is the source only on the dedicated server (M6).
- `PresenceShim.ToAbsolutePose(Transform)` + `ToRotation(Pose)`; panel `Items — N in the world`
  line; bot `--grab-items` (+ `--drop-after`) as the one-PC pickup/drop driver (in RemoteActor).

**Learned / notes:**
- `ItemBase.AboutToBeDestroyed` is `Action<ItemBase>`, NOT the parameterless `Action` that
  `TrainCar.OnCarAboutToBeDestroyed` is — cost one compile round-trip (CS0029). Watch the arity
  when copying the car destroy-hook idiom to items.
- `StorageWorld` holds only `BelongsToPlayer` items (shelf/display items live outside storage until
  bought), so `StorageWorld.ItemAdded` is a clean "a player dropped something" trigger — no filter
  needed.

**Deferred (banked in STATE):** held-item avatar display (Option 2, protocol v7), shop
materialization (Option 3), joined-GAME-client native grab capture (friend session), item state
blob, containers.

**Process decision (Cody, this session):** in-game smoke runs are now **batched per MILESTONE, not
per burst** — each slice builds + stages + pushes on merit; live verification is one consolidated
pass at the milestone boundary. M4.2's Run-A checklist joins the pending M4 milestone smoke pass.

**PUSHED (Cody's go):** committed + pushed to `main` without waiting on the live run (same call as
the debt pass). **Next:** the batched M4 smoke pass when the game's up; meanwhile the M4.2 follow-on
options / comms-radio to finish M4.

---

## 2026-07-19 (late) — M4.1: the game-free Items core 📦

**Goal:** with D15 pushed and the item recon banked, build the M4 authority layer — an item
registry + protocol + persistence + fuzz, headless (no Shim), the way M2.1/M3.1 opened their
milestones. Cody picked M4 for the burst.

**Done — M4.1 (155/155 ×3, full sln 0 warnings, staged to Mods/ + dist/, PUSHED `6064759`/`31d1f5c`):**
- `Items/` domain mirroring `Trains/`+`Career/`: `ItemDef`/`ItemRecord`/`ItemRegistry` +
  `ItemConfig`. The registry mints its own `ItemNetId` (the recon confirmed DV has no per-instance
  id), enforces the single-location invariant (World-pose XOR one scope's possession) and carries an
  item-conservation oracle that is the exact shape of the ledger's money oracle. Possession routes
  through a new `ProgressionPolicy.InventoryScopeFor` — per-player private, shared-career pooled.
- Protocol v6 (msgs 50–58); `ItemCodec` shares the def between wire + save while each side writes
  its own location (wire = holder peer+name for privacy; save = scope key, which re-binds inventory
  on restart) — the exact JobState/JobSave split. LMPS schema v3→v4 (items half appended, v3 refused
  cleanly). `EconomyEventKind.ShopPurchase`.
- `ServerItems`/`ClientItems` wired into the session stack. Purchase charges the policy wallet
  (overdraft-refused) THEN mints, so money+item are atomic — the win condition: a client's buy
  debits the client's wallet. Pickup proximity-gated + exclusive; world register/despawn
  world-source-gated (M4.2 host capture, tested now); join burst + reconnect rebind.
- Tests: `ItemRegistryTests` (+2,000-op fuzz w/ save/restore round-trips) and `ItemSessionTests`
  (win condition, buy→drop→pickup lifecycle, host capture, proximity, cold-restart persistence,
  shared-career pooling).

**Design notes banked:** the item system is far friendlier than trains — no destruction storms
(ItemDisablerGrid only deactivates), all capture surfaces are public events, spawn-by-prefab-name is
native. So M4.2 (Shim ItemSync) is event hooks + `Resources.Load`, not the M3.5b materialization
machinery; a far-item keep-alive/exemption is the one wrinkle.

**Next:** M4.2 Shim ItemSync on the recon's event seams, then comms-radio actions for all players.

---

## 2026-07-19 (late) — O11/O12 resolved → D15 built; M4 opens with the item recon 🎁

**Goal:** Cody's direction for the burst: **M4 next**, **O12 accepted** (LMPS ratified), and an
**O11 decision in his own words** — "keep the host grant system, but gate it to what licences the
host already has. Also have a checkbox to automatically grant new licences to clients on
join/licence acquisition on host." Recorded as **D15** in 00; O11/O12 struck; 03 §7 amended.

**Done — D15 implementation (134/134 ×3, full sln 0 warnings, staged to Mods/ + dist/, uncommitted):**
- Core gate: a world-source grant to ANOTHER player now requires the license in the world
  source's own server-side scope — which the join-time native sweep already fills, so "licenses
  the host holds" was a server-known fact with zero new Shim plumbing, and the gate is
  cheat-proof by construction. Self-mirror grants stay scope-agnostic (D14: the game is the
  authority on its own acquisitions).
- `AutoGrantHostLicenses` on ServerCareer: admit-time copy runs BEFORE the career burst is built
  (inherited licenses arrive inside CareerState, not as N trailing updates); live propagation
  fires from both paths a license newly enters the host scope (native-mirror grant AND panel-shop
  purchase — the latter's native echo returns idempotent, so it needs its own trigger); the
  property setter sweeps connected players when flipped on mid-session.
- Panel: auto-grant checkbox (idle + live in the host section); the grant list switched from the
  price catalog to the host's HELD licenses — the UI can no longer even offer what the server
  would refuse.
- One M3.5c test reworked: `Host_grant_unlocks_a_license_gated_job_for_the_remote_claimant` now
  has the host self-grant first — the intended D15 behavior change, not a regression.

**Done — M4 opened (02 verification item 5 ANSWERED):** `../research/item-system-recon.md` + ~20
ilspycmd dumps in `../scratch/decomp/` (delegated recon agent; ilspycmd needs
`DOTNET_ROLL_FORWARD=Major` on this machine now). The item system is MUCH friendlier than trains:
no per-instance id anywhere (LocoMP mints ItemNetIds — the car-id pattern), all capture surfaces
are public C# events, spawn-by-prefab-name is the game's own pattern, and item distance streaming
merely deactivates (the M2 destruction-storm mechanism has no item analog). Shop purchases'
money legs already ride D14's wallet mirror. Full implications section in the report.

**Next:** M4.1 game-free Items core (ItemRegistry, protocol v6, policy-routed inventory, LMPS
persistence) → Shim ItemSync on the event seams. D15 smoke items ride the next game session
beside the debt-pass checklist (see STATE). Commit+push on Cody's go.

---

## 2026-07-19 — Debt/polish pass: the ledger gets swept 🧹

Cody's pick for the burst (over starting M4 or M3.2): stop and pay down the banked debts while
they're cheap. Every "banked/watch/unaudited" line across STATE, SESSION, and 08-RISKS went into
one ledger; everything solo-closable got closed the same day. 125 → 129 tests ×3, full sln 0
warnings, uncommitted.

**The audit that closed with zero code**: does native `AbandonJob` fine the host? Decompiled
B99.7 (ilspycmd, `scratch/decomp/`) says **DV has no flat abandonment fine at all** —
`JobDebtController` stages only the job cars' ACCRUED service costs (damage/fuel deltas, only if
> 0), identically for complete/abandon/expire. So the optimistic rollback seconds after a take
stages nothing, and a released remote claim stages the haul's real wear onto the host's career
manager — whose payment already rides the D14 `Buy` hook into the ledger as an external fee.
The scary-sounding debt was already fully plumbed; it just needed proving.

**The kindness fix**: a dead session now SAYS SO. `NetClient.Disconnected` (post-admission drops
only) → after a 3 s recovery window the panel flips to "SESSION LOST — Leave to restore your
world, then reload your save". No auto-Leave, deliberately: leaving re-enables native saving,
and doing that unattended in a session-mangled world is the exact leak SaveSuppressor exists to
block. The mid-load eviction from the M3.5b runs also got its root cause fixed — LiteNetLib's
5 s default disconnect timeout vs DV's load freezes; now 15 s both roles.

**The rig upgrades**: the listen-mode bot now EXECUTES remote couple/uncouple requests on its
consists (propose → server transaction → everyone converges) and adopts split/merge products by
lead-car id instead of re-registering a duplicate train — so the owner-side half of chain
interception, previously "friend session only", live-fires on one PC. And `--derail-car <n>`
streams a consist car off-rail at the `--at` anchor, making the never-fired null-track
SpawnLoadedCar leg testable on demand (RealCarSync announces it loudly; the ghost fallback is
its net). One debt re-audited and RETIRED rather than fixed: CabControlSync's "assumes controls
exist" was already null-safe end to end — the real gap was silent input drops, now logged once.

Docs: 08-RISKS gains **R16** (host-presence scoping — remote claims/consists follow the host's
world lifecycle; dedicated server is the real fix), R8/R10 refreshed; 00 gains **O11** (guest
progression on mature hosts) and **O12** (ratify the LMPS hand-rolled-codec deviation) — both
Cody decisions. Smoke checklist for the next game session is banked in STATE.

## 2026-07-19 — M3.5c runs day: eight findings, three passes — a friend can now WORK in your world 💰

The heaviest run cadence yet — one evening, eight live findings, every one root-caused and fixed
inside the hour, and all three runs green by the end. What the runs proved: a remote player can
claim a real job from the host's world, the host's own game validates their delivery (nobody gets
paid for an unfinished haul), the payout lands in the claimant's policy wallet with the host's
untouched — and both directions of multi-crew work: a remote hand drove the host's throttle by
wire (Run B), and the host drove a remote consist from inside its cab (Run C, 13 controls live).

The findings, in the order the runs surfaced them:
1. **Claim eligibility** — the bot claimed the lowest-id job blind; a fresh profile holds only
   the starting floor. Now it claims only what its licenses allow and logs exactly what each
   skipped job needs (which empirically settled: DV shunting jobs require the Shunting license).
2. **Host license grants** (new feature, not a test hack) — msg 42 gained a target peer; the
   host panel grants catalog licenses to connected players, charge-free and logged. This is the
   interim answer to a REAL design gap: a fresh guest on a mature world faces license-gated
   boards no starting wallet can unlock. The deeper progression question is flagged for 00.
3. **Ghost jobs** — the career save persisted available captured jobs; a re-host resumed entries
   whose native counterparts no longer existed (claimable, backed by nothing). Available externals
   are no longer persisted at all, and a resumed board reconciles against the live world.
4. **Route visibility** — the booklet the native claim-swap prints is the ONLY place DV shows the
   destination track, and a take-on-behalf prints none. The captured job now carries the
   booklet's essence (real track ids from the task tree) in its task param.
5. **Car identity** — "no loadable trains parked on the track": the warehouse machine services
   THE job's exact cars, not any empty of the right type. Route steps now carry car spans
   ("load [5× G-123 … G-130] @ SM-A1-L").
6. **Far-station expiry** — DV expired a Golden Falls job under its remote claimant while the
   host stayed at Steel Mill: the world's job lifecycle follows the HOST's presence. A natively
   dead external job now retracts even under a claim (the world is the truth; the claimant gets
   an explicit toast). Banked as the host-native mode's core limitation — remote claims are only
   reliable near the host; the dedicated server (M6) is the real fix.
7. **`--drive-car <plate>`** — "first loco on the wire" drove L-014 while the host stood by
   L-013. Explicit plate targeting (possible only because v5 put real GameIds on the wire).
8. **The spawn-cull war** — the joined-client cull of native spawns melted the client twice: DV's
   restoration controllers respawn their locos every frame (and flag them playerSpawnedCar, so
   even the narrowed filter lost). The cull is GONE — one world-clear at join, everything the
   client's world spawns afterwards coexists unsynced. You cannot win a deletion war against a
   live game's spawn systems; world suppression belongs to the dedicated server.

Also answered en route: the overview leaflet SURVIVES a programmatic take and self-cleans if put
through the validator (the pre-gate protects it for claimed jobs); and the warehouse machine has
no player concept at all — the take-on-behalf satisfies it by construction.

125/125 ×3 at close, full sln 0 warnings, zero LocoMP exceptions across every passing run.
Uncommitted; commit + push on Cody's go. Then M3.2 (deferrable) or M4 — items, where remote
booklets and remote warehouse ops live.

---

## 2026-07-19 — M3.5c: remote claim parity + multi-crew — friends become players, not spectators 🎛️

The pulled-forward M4 spine's second half. Recon-first again, and again the game handed over
first-class API for everything: `JobsManager.TakeJob(Job, bool)` is public and DV's "taken" is
global world state (no player context, no booklet print — a remote player's claim can be mirrored
natively with one call), `TryToCompleteAJob(Job)` returns a verdict from the game's own task tree
(the host can VALIDATE a remote claimant's turn-in without trusting them), and every cab control
in the game — throttle to firedoor — lives behind one uniform `OverridableBaseControl` surface
whose 42-value ControlType enum happens to fit the wire's byte.

**The recon also caught a real M3.5b bug before it could bite M3.5c.** Protocol v4 added car
identity + cargo to the defs, the saves, and the codec — but the REGISTRATION message (untouched
since M2.1) still stripped everything to kind+derailed. Every "real" remote spawn in the M3.5b
runs actually ran on the `LMP-N` fallback identity and spawned cargo-empty; the liveries worked
because Kind flows, which is why it looked right. M3.5c's whole job-paperwork story names cars by
GameId, so registration now rides the full CarDef codec — protocol v5, with a regression test.

**Remote claims (the core).** A joined player claims a captured job from the panel; the host's
JobCapture sees the claim broadcast and takes the job natively on their behalf. Their "Report
delivery" defers server-side into a `JobCompleteRequest` the world source answers from
`TryToCompleteAJob` — nobody gets paid for an unfinished haul, and the claimant's toast carries
the native verdict on a refusal (15 s timeout fails safe and retryable). The sharp-edged design
call: a RELEASED external claim — abandon, TTL, reconnect-grace lapse — retires the job
everywhere, because DV cannot re-shelve a taken job (abandoned ≠ available, the D14 lesson) and a
board must never advertise a job no world can deliver. Booklet materialization for remotes is a
deliberate M4 deferral: a booklet is an ITEM plus task-tree reconstruction, and M4 is the items
milestone — the panel is the remote claim UX until then.

**Multi-crew cab controls.** Holder side: sitting in a remote cab with the control grant, lever
moves ride the existing `ControlInput` routing to the sim owner. Owner side: every control on
owned cars is watched and committed values broadcast as `ControlState`, which the server stores
per (car, control) and replays in the join burst — a newcomer's replica levers match reality.
Input becomes state only through the owner (03 §3): the owner's apply deliberately lets its own
ControlUpdated fire so the committed value echoes to every replica; replica applies are
reentry-guarded and skipped while we're the occupying grant holder. Chain couples/uncouples on
remote cars are intercepted BEFORE physics (`ChainCouplerCouplerAdapter.TryCouple/TryUncouple` is
the single funnel under the chain FSM) and routed to the owner, whose native event drives the
normal proposal path — one authority chain, no second commit path.

**The rest of the debt list.** Live cargo syncs by 1 Hz polling (the derail-polling posture — no
delegate-signature guessing) and folds into the stored CarDef at the SAME epoch, so late joiners
and saves carry the live load with no schema change. The host's 2 s scan registers mid-session
spawns (job chains! crew vehicles) and — nicer — re-binds a consist DV's distance streaming
destroyed and rebuilt by matching car GameIds against the still-live server def, closing the
banked "respawn rebinding" debt instead of duplicating sets. Joined clients cull native spawns
after the world clear. The bot learned the whole remote-player repertoire: `--claim-first` /
`--report-interval` / `--abandon-after` for the career loop (host hauls, bot gets paid — the full
remote loop on one PC), `--drive` to grant-and-throttle a host loco (the host watches their own
lever move), and in `--listen` mode a granted player's throttle drives the bot consist's speed —
Cody can climb into its cab and drive it.

Tests 110 → **122/122 ×3** (deferred-completion ok/refusal/timeout, external death on
abandon/grace, control relay + join-burst replay, cargo folding, request routing, the
registration-identity regression), full sln 0 warnings, headless listen smoke green, payload
staged 16:51. Next: Cody's three runs per STATE (career loop / drive test / client cab), then
commit + push. Known unknown to watch: the physical overview leaflet's fate after a programmatic
TakeJob.

---

## 2026-07-19 — M3.5b: real-car replication — the ghosts get bodies 🚃

The pulled-forward M4 spine's first half, recon-first as always. The reflection sweep turned up
something better than hoped: everything this milestone needs is FIRST-CLASS game API. The
headliner is `CarSpawner.SpawnLoadedCar` — DV's own savegame-restore primitive takes a car id, a
guid, and *per-bogie* (track, span) placement, which means remote consists can be spawned exactly
where the sync stream says they are, carrying the source world's identity (the same ids job
booklets will name in M3.5c). No "closest track" guessing — which is precisely where the
incumbent's client-spawning bugs live (their issues #138/#139).

**Core (protocol v3→4, save schema v2→3):** `CarDef` gains `GameId`/`GameGuid`/`CargoId`/
`CargoAmount`. Every def-bearing message changes shape → deliberate incompatible bump. 110/110 ×3.

**Shim `RealCarSync`** replaces GhostConsists in TrainSync (the boxes remain as a per-set fallback
whenever a livery can't resolve — the bot's synthetic kinds keep the old rig intact). The one
design law: remote cars are **kinematic**. The owner's physics is the only truth; local rigidbodies
are frozen, deletion and auto-coupling are disabled, and the car root is driven by the same
12/s-lerp machinery the ghosts proved. Letting local physics argue with remote authority is the
incumbent's snap-back graveyard, and kinematic drive forecloses the entire class. Membership
transactions re-map live cars BY SERVER CAR ID across merges/splits (no despawn flicker), then
repair physical couplings to match the defs; adjacent cars couple via position-based `TryCouple`
so spawn orientation can't break it. A local player manually chaining onto a remote car gets an
immediate revert + honest log — the couple-request path is M3.5c's.

**The join story grew teeth.** A joined client's own SP world is NOT the session world: on join
the local cars are cleared and `SaveSuppressor` (one false-prefix on `SaveGameManager.SaveAllowed`,
the gate every save path consults) blocks native saving until Leave — a session can never leak
into the player's singleplayer save; reload the save afterwards and everything is back. The same
recon closed the flagged D14 debt for free: `WalletMirror` now hooks the static
`SaveGameManager.AboutToSave` and writes the true pre-session balance into any mid-session host
save, with the 0.75 s reconcile re-mirroring right after.

**The one-PC rig inverted.** The existing setup (Cody hosts, bot joins) already tests the
spawning path — RealCarSync runs for any set not locally bound, so the bot's train becomes real
cars on the HOST's screen once the bot passes `--livery` (the host log now prints a paste-me
`bot livery hint` with real ids). For the CLIENT side, the bot learned `--listen`: it hosts a
NetServer over the same Loopback+UDP composite the in-game host uses, and Cody's game JOINS it —
world clear, save suppression, and client-side spawning all exercised on one PC. Headless smoke
run of listen mode passed (self-join, registration with liveries + cargo, streaming).

Banked for M3.5c: input routing over control grants (you can sit in a remote cab, not drive it),
couple/uncouple requests, live cargo load/unload, dynamic registration of mid-session host spawns
(new job chains!), booklet materialization + the remote claim flow. Full sln 0 warnings, payload
staged 14:31. Next: Cody's two runs per STATE, then commit + push.

**Same-day run cadence: 3 in-game runs, 2 real design corrections, M3.5b CLOSED.**
- **Run A №1**: pipeline proven but the consist materialized ON Cody's train (the `--start-edge`
  hint points at the edge nearest the player = where his train sits); a scan-based TryCouple
  grabbed his coupler and the kinematic colliders stress-derailed his flatbed at 24.9 m/s. Fixes:
  spawn DEFERS while any position overlaps existing cars (`IsBoxOverlappingSimple`); adjacency
  coupling by EXPLICIT partner coupler (`CoupleTo`, never a scan); bogie `DistanceTrackingEnabled
  = false` (killed the traveller warnings). Bonus verified live: the D14 `AboutToSave` fix — a
  mid-session autosave wrote the real $10,000.
- **Run A №2 PASSED**: real cars rolling, correct liveries, interactable. Banked as accepted:
  inter-car gaps = bot artifact (synthetic 16 m car length — real replication uses real bogie
  data); remote cars phase through local vehicles (safer than shoving; collision authority later).
- **Run B №1**: world handover + save protection PASSED (39 cleared, remote cars excluded, save
  no-op, SP save verified intact) — but the consist spawned ~359 m out and **DV's distance
  streaming destroyed it within a second** (`preventDelete` does not cover ECS conversion — the
  M2 storm mechanism eating our own spawns). The fix IS the architecture: **proximity
  materialization** (D10's interest management, forced early) — consists are data always, real
  cars within 250 m, voluntary dematerialize past 330 m, DV kills = quiet stream-out + 10 s
  cooldown via per-car destroy hooks (with an our-own-delete guard).
- **Run B №2 PASSED — M3.5b closed**: "test success! even tested the culling and it works
  brilliantly! Consist popped out, i caught up, it rematerialised." The dematerialize →
  rematerialize round-trip worked on first try. Also banked: a mid-load server eviction
  self-healed by re-handshake (id 2 → id 3) — the grace/rebind machinery absorbing a real
  disconnect in the wild. Next: commit + push (Cody's go), then M3.5c.

---

## 2026-07-19 — D14: full wallet unification — run №1's leaflet casualty closes the economy seam 💸

M3.5a run №1: Phase 1 clean (board 1:1 with world jobs at SM on a new save, FH granted), then the
seam D13 left open bit exactly where it had to: Cody bought a license at the NATIVE career manager
(with the SP $2000), the native validator approved a take LocoMP hadn't licensed, the async server
refusal fired the optimistic rollback — and `AbandonJob` (DV's only "untake") **destroyed the
leaflet**; no booklet, nothing in the lost-items shed, and our "returned to the world" log line was
flatly wrong (abandoned ≠ available in DV). Root cause, not a bug: D13 unified jobs while licenses
and money still ran beside the policy layer.

Options put to Cody (suppress native buying / sync grants only / unify) → **D14: full wallet
unification** (00 has rationale + accepted costs). Built + tested same day, staged 13:14 via the
game-exit waiter:

- **Core protocol 42/43** (`LicenseGrantExternal` / `FeeExternal`, world-source-gated like
  JobRegister): grants are IDEMPOTENT and CHARGE-FREE (the register fee arrives separately —
  charging both ways was the double-bill trap), fees are policy-routed burns; +5 tests → **109/109
  ×3**, conservation oracle holds through both.
- **`LicenseSync`** (Harmony-free — `LicenseManager` exposes acquired EVENTS): native grants →
  server; server grants (panel shop, starting floor, resumed saves) → applied natively via
  `Globals.G.Types.TryGet*License`; join-time sweep mirrors a mature save's licenses (the host's
  progression is part of the world, same argument as its jobs). Host-only: applying grants on a
  JOINED client would write into that player's own SP save.
- **`WalletMirror`**: native `Inventory.PlayerMoney` = live view of the LocoMP wallet (saved on
  Host, restored on Leave). Finalized purchases captured by patching **both** `Buy()` overrides
  (`CashRegisterCareerManager` + `CashRegisterWithModules` — Buy is virtual; patching the base
  body catches nothing; cost read in the prefix because commit clears it). Reconcile every 0.75 s
  but only while no register holds deposited cash — the deposit → buy → leftover-return orderings
  race otherwise. Debt payments ride the same hook: a slice of the M3.5b audit landed free.
- **Validator pre-gate** (JobCapture prefix on `ProcessJobOverview`): a take the server would
  refuse is refused BEFORE the game consumes the overview — error sound, toast, leaflet in hand.
  With licenses unified, the license-refusal class is gone entirely; the backstop rollback now
  also retracts the board entry (no ghost jobs) and logs the loss honestly.
- Config: `MaxConcurrentClaims=99` in host-native mode (DV's own concurrent-jobs licensing
  governs; a stricter core limit refuses takes the native validator already allowed — and every
  such refusal used to cost a leaflet). General licenses joined the price catalog. New game ref
  `DV.Inventory` (targets ×2 + CI ×2).

**Learned:** DV's job/economy surfaces are event-rich — `JobLicenseAcquired`/`LicenseAcquired` and
the single `Buy()` commit point made this a subscription job, not a patch-hunt. Small trap: Unity's
overloaded `==` breaks C# null-flow analysis (`is null` in Shim code or eat CS8602). Accepted cost
flagged for M3.5b: a native mid-session game save can persist the mirrored balance into the SP save.

**Run №2 (same day): PASSED — "All testing done - Zero bugs or regressions!"** Full D14 surface
verified live: mirror boot, the run-№1 killer sequence (career-manager license buy → licensed
claim, leaflet safe), panel-shop reverse sync, haul/turn-in through the ledger, deposit/cancel
settling, persistence + grace, bot regression. Starting balance bumped to $2000 pre-run (Cody:
the wallet IS the license budget under D14; SP parity; hardcoded in CareerConfigBuilder — not in
GameParams, re-check on B100). M3.5a CLOSED. Committed as three dependency-ordered commits
(core → shim/mod → docs; per-feature splits would not have built). Push on Cody's go → M3.5b/c.

---

## 2026-07-18 — D13 + M3.5a: the 180 — native jobs, host-capture architecture 🔀

Cody called the UI/X question mid-evening: claiming in a UMM panel while the station computer
knows nothing is split-brain, and the virtual board (no cars to haul!) exposes it. Decision made
interactively: **full native, pull M4 forward**, and for the generation architecture **Route A —
host-native capture** (now **D13** in 00): DV's generator keeps running on the host; LocoMP
mirrors every job onto the server board; claims/completions/payouts ride the policy layer; the
M3.1 deterministic generator is reserved for the dedicated server. The amendment to 02 §4 is
recorded with rationale + accepted costs (not seed-deterministic in host mode; dual-source seam).

**Also live-found and fixed: the license deadlock.** DV grants Freight Haul at career start; our
career granted nothing → every board job locked behind licenses the $500 start can't buy.
StartingLicenses now come from the game's own `GetRequiredLicensesForJobType(Transport)`, applied
as an idempotent FLOOR on every connect — existing saves heal on the next join, no reset needed.

**The capture layer (one evening, recon-first):** `JobsManager.RegisterGeneratedJob` is the single
choke point every real job passes through (procedural AND savegame-loaded) — postfix + a join-time
sweep covers pre-session jobs. The native claim moment (`JobValidator.ProcessJobOverview` →
`TakeJob`) becomes an OPTIMISTIC server claim: native take proceeds instantly, and if the server
refuses (race/limit), the capture rolls the native job back with `AbandonJob` (reentry-guarded;
CareerRejected now carries the jobId precisely so this rollback knows its target). Turn-in
(`ValidateJob` → `Job.JobCompleted`) reports for payout; `MoneyPrinterJobValidator.PrintPayment`
is false-prefixed in-session so the wage lands ONLY in the LocoMP wallet — the host's SP economy
stays a bystander (03 §7). Native expiry retracts unclaimed board entries. Captured jobs are
EXEMPT from the proximity gate (the game's task tree is their validator) and hide the panel Claim
button — the booklet IS the claim UX now. Save schema bumped to v2 (JobDef.GameId).

Known M3.5b/c debts, banked in STATE: AbandonJob's debt-system side effects unaudited; remote
players are read-only on captured jobs until REAL-CAR replication replaces ghost consists (the
pulled-forward M4 spine); wage = base payment only. 104/104 ×3, 0 warnings, staged 22:36.

---

## 2026-07-18 — M3.3 BUILT: the career meets the game — real stations, real routes, one clean hook 🎫

M3.1 pushed first (`53d642b`, Cody's go). Then the game half, recon-first as always
(`scratch/dv-reflect.ps1`, reflection-only over the local install — clean-room safe).

**02 verification item 4, answered: interception is CLEAN.** Every one of DV's procedural job
generation paths — player entering a station's zone, `RegenerateJobs`, expiry-driven attempts —
funnels through `StationProceduralJobsController.TryToGenerateJobs`. One Harmony false-prefix
(`JobGenSuppressor.Active`, engaged on host AND join, released on Leave) turns the game's own
generation off for the whole session; `StopAll()` halts coroutines already mid-walk at session
start. Pre-session jobs are deliberately left alone — stale booklets on a desk beat deleting world
objects mid-join.

**The board runs on the real map now.** `CareerConfigBuilder` pulls stations (YardID + absolute
position in presence-pose space), route distances from the game's own
`JobPaymentCalculator.GetDistanceBetweenStations`, one job shape per (station × output CargoGroup)
so origins/destinations mirror actual cargo routes, license requirements from `LicenseManager`,
and the purchasable-license catalog with real prices from `Globals.G.Types.jobLicenses`. Payouts
are $100/car + $10/car/km feel-constants (the game's exact formula needs per-car cargo value data —
M4's item territory). Every reflected signature compiled against B99.7 on the first build — worth
doing recon before code, every time.

**Core grew two things the recon justified:** route-constrained `JobTypeSpec`s with
distance-scaled payouts, and a **server-side task proximity gate** — the claimant's own presence
pose (which the server already holds) must be within ~500 m of the task's station. Owner-reported
world state, validated by the server, no new trust surface. CareerState now carries the license
catalog too (a client can't render a shop from gate failures alone).

**UX + identity:** UMM panel career section (wallet/licenses, MY JOB rows with `Report <step>`,
claimable board, license shop, toasts), `PlayerKeyStore` GUID in persistentDataPath (survives
restages and game updates; delete the file = fresh career), per-preset `locomp-career-*.lmps`
saves with 2-min autosave + save-on-Leave, auto-resume unless "Fresh career" is ticked. Key
design call, banked: **host-mode resume restores the CAREER half only** — the host's live game
world is the physical truth and re-registers its consists fresh; restoring saved trainsets would
duplicate them as ghosts. The full-world restore stays the dedicated server's path (M6).

102/102 ×3, full sln 0 warnings. Payload staging waits on the game quitting (DLL lock — the M2
lesson, now with a background waiter). Next: Cody's in-game M3 run per the STATE.md checklist,
then commit + push.

---

## 2026-07-18 — M3.1 BUILT: game-free career core — jobs, economy, policy layer, persistence v1 💰

The M2 pattern repeats: the milestone's hard logic lands game-free first, fuzzed headless, before
any Shim work. Everything below runs in `pr.yml` with no game install.

**Protocol v3.** The handshake now carries a stable **player key** — profiles, wallets, and
reconnect grace need identity that outlives peer ids. Design rule worth remembering: the key
travels client → server ONLY. Within the grace window it IS the reclaim credential, so broadcasting
it would hand out impersonation; other players only ever see session peer ids + display names
(JobState re-broadcasts on claimant leave/rejoin keep those bindings fresh — a real bug the session
tests caught). Career messages 29–39; v2 clients still get a proper "protocol mismatch" reject.

**Career core (02 §4/§6).** `CareerRegistry` holds every rule (mirror of TrainsetRegistry's
"epochs live here and only here" discipline): exclusive TTL'd claims, license gates checked
server-side at claim time, per-player claim limits, strictly-sequential task reports, payout minted
on the final step. `ProgressionPolicy` makes D3 real — per-player (default) vs shared career is one
routing switch consulted by every wallet/license touch, not scattered ifs. `EconomyLedger` keeps
integer cents with mint/burn as the ONLY money paths, so the M3 economy invariant is exact
arithmetic: sum(balances) == minted − burned, asserted after every op in a 2,000-op fuzz in both
presets. Deterministic generation runs on hand-rolled xorshift32 — **System.Random's algorithm
differs between net48/Mono and net8**, which would have silently broken same-seed boards between
the host-embedded server and the dedicated one.

**Reconnect grace (07 §M3).** Disconnect starts a 10-min hold; claims stay bound to the KEY, so a
rejoin restores claim + progress + wallet + licenses exactly and the haul continues mid-job (tested
end-to-end). Grace lapse returns the jobs to the board for everyone.

**Persistence v1 (03 §7).** "LMPS" versioned binary store, hand-rolled over PacketWriter/Reader and
REUSING the wire codecs (store and wire can't drift). **Deliberate deviation, flagged for Cody:**
03 §7 sketched MessagePack; zero-new-deps + proven infra won, MessagePack stays reserved for the
bulk join-snapshot channel if it's ever needed. Atomic temp+rename writes with a rotating backup
chain; interval `Autosaver` shared by both frontends. Subtleties banked: deadlines persist as
REMAINING milliseconds (the monotonic clock restarts with the process); players online at save time
get a fresh grace hold on restore (a restart IS their disconnect); restore refuses a preset
mismatch loudly. The trains half saves defs + junctions + turntables + id counters AND the last
admitted snapshot per set — which now also rides the join burst reliable, so parked/restored
consists have positions before any owner streams (they previously didn't exist anywhere until then).
Cold-restart e2e test: build world → save → new server from bytes → rejoin → finish the job.

**100/100 tests (was 70), stable ×3, full sln 0 warnings, committed.** Next: M3.3 Shim career
integration needs the game — real stations/licenses into `CareerConfig`, and the 02 verification
item 4 recon (how cleanly DV's job generation can be intercepted). M3.2 (phased join snapshot +
queue) can ride behind it; the current burst is fine at friend-session scale.

---

## 2026-07-18 — M2 EXIT RUN №5: ALL PASSED — MILESTONE 2 CLOSED (one-PC wording) 🚂

Every criterion has its log line, zero LocoMP exceptions:
- `couple contact: car 77 (Rear) + car 78 (Front) at 0.0 m/s — proposing` → server merged into
  **set 20** (fresh id, 18+ cars — the epoch machinery minting exactly as designed);
- `uncouple: set 20 between index 17 and 18 — proposing` → clean split of the merged product;
- `car 78 derailed — reporting` (L-014, derailAllBogiesAtOnce) → `car 78 rerailed — requesting set
  rerail` — both fixed paths proven;
- grants swapped cleanly 77↔78; ghost consist 19 on the rails at edge 205 (hint 207) beside the
  player; no snap-back observed (Cody), no resyncs, no mid-session storms.
- Teardown at quit: TAMED — sets unbind with one log line each via the destroy hook; at most a
  single stray proposal per set (first coupler event can beat the destroy hook), bounded + harmless
  on a dying session. The closing `Bolt.SceneVariables` UnityException is the game's own teardown.

**M2 (THE hard problem + the extractor risk) closed in ONE DAY** — planning corpus to verified
in-game consist transactions. Exit wording upgrades to official at the first friend session.
Run cadence total: 5 in-game runs, 8 real bugs found + fixed (3 lifecycle, 3 event-translation,
1 spawn-placement, 1 publicizer). Next: commit + push (Cody's go), then M3 (World & jobs) planning.

---

## 2026-07-18 — M2 exit run №4 (coupling test): 3 bugs flushed out — all fixed, restaged

**What the log proved:** manual UNCOUPLE works end-to-end (`set 4 between index 4 and 5 —
proposing`, committed clean, no snap-back reported); grants kept flowing all session; ghost + hint
flow again fine.

**Three real bugs caught (this is what exit runs are for):**
1. **The storms are DISTANCE STREAMING, not quitting.** This one fired mid-session seconds after
   hosting: DV converts far-away cars to ECS entities and DESTROYS their GameObjects (also why the
   registered car count varies 74/83/89 per run) → genuine Uncoupled cascades from sets 5–15 while
   near-player sets 1–4 survived. Neither `scene.isLoaded` nor `CarAboutToBeDeleted` catches
   conversion. **Fix: per-car `TrainCar.OnCarAboutToBeDestroyed` hook** (fires for every destruction
   path) → despawn set + unbind that set with ONE log line ("left the streamed world — unbound";
   respawn rebinding = M3).
2. **Couple test produced ZERO proposals.** The old dedupe assumed the contact fires on BOTH
   couplers and let only the lower car id speak — if the game raises it on one coupler only, that's
   a coin-flip drop. **Fix: handle every event, collapse same unordered pair within 0.5 s
   (`Time.unscaledTime`) instead.**
3. **L-039's 98 km/h stress derail went unreported.** Derail polling lived inside the snapshot loop,
   which breaks out at the first un-capturable car — later cars were never polled (and the set may
   already have been storm-unbound). **Fix: poll derail transitions for every live car BEFORE the
   snapshot pass.**

70/70, 0 warnings, staged 18:21. **M2 exit still needs a green coupling run:** couple (expect
"couple contact … — proposing"), uncouple, derail (expect "car N derailed — reporting"), rerail
(expect "requesting set rerail") — near the player so streaming can't unbind the set under test.

---

## 2026-07-18 — M2 exit run №3: GHOST TRAIN VERIFIED IN-GAME 🎉 (exit scenario still to run)

**Cody: "looks good!"** — the ghost rig works end-to-end in the live game: host registered 15 sets /
89 cars, calibration 0.4 m, hint line `--start-edge 1176 (~4 m from you)` → ghost consist 16
created and placed on edge 205 (spatially adjacent to 1176 after the ~50 m trail warm-up — edge ids
are registry order, not spatial). Remote train motion through the full pipeline (bot walker → UDP →
server relay → epoch admission → spline eval → lerped boxes) is PROVEN.

**Two findings:** (a) the quit-time uncouple storm STILL leaked — scene-unload destroys cars BEFORE
the registry dies (WorldAlive true) and never fires CarAboutToBeDeleted; cascading splits minted set
ids into the 150s. Fixed with the precise Unity signal: `gameObject.scene.isLoaded` is false during
unload → `IsLeavingWorld` guard on both coupler handlers (built, 70/70, restage pending — the game
was still running and holds the DLL lock). (b) The GAME logged "Junctions hashes match '59887E…'" —
the same JunctionsHash our extractor banked; also TracksHash differed across two sessions
(`FDEBEB…` in run №2 vs `B77208…` in №1/№3) while numbering kept working — hash may fold in
session state; watch it, don't rely on it session-to-session (build-to-build comparison still the
intended use).

**Still open for the M2 exit:** the coupling scenario itself — couple two real consists, uncouple,
derail + rerail, no snap-back (the staged build is fine for it; the storm fix only affects quit).
Commit + push after that passes.

---

## 2026-07-18 — M2 exit run №2: ghost ALIVE but far away — three fixes, restaged

**Symptom:** still no visible ghost. **But the new logging worked exactly as designed:** the log
shows `remote consist 19 (3 cars) — ghost created` AND `ghost consist 19 is on the rails (edge 0)`
— the whole pipeline ran; the ghost spawned on **edge 0**, kilometres from Cody at (7908,132,7351).
The walker picks its start blind because LMPW carries no world coordinates.

Also surfaced: (a) hosting from the loading screen → `RailTrackRegistryBase.OrderedRailtracks`
NREs INTERNALLY (TrackRootParent not up) — my null-check wasn't enough; (b) same premature host
latched `_worldRegistered` with 0 sets ("no live trainsets"), never retried — the second manual
host that run worked (18 sets / 83 cars, calibration 0.1 m abs).

**Fixes:** (a) `TryBuild` wrapped in try/catch (registry getters throw mid-load = "still loading");
(b) `RegisterWorld` only latches once ≥1 set registered — retries silently until cars spawn;
(c) **ghost start hint**: host logs `ghost-train hint: --start-edge N (~D m from you)` (nearest
edge by track-origin distance — same paste-me pattern as `--at`), bot gains `--start-edge <id>`,
`TopologyWalker` takes an explicit start edge (falls back to its own pick when absent/unknown).
70/70, 0 warnings, restaged. Run №3: paste BOTH host log lines into the bot command.

---

## 2026-07-18 — M2 exit run №1: failed on ghost visibility — root-caused, fixed, restaged

**Symptom (Cody):** no ghost train, only the bot's avatar capsule orbiting.

**What the log proved WORKED:** host + track index (2,073/563 matching the extraction), 14 trainsets
/ 74 cars registered, **point-set space self-calibration: Absolute, 0.4 m error vs 9,910.6 m for the
local hypothesis** — the (edge, s)→world path is right; control grants granted/released cleanly on
three cab entries. Protocol v2 handshake on runtime build string worked.

**Root cause:** the session OUTLIVED its world. Quit-to-menu destroyed every car → every coupler
fired `Uncoupled` → proposal storm (all adjacent pairs, sets 1–14) → server committed the splits →
products referenced destroyed cars → resync spam. Then the world was reloaded but `_worldRegistered`
stayed true (no re-registration) and `TrackIndexMap` held DESTROYED RailTracks — the bot's ghost
registered + streamed fine, `TryGetLocalPoint` returned false on dead tracks, and the ghost boxes sat
unpositioned (invisible) at the origin. A silent failure by construction: nothing logged on the ghost
path.

**Fixes:** (a) `TrainSync` watches `RailTrackRegistryBase.Instance` — world death fires
`WorldUnloaded` once; `SessionController` closes the session with a clear message (deferred out of
the tick to avoid disposing mid-callback); re-hosting in the new world re-registers everything and
the bot reconnects + re-registers by itself (already-tested churn path). (b) All proposal paths +
transaction resyncs guarded by `WorldAlive` and a despawn set fed by `CarSpawner.CarAboutToBeDeleted`
(teardown storms can't become protocol traffic). (c) Ghost cars spawn INACTIVE until first
positioned; an all-unresolvable snapshot logs one loud "stale world map?" warning; ghost creation +
first placement are logged ("ghost consist N is on the rails (edge E)"). Never again invisible-and-
silent. Rebuilt 0 warnings, 69/69, restaged.

**Bonus banked:** quit-to-menu DOES route car teardown through coupler events; `CarSpawner`'s
`CarSpawnEvent` delegate = `(TrainCar car)`; grants confirmed working in-game (first M2.3 feature
with live proof).

---

## 2026-07-18 — M2.3 Shim train integration — CODE-COMPLETE, staged (uncommitted)

**Goal:** the in-game half of M2 — live consists on the wire in both directions, membership events
as transactions, junction/grant flows, supported-build gate, and the one-PC ghost-train rig.

**Done (game-free half — 69/69 tests, all headless-proven):**
- **Supported-build gate:** `PresenceShim.GameBuild` is now the RUNTIME `Application.version`;
  `SupportedBuilds = ["99-build2702"]`; on an unknown build the mod loads inert with a friendly
  panel message (no Harmony patches installed). Bot default build updated to match.
- **`Core.World.TopologyWalker`:** seeded kinematic traveller over extracted topology — head
  advance across nodes, junction-aware branch picks (facing = seeded choice + throw event,
  trailing = forced to the branch it came from), dead-end reversal, and `Behind(d)` resolving
  trailing points across edge boundaries off a trail history. The seed of the M6 kinematic coaster.
  6 new tests incl. a 10 km soak over the REAL extracted map (conditional on the dump).
- **`Bot --consist <n>`:** registers an n-car ghost consist, drives it along the real topology at
  `--consist-speed` (default 8 m/s), streams current-epoch spline-space snapshots, throws crossed
  junctions, re-registers after churn/reconnect. `--world <path>` / env / tests-data probing.
  2 end-to-end tests (admitted stream with 0 discards; churn re-registration).
- **Krafs.Publicizer gotcha (banked):** publicizing compiler-generated members surfaces event
  backing fields as same-name siblings → every `+=` dies with CS0229 ambiguity. Fix:
  `IncludeCompilerGeneratedMembers="false"` on the Publicize item.

**Done (Shim half — compiles 0-warnings vs B99.7, needs the in-game run):**
- **`TrackIndexMap`:** RailTrack↔edgeId (registry order = extractor numbering), Junction↔save-id,
  and (edgeId, s)→world eval via `EquiPointSet.GetPointIndexForSpan` + interpolation. Point-space
  (absolute Vector3d vs shifted-local) is an inference, so it SELF-CALIBRATES from the first real
  bogie sample and logs which fit won — a wrong guess costs a log line, not a broken render.
- **`JunctionHook`** (replaces WorldStateSpike, which is deleted): patches ONLY the inner
  `Switch(SwitchMode, byte)` overload (M2.1 finding), `ApplyRemote` = FORCED switch with hook
  suppression so server echoes never loop.
- **`GhostConsists`:** box-per-car visuals for remote sets (amber loco, slate wagons), placed from
  two bogie spline points, avatar-style 12/s lerp + 80 m snap; derailed cars use the 6-DOF pose.
- **`TrainSync`:** host registers every game `Trainset` (token = game set id + 1); 20 Hz capture of
  bound sets (front/rear bogie `traveller.Span` + `TrackDirectionSign`-signed speed; derailed cars
  stream absolute pose); membership via the game's OWN public events — `Coupler.Coupled/Uncoupled`
  (EventHandler args, deduped by lower-car-id, translated to trainset-relative ends) — derail by
  POLLING `car.derailed` per tick (no guessing at custom delegate signatures); junction hook →
  `ThrowJunction`; `PlayerManager.CarChanged` → grant request/release; commits rebind bookkeeping
  only (host physics already happened). Wired into SessionController (host=true / join=false).
- New game refs ×4 spots: DV.PointSet, net.smkd.vector3d, DV.ThingTypes.

**API intel (reflection-only, banked):** `Trainset` {cars, id, allSets, Merge/Split}; `Bogie`
{track, traveller.Span, TrackDirectionSign}; `Coupler` {train, coupledTo, Coupled/Uncoupled +
CoupleEventArgs/UncoupleEventArgs {thisCoupler, otherCoupler, viaChainInteraction,
dueToBrokenCouple}}; `TrainCar` {trainset, derailed, FrontBogie/RearBogie, carLivery.id, couplers};
`CarSpawner` {CarSpawned, CarAboutToBeDeleted}; `PlayerManager` {Car, CarChanged};
`EquiPointSet` {points[{position:Vector3d, forward, span, spanToNextPoint}], span,
GetPointIndexForSpan}; Vector3d lives in net.smkd.vector3d.dll.

**Next (the M2 exit run, Cody at the PC):** host → check `[trains]` registration + calibration
lines → run `LocoMP.Bot --consist 3 --at <coords>` → ghost train visible and rolling, switches
flipping ahead of it → drive your own loco (bot sees snapshots) → couple two consists, uncouple,
derail + rerail — watch for clean transactions, no snap-back, zero exceptions. Then commit + push.

---

## 2026-07-18 — M2.2 world extractor — CLOSED (real extraction + exit test green)

**In-game run (Cody):** loaded a world, hit the panel button. `world-99-build2702.lmpw` (25,217
bytes): **2,073 edges / 279.6 km / 1,886 nodes / 563 junctions, all health counters zero** —
0 position mismatches (the `Branch.first` = IN-end reading is now empirically proven against the
live game), 0 zero-length edges, 0 skipped or duplicate-id junctions, Player.log exception-free.
Banked for B100 comparison: TracksHash `B77208E53A86A3B95DE74FF0BEE9B093`, JunctionsHash
`59887E6A2C1E9ED9940EB10DCB51F4F6`. Dump copied to `tests/data/` → `RealWorldTopologyTests` went
live and passed (scale, id discipline, junction-node consistency, one dominant component): **the
M2.2 exit — Core loads the REAL extractor output — is met.** 61/61. The riskiest dedicated-server
dependency (03 §6) is retired at prototype level. Next: M2.3.

---

## 2026-07-18 — M2.2 world extractor (code half) — built + staged

**Goal:** the Shim-side extractor (03 §6): dump the live RailTrack graph to LMPW so the headless
dedicated server can load the real world. Riskiest dedicated-server dependency — prototype level.

**Done:**
- **B99.7 API nailed by reflection-only inspection** (banked for M2.3 too):
  `RailTrackRegistryBase.Instance` (via DV.Utils `SingletonBehaviour<T>`) exposes `OrderedRailtracks`
  + `OrderedJunctions` — the game's OWN stable ordering — plus `TracksHash`/`JunctionsHash` for
  cross-extraction comparison. `RailTrack`: `curve.length` (BezierCurves.dll), `inBranch`/`outBranch`
  (`Junction.Branch { RailTrack track; bool first }`), `GetInNodeT()`/`GetOutNodeT()`. `Junction`:
  `inBranch`, ordered `outBranches` (order is load-bearing — `selectedBranch` indexes it),
  `junctionData.junctionId` (the save format's stable ids; protocol VarUInt-compatible).
- `LocoMP.Shim.TopologyExtractor`: EdgeId = `OrderedRailtracks` index (the M2.3 `BogieState.EdgeId`
  numbering); nodes = union-find over 2N track endpoints joined via the game's Branch pointers
  (track↔track + junction fuses entry+branches into one node); junction ids = `junctionId` with
  dedupe; writes `world-<Application.version>.lmpw` via TopologyCodec into the mod folder.
  **Every union is positionally verified** (in/out node transforms ≤ 1 m apart) — this empirically
  proves the `Branch.first` = "target track's IN end" reading against the live game; mismatches log
  a do-not-trust warning. Health counters logged: mismatches, zero-length edges, skipped junctions.
- UMM panel: "Extract world topology" button (Main dev-tools row, under the session panel).
- New game refs ×4 (local targets, .EXAMPLE, build.yml, release.yml heredocs): DV.Utils, BezierCurves.
- `RealWorldTopologyTests` (M2.2 exit): loads `tests/data/world-*.lmpw` (git-ignored, or
  `LOCOMP_WORLD_FILE` env override) and asserts scale (>500 edges, >50 km, >30 junctions),
  sequential edge ids, unique junction ids, every junction's entry+branches sharing one node, and
  ≥50% of edges in one connected component (turntable stubs may be islands — turntable links are
  session state, not topology). Passes vacuously with no dump so pr.yml stays game-free.
- Full sln Release 0 warnings; **61/61** game-free tests; payload staged to `Mods/LocoMP/`.

**Next:** in-game extraction run (Cody: load world → Ctrl+F10 → Extract) + name-tag visual check,
copy the .lmpw to `tests/data/`, re-run the real-file test = M2.2 exit; then commit. Then M2.3.

---

## 2026-07-18 — M2.1 in-game regression + UDP proof + push

**Goal:** verify M2.1 in a live session (regression: the v2 protocol + new train plumbing must not
destabilize presence), close the real-UDP gap for train flows, then push.

**Done:**
- `TrainUdpIntegrationTests`: register → relay → couple → stale-stamp drop → re-baseline over REAL
  localhost UDP (**60/60**). Stale stamp fired only after merge convergence — transactions and
  snapshots ride different UDP channels with no cross-channel ordering; the in-flight race stays the
  Loopback fuzz's job.
- In-game run (Cody): mod loaded on protocol v2, hosted on 8877, bot joined/left twice over UDP,
  avatars + tags fine, `Player.log` exception-free.
- **Junction double-fire CONFIRMED** by a controlled single throw: player throw = 2 hook fires (the
  two `Switch` overloads chain); game-internal sets hit only one. M2.3: hook the inner overload only.
- Name-tag shadow still read as doubled text up close (0.048 offset + 0.03 depth parallax) →
  tightened to 0.012/0.004. Visual re-check on the next game run.

**Next:** M2.2 world extractor. See `STATE.md` → Next.

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
