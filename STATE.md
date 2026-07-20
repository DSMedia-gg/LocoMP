# STATE — LocoMP (implementation)

**Updated:** 2026-07-20 — **M6-B.2: server-owned kinematic trains BUILT + verified headless (clean,
server-authoritative). Committed locally, push awaits Cody's go.** Cody picked the clean build over the
quick internal-client hack. Now a fresh `LocoMP.Server --spawn-trains N` **drives its own trains** along
the extracted topology — **no bot needed** — retiring M6-B.1's "a fresh server has no trains" limitation.
- **Core (small, clean):** `ServerTrains.ServerOwnerId` (=`int.MaxValue`, never a peer, never 0) +
  `SpawnServerOwned(cars)` + `PushServerSnapshot(snap)`. Server-authoritative: the consist is registered
  under that sentinel owner, so `TrainsetRegistry.TryClaim` (owner≠0 → refuse) **can't be hijacked** by a
  player; snapshots bypass the owner admission check (the server IS the authority). **NO new message
  types** (reuses TrainsetCreate/TrainsetSnapshot; the join burst already sends all sets), NO handler
  changes. A player couple/comms on a server train routes to the dead sentinel = harmless no-op (both
  transports ignore unknown peers) → server trains are ambient (player-takeover is a later refinement).
- **Server:** `ServerKinematicTrain` (walks the topology via `TopologyWalker`, publishes snapshots through
  `PushServerSnapshot` — geometry mirrors the bot's ConsistDriver but server-shaped, no NetClient) +
  `Program` wiring (`--spawn-trains N`/`--train-cars`/`--train-speed`/`--train-livery`; `--world` or a
  tests/data probe supplies the `.lmpw`; ticked each loop with real dt).
- **Verified (game-free):** `ServerOwnedTrainTests` (spawn reaches a client + snapshots move it; rides the
  join burst; unclaimable) + a real-UDP integration test (a client sees a server train **visibly move**).
  Suite **167→171 ×3** (UDP test stable ×3), full sln **0 warnings**. **Real-exe smoke:** `--spawn-trains 2`
  logs `driving 2 server-owned train(s) … along world-99-build2702.lmpw (2073 edges)`; a real bot joined
  clean. Docs: README recipe is now bot-free; CHANGELOG updated.
- **Deferred:** player claim/couple of a server train; junction-throwing as the train crosses switches
  (movement is correct via snapshots regardless); real DV career export (`--config`); deploy.

**Prior (2026-07-20): M6-B.1: dedicated server PULLED FORWARD + BUILT + smoke-verified headless.
PUSHED** (`8409bac` feat / `b38b7df` docs, `3c8a634..b38b7df`, Cody's go). Cody has no test partner until ~next weekend, so we pulled
the M6 Track B dedicated server ahead of M5 to make multiplayer **solo-testable**: a standalone game-free
process he joins from his own game as a client, against a world that persists across game/server restarts.
Feasible now because the hard parts already existed and are tested (NetServer + persistence run headless;
the bot's `--listen` already ran NetServer over real UDP; the deterministic job generator is game-free
Core, D13-reserved). This slice is **wiring**, not new networking. What shipped (all in `src/LocoMP.Server/`,
game-free):
- **`Program.cs`** (was an 8-line stub): loads/creates a `.lmps` save (corrupt/foreign/preset-mismatch →
  clean fresh start, never a crash — the preset guard dodges the `CareerRegistry` restore throw *in the
  NetServer ctor*), builds the config, opens `LiteNetLibTransport.StartServer`, runs a 30 Hz loop
  (`Poll` + `BroadcastTime` every 5 s + `Autosaver.Tick`), primes the board with one `Poll` so the banner
  count is real, Ctrl+C / `stop` → `SaveNow` before `Dispose`.
- **`ServerOptions.cs`** (mirrors BotOptions): `--port/--key/--save/--world/--config/--password/
  --max-players/--build/--mod-version/--modlist-hash/--name/--autosave-seconds/--preset/--tick-hz`.
- **`DefaultCareer.cs`**: a synthetic starter board (4 stations, 3 license-free job types, target 8) so it
  runs out-of-the-box — the core generator fills the board on `Poll` with **zero players connected**, so a
  solo joiner arrives to a populated board.
- **`ConsoleAdmin.cs`**: stdin thread → queue drained on the loop thread; `status`/`save`/`stop`/`help`.
- **`README.md`**: the solo-test recipe + the honest limitations.
- **Verified programmatically (game-free):** `DedicatedServerIntegrationTests` (real UDP, in-process) — a
  solo client joins and gets a non-empty board; the world survives a **cold restart** through the save
  file. Suite **165 → 167 ×3** (UDP tests stable ×3), full sln **0 warnings**. **Headless smoke on the real
  exe:** bind UDP → a real `LocoMP.Bot` joins as id 1 over the wire → board = 8 jobs → `status`/`stop`
  console admin → graceful save (678 B) → **cold reload** ("loaded world … Board: 8 job(s)").
- **The honest limitation (in the README):** a fresh server has **no trains of its own** — trains come
  from a client that registers them (a bot joining first is the world source; the "first peer = world
  source" concept is otherwise inert with `AcceptExternal*` off). Server-owned **kinematic** trains (no bot
  needed) are the next slice (M6-B.2). The career board is a synthetic placeholder; a **real DV career
  exported from the game** (like the topology `.lmpw`) is a Shim slice — `--config` is the reserved hook
  (currently warns + falls back to the default).
- **Roadmap:** this re-sequences M6-B ahead of M5 to unblock solo testing; changes no locked decision.
  Plan at `../` planning corpus; approved by Cody. Push awaits Cody's go (hard rule 7).

**Prior (2026-07-20): Perf baseline harness BUILT + measured (the audit §6 "no numbers" gap);
game-free, 165/165. PUSHED** (`2cf3d58` test / `3c8a634` docs, `fecc886..3c8a634`, Cody's go). Closes the recurring "budgets have no
measurement harness or recorded numbers" finding and — the point — produces the data that decides what
to build next. `tests/…/BudgetBench.cs` + `CountingTransport.cs` (an ITransport decorator that weighs
every byte the server sends; zero production change); results written to `docs/PERF-BASELINE.md`.
Headline: measuring **reversed the intuitive priority**.
- **Late-join snapshot: 37 KB worst case (60 trains/360 cars/100 jobs/400 items/16 players) — ~270×
  under the 10 MB budget.** So **M3.2 (phased/compressed join) is correctly deferred** — compression is
  a non-issue for any realistic world; only its *phasing* is worth doing later (as an M5.1 staged-loading
  hook), not its compression. The would-be "M3 gap" is measured as a non-gap.
- **Steady-state bandwidth: 6–42× OVER the 128 kbps budget at scale** under the current broadcast-all
  model (0.8 Mbps @ 8p → 5.4 Mbps @ 32p/200 trains). **Interest management (D10) is the genuine next
  architecture priority** (already M6 Track B; this says it should LEAD scaling). Friend-scale (≤8p) is
  tolerable, so the M5 alpha is NOT blocked; the 32-player ceiling is unviable without relevance filtering.
- **Host tick: ~25 µs/tick, ~80× under the 2 ms budget** — no concern.
- **Not measured (needs the game):** the ≤1.5 ms/frame *client* cost (Unity-side Shim work) — flagged in
  the doc for an in-game profiler pass before M5's alpha bar.
- **Roadmap effect:** keep M3.2 deferred; when M5.1 wants a loading bar, do join *phasing* only. Prioritize
  interest management (M6-B) ahead of any 16+ tester session. No decision changed unilaterally — this is
  data feeding the existing plan; flagged for Cody.

**Prior (2026-07-20): M4.6 (Locked personal essentials, v9) — BUILT (pre-`/clear` burst) + VERIFIED +
DOCUMENTED + PUSHED** (`2ca3ff5` feat / `fecc886` docs, Cody's go; `bdc9f24..fecc886`). A `/clear`-
interrupted burst was found complete in the working tree; verified programmatically and written up.
Three threads, protocol **v8→v9**, save schema **v4→v5**:
- **The feature — look-but-don't-touch essentials.** A DV personal essential (Map/CommsRadio/wallet/
  Compass/DVGuide) set down in the world syncs LOCKED: visible to all, pickup refused for everyone but
  its owner (who reclaims it natively, never via a wire request). Core: `ItemRecord.WorldLocked` +
  `SpawnInWorld(locked)` + `TryPickUp` refusal + save-codec v5 byte + register/broadcast wire byte.
  Shim `ItemSync`: `IsEssential` (`InventorySpecs.IsEssential`) sets the lock, **`IsJobItem`
  (JobBooklet/Overview/Report) EXEMPT** — job paper is shared crew state, stays shareable. **Lineage:
  not a corpus decision; an M4 refinement off the item recon's `BelongsToPlayer`/`IsEssential` finding
  (`../research/item-system-recon.md:55,66`). FLAG FOR CODY: confirm intended + go to commit/push.**
- **Host-native hide-not-destroy (correctness).** A remote carrying off the host's REAL item now
  `SetActive(false)`s + tracks it in `_hiddenNatives` instead of `Object.Destroy` (destroying fights
  DV's `RespawnOnDrop` → "Cannot set parent while being destroyed"). `ReShowNative` re-shows the SAME
  object on drop-back (no duplicate); Dispose reactivates still-hidden natives. Replicas we spawned are
  still destroyed. `_applying` guard intact.
- **CommsRadioSync perf fix.** Discovery ran 3× `FindObjectOfType` per FRAME (host-FPS crater); now 1 Hz,
  anchored on the always-active `CommsRadioController`'s public mode fields, hooked once.
- **Verified programmatically (no VR/2nd-PC/in-game):** clean full-solution rebuild (Shim vs real
  B99.7) = **0 warnings**; game-free suite **164/164 ×3** (+3 locked-item tests over 161); staged
  `LocoMP.Shim.dll` SHA-256 **byte-identical** across fresh `bin/`, `dist/`, and live game `Mods/`
  (deterministic build ⇒ deploy == tested source, no re-stage). Smoke checks added to
  `RUNBOOK-M4-SMOKE.md` (**A5** + folds into **A1**/**A3**). **Open in-game unknown:** some essentials
  auto-return to inventory on drop (RespawnOnDrop) → may never rest as a world item; which ones stay
  set-down is a live question for the smoke pass.

**Prior (2026-07-20): M4.5 (Manual service) — recon = VERIFY-NOT-BUILD, then a small
defensive guard BUILT + PUSHED** (`0e65c0a` feat / `8c70a0b` docs, Cody's go). Recon (`research/manual-service-recon.md`) settled the last M4
scope item: manual service is the INVERSE of comms-radio. The metered loop (turn a valve → deposit
cash → hit Buy) commits through **`CashRegisterWithModules.Buy()`** — one of the two exact `Buy`
overrides D14's `WalletMirror` already patches — so the refuel/repair fee has been economy-correct for
free since D14 shipped. NO direct `RemoveMoney`, so no free-service hole in normal play. The
`ManualService` license is a `GeneralLicenseType`, already mirrored both ways by D14 `LicenseSync`.
Resource/damage STATE sync (fuel/water/coal/sand/damage visible to others) is deliberately OUT of
scope — it lives in the loco-sim layer LocoMP doesn't replicate yet (remote cars are kinematic
ghosts); public seams (`LocoResourceModule.OnResourceBought`, `SimulatedCarPitStopParameters.
ParametersUpdated`) are banked for when it lands. **The one edge — `PitStopStation.RefillAll()`/
`RepairAll()` bypass the buy button (free full service) — now has a guard** (Cody's pick over
verify-only): recon found these have ZERO callers in any B99.7 assembly (a scene UnityEvent or a
future free-service mode is the only way they'd fire), but under D14's ledger-is-truth posture a
session must never mint value for free, so a host-side Harmony prefix (`ManualServiceHook`) bills the
equivalent cost — read straight off the bay's own resource modules (`BuyMaxLimit` × `Data.pricePerUnit`
per resource, so no re-implemented price formula) — as a self-scope `FeeExternal`
(`ManualServiceSync`). Never suppresses the service, just bills it; disarmed outside a session (native
= free, as SP intends). **NO Core change (reuses D14's `ReportExternalFee`), NO protocol bump, NO new
game ref** (`PitStop*`/`LocoResourceModule` in Assembly-CSharp, `ResourceTypes` in DV.ThingTypes — both
already referenced). Tests unchanged **161/161 ×3** (pure Shim, no headless surface), full sln **0
warnings**, payload STAGED to Mods/ + dist/. **This closes the M4 SCOPE — every item (items, world
items, shops, comms-radio, manual service) is built or proven-covered; the only M4 work left is the
batched in-game milestone smoke pass.** PUSHED `e7412c3..8c70a0b`.

**Prior (2026-07-20): M4.4 (Comms-radio actions for all players — rerail/delete/summon + fees)
BUILT + PUSHED** (`5f073f4`/`887cd03`/`f9981f7`, Cody's go). Cody picked "go for all three sub-slices"
after the recon flagged the size +
one-PC-testability split. Recon-first (decompiled `RerailController`/`CommsRadioCarDeleter`/
`CommsRadioCrewVehicle`/`CommsRadioController` → `research/comms-radio-recon.md`): all three fire a
public `Action<TrainCar>` success event and charge a DIRECT `Inventory.RemoveMoney` (not a register),
so **D14's WalletMirror reconcile was reverting the fee → the host acted FREE in a session** — the
load-bearing finding. What shipped (protocol **v7→v8**, tests 156→**161 ×3**, full sln **0 warnings**,
STAGED to Mods/ + dist/):
- **Sub-slice 1 — host fees:** `Shim/CommsRadioSync` + `CommsRadioHook` (Harmony prefix per mode's
  `OnUse`, acting only in the CONFIRM state — the ChainHook pattern). The prefix snapshots the game's
  computed price; the mode's success event fires it as a `FeeExternal` (target 0 = host scope) so it
  burns through the ledger once. Rerail/delete/summon now cost money.
- **Sub-slice 2 — delete → removal:** `TrainsetRegistry.TryDeleteCar` (last car → `TrainsetRemove`;
  else the survivors re-form a fresh set) + a world-source→server `CarDeleteNotice` (62). On the host
  `CarDeleted` event, LocoMP sends the notice (car id snapshotted before the destroy unbinds it) and
  the server removes the car everywhere — no more ghost replicas after a delete.
- **Sub-slice 3 — remote initiation:** on a joined client the confirm prefix SUPPRESSES the local
  mutation and sends `CommsActionRequest` (60); the server routes `CommsActionCommand` (61) to the
  car's sim owner (the M3.5c CoupleRequest pattern); the host executes the real rerail/delete and
  charges the INITIATOR via `FeeExternal`'s new **target peer** (43, D15's LicenseGrantExternal
  pattern). `WalletMirror` now also runs on joined clients (money display + correct comms-radio
  affordability; it does NOT report the client's own register buys). Bot `--rerail <plate>`/`--clear
  <plate>` drive the wire path headlessly (the client Harmony interception itself needs a real game
  client — friend-session). **Remote SUMMON banked** (spawning a new car at a remote location with
  livery/garage resolution is a later slice; host summon works + is charged).
- **NO new game ref** (all comms-radio types are in Assembly-CSharp). Prices reimplemented from
  observed behaviour (clean-room, our own code): rerail `RoundToInt(Clamp(500+dist·150,0,cap))`
  (HandCar/newbie free), delete `playerSpawned?0:DeleteCarMaxPrice`, summon garage `summonPrice`.
- **Banked**: remote summon; a real game client's affordability now checks the mirrored wallet, but
  the client-side interception is untested this session (bot drives the wire, not a radio); exact
  rerail placement uses a trimmed track search vs the game's full valid-point solve.

**Prior (2026-07-20): M4.3 (Shops) BUILT + PUSHED** (`8fd47af`/`8870cd3`/`d742960`, Cody's go). The
purchase TRANSACTION was already done + fuzzed in M4.1 (charge-then-mint: a
client's buy debits the CLIENT's wallet and mints the item into its possession). This slice is the
front-end + catalog that makes it usable in-game: a host-side `ShopCatalogBuilder` reads DV's live
`GlobalShopController.shopItemsData` into `ItemConfig.ShopPrices` (prefab→cents); the catalog rides
the join burst (new `ItemShopCatalog` msg 59, **protocol v6→v7**) so a client renders Buy buttons;
the panel gains a Shop section (Buy per item + "Drop here" on carried items); the bot gains `--buy
<prefab>` (buy once joined → mint in its own possession → hold → drop) — the one-PC win-condition
driver that proves wallet-scope isolation (bot's wallet debited, host's untouched) and then routes
the bought item through the M4.2 world-drop loop where the host materializes it. **NO new game ref**
(GlobalShopController is in Assembly-CSharp), NO Harmony (the shop read is a plain singleton walk).
Tests 155→**156 ×3** (+catalog delivered on join), full sln **0 warnings**, payload STAGED to Mods/ +
dist/. Awaiting Cody's go to push. In-game verification joins the batched **M4 milestone smoke pass**
(new cadence, D8/2026-07-20 — the shops Run-A checklist is banked below beside M4.2's). Deferred +
banked: live stock/restock replication (client buys are LocoMP mints, independent of the host shelf —
correct for the win condition, but the host's shelf count and the catalog can drift); routing a
client purchase through the host's real shelf; held-item avatar display (M4.2 Option 2, protocol v8).

**Prior (2026-07-20): M4.2 (Shim ItemSync — the world-item loop) BUILT + PUSHED.** Cody
picked Option 1 (world-dropped items only; held-item avatar display + shop materialization deferred
to their own bursts). `ItemSync` (host-native capture + client materialization, NO Harmony — every
seam is a public event), PresenceShim item-pose helpers, host `ItemConfig{AcceptExternalItems=true}`,
bot `--grab-items` driver. 155/155 ×3 (Core item loop already fuzzed in M4.1; the Shim is
game-facing = in-game verified, not headless), full sln **0 warnings**, payload STAGED to Mods/ +
dist/. **PUSHED (Cody's go).** **NEW TESTING CADENCE (Cody, 2026-07-20): in-game smoke runs are
BATCHED per MILESTONE, not per burst** — each slice builds + stages + pushes on merit; live
verification is deferred to ONE consolidated pass at the milestone boundary. So M4.2's Run-A
checklist joins the pending **M4 milestone smoke pass** (with M4.1's and whatever M4.3+ adds). This
is the **implementation** memory (burst cadence, D8).
The **planning corpus** lives one level up at `../` (00–09, INDEX, research/) — strategic, kept private.
Cold-starting? Read `../CLAUDE.md` (hard rules) → this file → the current milestone in `../07-ROADMAP.md`.

## Where things stand

- **M4.5 — Manual service: recon = VERIFY-NOT-BUILD; defensive RefillAll/RepairAll guard BUILT
  2026-07-20 (uncommitted). 161/161 ×3, full sln 0 warnings, STAGED to Mods/ + dist/.** The last M4
  scope item (07 §M4). Recon (`research/manual-service-recon.md`) proved the servicing economy is
  already correct:
  - **Fee already mirrored (NO fix needed).** The metered manual-service loop commits through
    `CashRegisterWithModules.Buy()`, which WalletMirror patches (D14, WalletMirror.cs:48–49) → the
    finalized refuel/repair fires `FeeExternal` (msg 43). Money mechanics detail: the register is a
    till (player deposits physical cash via `Wallet.TrySpend → Inventory.RemoveMoney`), and
    `WalletMirror.Reconcile` guards on `AnyRegisterHoldsCash()` so it won't revert while cash is in the
    till. No race, no free service. The pit-stop chain: hose flow only accumulates
    `LocoResourceModule.Data.unitsToBuy` (no money) → buy button → `Buy()` → `CashRegisterBase.Buy`
    consumes deposited cash → `GetBoughtResource()` realizes the price + fires `OnResourceBought`.
  - **License already mirrored.** `GeneralLicenseType.ManualService` rides D14 `LicenseSync` both ways.
  - **State sync OUT of scope (deferred).** Servicing mutates `SimulatedCarPitStopParameters` (the
    loco-sim layer) — LocoMP doesn't replicate loco sim yet (remote cars are kinematic ghosts), so
    synced fuel/damage has nowhere to land. Public seams (`OnResourceBought`,
    `SimulatedCarPitStopParameters.ParametersUpdated`) banked for the loco-sim milestone (post-M4).
  - **The guard (Cody's pick over verify-only).** `PitStopStation.RefillAll()`/`RepairAll()` apply a
    full free service bypassing the buy button (`UpdateCarPitStopParameter` directly). Recon found
    ZERO callers in any B99.7 assembly (the `Dev_LocoRefill*`/`Dev_LocoRepair*` console commands hit a
    different path — `ResourceContainerController`/`DamageController`), so this is defensive
    future-proofing, not a live leak. `ManualServiceHook` (Harmony prefix on both, host-only, armed
    only in-session) hands the station to `ManualServiceSync`, which sums the equivalent cost from the
    bay's own modules (`BuyMaxLimit` × `Data.pricePerUnit` over `ResourceTypes.Consumable`/`Damageable`)
    and burns it as a self-scope `FeeExternal` — never suppressing the service. Best-effort: a module
    with unknown/zero price is skipped + logged. NO Core/protocol/game-ref change.
  - **This closes the M4 SCOPE.** Every M4 item is built or proven-covered; only the batched in-game M4
    milestone smoke pass remains (manual-service checks added below beside the other M4 slices').
- **M4.4 — Comms-radio actions (rerail/delete/summon + fees, for all players): BUILT + PUSHED
  2026-07-20 (Cody's go). 161/161 ×3, full sln 0 warnings, STAGED to Mods/ + dist/.** All three sub-slices Cody
  asked for. See the header above for the full breakdown. Net: comms-radio actions cost money through
  the wallet (were free), deletes clear replicas everywhere (were ghosts), and joined players can
  rerail/delete host cars (fee to the initiator). Protocol v8. Recon at
  `research/comms-radio-recon.md`. In-game verification joins the batched M4 smoke pass (checklists
  below). Remote summon banked; client-side radio interception is friend-session (the bot drives the
  wire path solo).
- **M4.3 — Shops: BUILT + PUSHED 2026-07-20 (Cody's go). 156/156 ×3, full sln 0 warnings, STAGED to
  Mods/ + dist/.** The "a client buys a lantern" half of the M4 exit (07 §M4). The purchase transaction is
  M4.1's (charge-then-mint, already fuzzed) — this is its catalog + front-end + one-PC driver:
  - **Core (protocol v6→v7):** `MessageType.ItemShopCatalog` (59) — the shop catalog (prefab→cents)
    ships in the join burst before the item burst, mirroring how `CareerState` carries the license
    price catalog. `ServerItems.OnPlayerAdmitted` sends it from `ItemConfig.ShopPrices`;
    `ClientItems.ShopCatalog` mirrors it (cleared on Reset). No change to the purchase path itself.
    Test +1 (catalog delivered to every client on join): 155→156.
  - **Shim `ShopCatalogBuilder`** (host-only, read-only, NO Harmony): walks
    `GlobalShopController.Instance.shopItemsData` → `itemPrefabName`→`basePrice*100` cents, skipping
    `unavailableDueToGameMode` (career-only items outside a Career session) + malformed rows; logs
    `shop catalog: N item(s) for sale from M shop(s)`. Wired into the host's `ItemConfig` beside
    `AcceptExternalItems`. **Defining assembly = Assembly-CSharp (already referenced) → NO new game
    ref, NO CI heredoc change** (same clean bill as M4.2).
  - **Panel `DrawShop()`:** a Shop section (collapsible, Buy per catalog item at its price) + a
    "Drop here" button on each item I'm carrying (drops at my pose over the wire). Renders uniformly
    on host and client (both read `_client.Items.ShopCatalog` — the host joins its own hub). Item
    proposal refusals now feed the panel toast too.
  - **Bot `--buy <prefab>`** (+ reuses `--drop-after`/`--at`): buys once joined → the mint lands in
    its OWN possession (charged to its OWN wallet — the win condition proof) → holds `--drop-after`
    s → drops at `--at`, where M4.2's host ItemSync materializes it. `RemoteActor` catches the mint
    via `ItemAdded` (possessed + owned by me), logs the debited balance.
  - **Design call (host-native posture):** a client's purchase is a pure LocoMP mint, independent of
    the host's real shop shelf — correct for the win condition (the money + item move together,
    server-authoritative) and keeps the builder read-only. The host buying NATIVELY is already
    covered end-to-end (D14 money + M4.2 world capture), so it needs no shop code. Consequence
    (banked): the host's shelf stock and the LocoMP catalog can drift — live stock replication is a
    later slice, not exit-critical.
- **M4.2 — Shim ItemSync (world-item loop): BUILT 2026-07-20, PUSHED. 155/155 ×3, full sln 0
  warnings, STAGED to Mods/ + dist/.** Cody chose Option 1 of the M4.2 cut (world-dropped items
  only). This is the game-facing half of M4.1's Items core — the "drop a lantern, another player
  picks it up" win condition, made real in-game. What shipped:
  - **`Shim/ItemSync.cs`** (the deliverable) — parallels JobCapture (D13 host-native capture) +
    RealCarSync (spawn/despawn) but SIMPLER, because the recon's key finding held: **items are
    `SetActive(false)`'d, never destroyed like cars** (no ECS storms, netId maps survive streaming),
    so the M3.5b 250/330 m proximity-materialization band collapses to a plain keep-alive.
    **NO Harmony patches** — every seam is a public event (Main.cs unchanged).
    - **Host capture (world source, D13 posture):** subscribes `StorageController.Instance.
      StorageWorld.ItemAdded/ItemRemoved`; a player-owned item entering world storage (a native
      drop) → `ClientItems.RegisterWorldItem(prefab, absPose, "", token)`; leaving it (native grab/
      dumpster) → `DespawnItem`. A join-time sweep of `GetStorageItemList()` offers items already
      lying in the world (JobCapture's pre-session-jobs pattern). `ItemBase.AboutToBeDestroyed`
      (the item analog of OnCarAboutToBeDestroyed) is the despawn signal.
    - **Materialization (both roles):** a **Reconcile** pass (0.5 s + dirty-flag on any server item
      event) brings the local world in line with the server board — every WORLD item gets a real
      GameObject via the game's own two-liner (`Resources.Load(prefabName)` + Instantiate +
      `AddItemToWorldStorage`, `ItemBase.Awake` self-assembles the rest); a possessed/removed one is
      despawned. Host natives are mapped at `RegisterAccepted` (skip-respawn); only genuinely-remote
      world items get a fresh replica. `_spawnedIds` tracks OUR replicas so **Dispose deletes only
      those, never the host's real native items** (their SP save owns them).
    - **Keep-alive = FREE (recon-confirmed):** `ItemDisabler.OnItemDisablePositionUpdated` exempts
      any item where `!isOnDisablingStaticParent && item.BelongsToPlayer()`, and
      `isOnDisablingStaticParent` is true ONLY on a paint-station parent. Replicas spawn
      `InventoryItemSpec.BelongsToPlayer = true` (also required for world-storage acceptance) on the
      normal world parent → automatically exempt from distance streaming-off. No patch, no per-frame
      SetActive fight.
    - **`_applying` reentrancy guard** (the M2 idiom) around every native change WE initiate, so our
      own spawn/despawn's StorageBase + AboutToBeDestroyed events never echo back as captures.
  - **`ItemConfig{AcceptExternalItems=true}`** passed to the host's ServerConfig — the default gates
    external items OFF (dedicated-server posture), which would have REJECTED the host's own
    registrations. Same fix D13 made with `AcceptExternalJobs`. PickupRadiusM=0 (no gate yet, so the
    headless bot can pick up from anywhere), no shop catalog (shops are a later slice).
  - **Host restore keeps starting the item store EMPTY** (`ServerSaveData(career, trains)`, items=null
    default) — the CORRECT host-native posture (like trainsets): the host's real items persist via
    DV's own save + re-sweep each session. The LocoMP item save becomes the source only on the
    dedicated server (M6).
  - **PresenceShim** gained `ToAbsolutePose(Transform)` + `ToRotation(Pose)` (item capture/spawn
    coord conversion, same OriginShift math as presence/cars).
  - **Bot `--grab-items`** (+ `--drop-after <s>`, default 20): picks up world items as they appear
    (proposing a pickup the server validates), holds each a beat, drops it at the `--at` anchor — the
    one-PC driver for the loop (folded into RemoteActor beside claim/drive). Headless smoke path:
    `client.Items.RequestPickup/RequestDrop`, the same calls M4.1's ItemSessionTests already fuzz.
  - **Panel:** a `Items — N in the world, M carried` line while in a session.
  - **Deliberately deferred (banked):** held-item avatar display (Option 2 — needs a small protocol
    v7 `ItemHeld` message + avatar render); shop materialization (Option 3 — client Purchase already
    mints in Core, needs Resources.Load-into-inventory + RespawnOnDrop/stock handling); joined-GAME-
    client native item capture (only the HOST captures natively — a real second player grabbing a
    replica is a friend-session concern, like several M3.5x items); item STATE blob (fuel/label —
    registered as "" for now; most items have none); container contents (recon §7).
- **M4.1 — game-free Items core: BUILT + PUSHED 2026-07-19 (late)** (`6064759` feat/`31d1f5c` docs).
  **155/155 ×3, full sln 0 warnings, payload staged to the game's Mods/ + dist/.** The M4 spine's
  authority layer, headless and fuzzed — no Shim yet (that's M4.2). What exists:
  - **`Items/` domain** (game-free, mirrors `Trains/` + `Career/`): `ItemDef` (id + prefabName +
    opaque state; the recon confirmed DV has NO per-instance id, so LocoMP mints `ItemNetId`s — the
    car-id pattern) + `ItemRecord` (def + location) + **`ItemRegistry`** — the authority enforcing
    the **single-location invariant** (an item is World-at-a-pose XOR one scope's possession, never
    both/neither) with an **item-conservation oracle** (`ItemConservationHolds`: live == spawned −
    despawned, the ledger's money-conservation analog). Possession routes through the **policy layer**
    (`ProgressionPolicy.InventoryScopeFor` — new; per-player keeps inventory private, shared-career
    pools it "freely shared" per 02 §6, the same one-switch as the wallet). `ItemConfig` = shop
    catalog (prefab→price) + pickup radius + host-capture gate.
  - **Protocol v6** (MessageType 50–58: ItemRegister/Pickup/Drop/Purchase/Despawn requests +
    ItemSpawned/Moved/Despawned/Rejected). A possession's SCOPE KEY never hits the wire — the holder
    rides as (peer id, name) like a job's claimant; the SAVE keeps the key (it re-binds inventory
    across a restart). `ItemCodec` shares the def between wire + save (can't drift), each side writes
    its own location. `EconomyEventKind.ShopPurchase` added.
  - **`ServerItems`/`ClientItems`** session modules beside the trains/career pair, wired into
    NetServer/NetClient (dispatch tries trains → career → items; admit AFTER career maps peer↔key,
    remove BEFORE it drops the map). **Purchase = charge THEN mint**: `ServerCareer.TryChargeShop
    Purchase` burns from the policy wallet (overdraft-refused) and only then does the item mint, so
    money + item move together — THE win condition: a *client's* purchase debits the *client's*
    wallet, not the host's. Pickup is proximity-gated (claimant-pose vs item pose, the career task
    gate's posture) and exclusive. World-item register/despawn are world-source-gated (D13 posture,
    for M4.2 host capture; exercised now). Join burst delivers every item; leaver's held items are
    retained + marked offline (rejoin rebinds), like a career claim under grace.
  - **Persistence: LMPS schema v3 → v4** — the items half (world items + per-player inventory + id
    counter) appended; a v3 file is refused cleanly (no migration, backups keep the bytes). Cold
    restart resumes world items at their poses AND each player's inventory keyed to their scope.
  - **Tests 134 → 155**: `ItemRegistryTests` (mint/pickup/drop/despawn, exclusivity, both presets'
    scope routing, capture/restore, + a **2,000-op fuzz** asserting single-location + item
    conservation after every op with a save/restore round-trip every 250) and `ItemSessionTests`
    (the client-purchase win condition w/ money conservation, buy→drop→pickup lifecycle mirrored,
    host-capture register + non-source refusal, despawn, proximity refusal, join burst, **cold
    restart resumes world items + inventory + wallet**, shared-career pooled inventory).
  - **O12 accepted earlier this session** already ratified LMPS as the save format, so schema v4 is
    a clean extension of a settled format, not a new deviation.
  - **Next — M4.2: Shim ItemSync** on the recon's event seams (`StorageBase.ItemAdded/ItemRemoved`,
    `Inventory.InventoryStatusChanged`, `Grabber.GrabStarted/Stopped`; spawn via
    `Resources.Load(prefabName)`; the `ItemDisablerGrid` only deactivates far items — a keep-alive/
    exemption, NOT the M3.5b materialization machinery). Then comms-radio actions for all players
    (summon/rerail/delete + fees) round out M4.
- **D15 BURST 2026-07-19 (late): O11 + O12 RESOLVED (Cody) → D15 built + M4 opened. PUSHED
  `7b93bc8` (feat) + `0c1bfe9` (docs), `2505e29..0c1bfe9`.**
  - **O12 accepted**: LMPS is the ratified career-save format (03 §7 amended; MessagePack stays
    reserved for bulk join snapshots). Standing flag closed, no code change.
  - **O11 → D15** (Cody's own design: "keep host grants, gate to what the host holds, plus an
    auto-grant checkbox"). Core: `HandleLicenseGrantExternal` now refuses a grant to ANOTHER
    player of a license the world source's own scope lacks ("grants share progression, they
    never mint it") — self-mirror grants stay scope-agnostic (the game is the authority, D14).
    `ServerCareer.AutoGrantHostLicenses` (host UI sets in-proc): join half copies the world
    source's scope onto the newcomer BEFORE the career burst is built (licenses ride the burst);
    live half propagates a license newly entering the world-source scope to every connected
    player — triggered from BOTH the native-mirror self-grant and the panel-shop purchase
    (the purchase path is separate because its native echo comes back idempotent); flipping the
    toggle ON mid-session sweeps immediately. Panel: "Auto-grant my licenses to joining players"
    checkbox (idle + live while hosting), and the host grant list now offers the host's HELD
    licenses (`career.Licenses`) instead of the price catalog. One M3.5c test updated — the host
    now self-grants before handing out, which IS the D15 behavior change. Tests 129 → **134**
    (targeted-grant delivery, unheld-grant refusal, join-burst copy, live propagation ×2 paths,
    toggle-on sweep).
  - **M4 recon: 02 verification item 5 ANSWERED** (`../research/item-system-recon.md`; ~20 type
    dumps in `../scratch/decomp/`). Headlines: `itemPrefabName` = the canonical TYPE id;
    **no per-instance id exists** → LocoMP mints its own ItemNetId (the M3.5b car-id pattern);
    capture hooks are ALL public events (`StorageBase.ItemAdded/ItemRemoved` on the five buckets,
    `Inventory.InventoryStatusChanged`, `ItemBase.ItemInventoryStateChanged`, `Grabber.
    GrabStarted/GrabStopped` for held-item display) — no Harmony needed; spawn = `Resources.Load
    (prefabName)` + instantiate (both native spawners do exactly this); `ItemDisablerGrid` only
    `SetActive(false)`s far items (NO car-style ECS destruction — remote item replication needs
    an exemption/keep-alive, not the M3.5b materialization machinery); shop money legs already
    ride D14's wallet mirror (`Wallet.TrySpend → Inventory.RemoveMoney` at deposit time); stock
    is runtime-reconstructed (counting live `ShopRestocker` items), not saved.
  - **Smoke additions (ride the next game session, with the debt-pass checklist)**: auto-grant
    checkbox visible idle + hosting; host grant list shows only held licenses; with auto-grant ON
    a joining client/bot receives the host's licenses in the join burst (client career log shows
    the license set) and a native license buy mid-session propagates live.
  - **Next: M4.1 — game-free Items core** (ItemRegistry + protocol v6 item messages + per-player
    inventory through the policy layer, LMPS-persisted), then Shim ItemSync over the recon's
    event seams. Commit+push of this burst awaits Cody's go, as usual.
- **DEBT/POLISH PASS 2026-07-19 (Cody's pick over M4/M3.2 for this burst): BUILT, uncommitted.**
  129/129 ×3, full sln 0 warnings. What closed:
  - **Session-lost UX (the M3.5b known debt)**: `NetClient.Disconnected` (Core — fires only after
    admission; failed joins stay Rejected-territory) → SessionController shows "SESSION LOST —
    Leave to restore your world, then reload your save" after a 3 s recovery window (a link that
    re-handshakes, e.g. the save-load-freeze eviction, continues silently with a log line).
    Deliberately NO auto-Leave: Leave() re-enables native saving, and doing that unattended in a
    session-mangled world is the exact leak SaveSuppressor blocks. Loopback server-endpoint
    Dispose now drops all clients (matches LiteNetLib's disconnect-on-Stop) so the seam is
    deterministically tested.
  - **Disconnect timeout 5 s → 15 s** (LiteNetLibTransport, both roles): the mid-load eviction's
    root cause was LiteNetLib's 5 s default vs DV's load freezes. A genuinely dead peer lingers
    10 s longer — park-on-disconnect + career grace absorb that by design.
  - **AbandonJob debt audit CLOSED — documentation only, no code change needed.** Decompiled
    B99.7 (`scratch/decomp/`, ilspycmd): **DV has NO flat abandonment fine.**
    `JobDebtController.OnJobCompletedAbandonedExpired` stages only ACCRUED car service costs
    (damage/fuel deltas, and only if > 0) — identically for complete/abandon/expire. So the
    optimistic rollback (abandon seconds after a take) stages nothing, and an external release
    stages the haul's real wear onto the host's career manager, whose payment already rides the
    WalletMirror `Buy` hook as `FeeExternal` (D14) — conservation holds, semantics match SP.
    Bonus: `JobsManager.AbandonJob` THROWS on a job not in `currentJobs`; all three call sites
    are state-guarded and RunNative-caught.
  - **Bot honors remote couple/uncouple requests** (the "owner-side execution not one-PC-testable"
    debt): the listen-mode bot now EXECUTES chain acts on its consists — request → ProposeUncouple/
    ProposeCouple → server transaction → everyone converges — and **adopts split/merge products by
    lead car id** instead of re-registering (which would have duplicated the train). The friend
    session demotes from "only way to fire this" to "confirmation".
  - **`--derail-car <n>`**: streams consist car n as OffRail at the `--at` anchor — the
    derailed-at-registration (null-track SpawnLoadedCar) path is now firable solo. RealCarSync
    logs loudly when that leg runs; the existing catch → ghost fallback is its safety net.
  - **CabControlSync "controls exist" debt RE-AUDITED as already fixed**: FindControl was
    null-safe and both apply paths caught; the real gap was a holder's input dropping SILENTLY —
    now logged once per (car, control). VR verification itself stays R2.
  - **Docs**: 08-RISKS — R8/R10 refreshed, **R16 added** (host-presence scoping in host-native
    mode, from run-A finding №6); 00 — **O11** (guest progression on mature hosts) + **O12**
    (ratify the LMPS deviation) recorded — both RESOLVED later the same day (the D15 burst above).
  - Tests 125 → **129** (session-loss ×2; chain-request round trip incl. product adoption +
    re-merge with 0 stale discards; derail-car stream).
  - **Smoke run (next game session, listen rig)**: (1) join the bot host, Ctrl+C the bot →
    within ~15–20 s the panel flips to SESSION LOST, saving still blocked, Leave → reload →
    SP save intact; (2) unhook a chain between two bot cars → the split now actually HAPPENS
    (host-side log `remote uncouple request honored`), re-hook → merge; (3) re-run with
    `--derail-car 2` → `spawning with DERAILED car(s)` log, car off-rail near you (ghost-box
    fallback with a log line is also a pass — the path is guarded either way); (4) normal host
    rig regression rides along.
- **Milestone: M3 — Career: IN PROGRESS. M3.1 (game-free career core) BUILT + COMMITTED 2026-07-18,
  100/100 tests, full sln 0 warnings.** What exists now, all game-free (03 §11 posture — the whole
  economy fuzzes headless):
  - **Protocol v3**: `JoinRequest` gains a stable **player key** (client → server ONLY — it doubles
    as the reconnect credential, so it is never broadcast; other players are identified by session
    peer id + display name on the wire). Career messages 29–39 appended. v2 clients get the proper
    "protocol mismatch" reject (defensive tail read in HandleJoin).
  - **`Career/` domain**: `CareerRegistry` (ALL career rules in one place, mirroring
    TrainsetRegistry's role) + `EconomyLedger` (integer cents; money ONLY minted [payouts, starting
    grants] or burned [fees]; `ConservationHolds` = the oracle: sum(balances) == minted − burned,
    exact) + `ProgressionPolicy` (D3/O2: PerPlayer default / SharedCareer preset — every wallet and
    license touch routes through it; shared account is `@shared`, and keys starting with `@` are
    rejected at the door) + deterministic job generation (**hand-rolled xorshift32, NOT
    System.Random — its algorithm differs between net48/Mono and net8**, which would silently break
    same-seed determinism between host-embedded and dedicated) driven by host-supplied
    `CareerConfig` data (stations + `JobTypeSpec`s + license prices; empty config = empty board,
    M3.3 feeds the real map). Claims: exclusive, TTL'd, license-gated at claim time, per-player
    limit; task steps strictly sequential; final step mints the payout and the job leaves the board.
  - **Session wiring**: `ServerCareer`/`ClientCareer` beside the trains modules; career burst after
    the trains burst; `NetServer.Poll` now also `Career.Tick()`s (TTL/grace expiry + deterministic
    board refill). Rejections go back to the requester as `CareerRejected` ("missing license: X" is
    UX, not just a host log). **Reconnect grace (10 min default)**: claims are held on disconnect
    and rebind to the new peer id on rejoin (JobState re-broadcast on BOTH leave and rejoin — a
    bug the session tests caught: others' boards showed the dead peer id).
  - **Persistence v1**: `SaveCodec` ("LMPS" magic + schema version, hand-rolled over
    PacketWriter/Reader like LMPW, reusing the wire codecs so store and wire can't drift).
    **Deliberate deviation from 03 §7's "MessagePack" sketch — flag for Cody**: zero new deps in
    the payload, proven infra; MessagePack stays reserved for bulk-channel join snapshots if sizes
    demand it later. `FileSaveStorage` (atomic temp+rename, rotating .1..N backups), `Autosaver`
    (clock-driven, both frontends), `NetServer.CaptureSave()` / ctor-restore. **Deadlines persist
    as REMAINING ms** (monotonic clock restarts with the process); players online at save time get
    a fresh grace hold on restore (a restart IS their disconnect). Restore refuses a preset
    mismatch loudly (wallet migration between presets is undefined). Trains half: defs (restored
    PARKED, ids/epochs preserved), **last admitted snapshot per set** (also now sent reliable in
    the join burst as a position baseline — restored/parked consists were previously nowhere until
    the owner streamed), junctions, turntables, id counters.
  - **Tests 70 → 100**: registry rules (grants/determinism/gates/order/TTL/grace, both presets),
    session e2e (burst, claim mirror w/ peer identity, full loop pays only the claimant,
    shared-wallet broadcast, rejection UX, duplicate/invalid key rejects, **rejoin-mid-job restores
    exactly and continues**, grace expiry releases for everyone, hard-disconnect grace), persistence
    (codec round-trip, foreign-bytes/future-schema rejects, rotation, autosaver, **cold restart
    resumes world + a rejoin finishes a job across it**, preset-mismatch throw), and a **2,000-op
    career fuzz × both presets with the conservation oracle asserted after every op and a
    save/restore round-trip every 250 ops** (persistence proven under arbitrary mid-flight state).
- **M3.3 — Shim career integration: CODE-COMPLETE + BUILT 2026-07-18 (uncommitted; payload staging
  pending — game was running and held the DLL lock; a background waiter stages on exit). M3.1
  pushed same day (`53d642b`, Cody's go).** What was built:
  - **02 verification item 4 ANSWERED: interception is CLEAN.** Every DV generation path (zone
    entry, RegenerateJobs, expiry attempts) funnels through
    `StationProceduralJobsController.TryToGenerateJobs` — one Harmony false-prefix
    (`JobGenSuppressor`, Active only in sessions, both host AND clients) plus `StopAll()` for
    coroutines already mid-flight at session start. Pre-session jobs are left as props.
  - **`CareerConfigBuilder` (Shim)**: real `CareerConfig` from the live world — station YardIDs +
    ABSOLUTE positions (− OriginShift.currentMove, presence-pose space), route distances via the
    game's own `JobPaymentCalculator.GetDistanceBetweenStations`, one `JobTypeSpec` per (station ×
    output CargoGroup) with licenses from `LicenseManager.GetRequiredLicensesForJobType/CargoTypes`
    (v2 `.id` strings), license catalog + prices from `Globals.G.Types.jobLicenses`. Payout feel
    constants $100/car + $10/car/km (NOT the game's exact formula — that needs car/cargo value data
    = M4); car counts capped at 8 (no train-length licensing yet). All typed access compiled first
    try against B99.7 — the reflection recon signatures were exact.
  - **Core additions**: `JobTypeSpec.Origins/Destinations` + `PayoutPerCarKmCents` +
    `CareerConfig.StationDistancesKm/StationLocations/TaskProximityRadiusM` (generator now follows
    real cargo routes with distance-scaled payouts) · **server-side task proximity validation** —
    the claimant's own presence pose (server-known) must be within the radius of the task's station
    (horizontal; missing data passes through so the check only ever ADDS a refusal). CareerState
    now also carries the license-price catalog (clients can't render a shop otherwise).
  - **Shim UX**: UMM panel career section — wallet/licenses/preset line, MY JOB rows with `Report
    <next step>` + Abandon, scrollable board with Claim buttons + others' claims (name + offline
    marker), collapsible license shop, toast line (rejections + economy events). Idle panel gains
    "Shared career (classic co-op)" + "Fresh career" toggles.
  - **`PlayerKeyStore`**: identity GUID at `%USERPROFILE%\AppData\LocalLow\AltFuture\Derail
    Valley\locomp-player-key.txt` (persistentDataPath — survives restages + game updates; deleting
    it = fresh career). Career saves: `locomp-career-<preset>.lmps` beside it, per-preset filenames
    so a preset switch can't hit the mismatch throw. Host auto-resumes unless "Fresh career";
    2-min autosave + SaveNow on Leave/world-unload. **Host-mode resume restores the CAREER half
    only** — the host's live world is the physical truth and re-registers consists fresh;
    restoring saved trainsets would duplicate them as ghosts (full-world restore = dedicated
    server, M6).
  - Tests 100 → **102** (route-constrained generation w/ distance payout; proximity gate over the
    session stack). Suite ×3 green (one localhost-UDP flake retried clean ×3), full sln 0 warnings.
- **D13 RECORDED (Cody, 2026-07-18) + M3.5a BUILT same evening (uncommitted, STAGED 22:36):
  host-native job capture.** Cody's UI/X 180 → "full native, pull M4 forward" → Route A approved:
  DV's generator keeps running ON THE HOST (real cars/booklets/yard logic); LocoMP mirrors every
  job onto the server board and routes claims/completions/payouts through the policy layer. The
  M3.1 core generator is RESERVED for the dedicated server (both feed the same board machinery).
  What shipped:
  - **Blocker fix (live-found)**: DV grants Freight Haul at career start; our career didn't →
    board fully license-locked with an unaffordable buy-out. `CareerConfigBuilder` now sets
    `StartingLicenses` from `GetRequiredLicensesForJobType(Transport)`, and starting licenses are
    a **floor applied on every connect** (idempotent) — existing saves heal on next join.
  - **Core**: MessageType 40/41 (JobRegister/JobRetract, world-source-gated = first admitted
    peer), `JobDef.GameId`, `CareerConfig.AcceptExternalJobs`, `TryRegisterExternal` (server
    assigns id; GameId deduped) + `TryRetract` (never retracts a claimed job), CareerRejected now
    carries the jobId (native rollback needs to know WHICH claim lost), save schema → v2 (v1
    files refused cleanly; backups keep the bytes). Proximity gate EXEMPTS captured jobs — the
    game's own task tree is their validator.
  - **Shim `JobCapture`**: `JobsManager.RegisterGeneratedJob` postfix (THE single point every
    real job passes) + join-time sweep of pre-existing available jobs; native take (booklet →
    validator `ProcessJobOverview` → `TakeJob`) = OPTIMISTIC server claim, rolled back via native
    `AbandonJob` on refusal (reentry-guarded); native completion (turn-in `ValidateJob` →
    `Job.JobCompleted`) reports the single Haul step → server pays the claimant;
    `JobExpired` → retract; **`MoneyPrinterJobValidator.PrintPayment` false-prefixed in-session**
    (the wage rides the ledger, never the game's cash — SP economy stays a bystander).
    Host runs with `JobGenSuppressor.Active = false`; only joining clients suppress.
    CareerConfigBuilder no longer emits synthetic job shapes (kept: stations/locations/distances,
    starting licenses, license catalog).
  - Panel: captured jobs show "[claim at the <yard> validator]" instead of a Claim button (native
    claiming is host-only until real-car replication; remotes read-only). 104/104 ×3, 0 warnings.
  - **Known M3.5b list**: native AbandonJob rollback may touch the debt system (fees) — audit;
    remote claim/abandon semantics + chain lifetime once real cars exist; wage = base payment
    only (bonus needs server-side time model); booklet materialization for remote players;
    **a native mid-session game save may persist the MIRRORED balance into the SP save** (D14
    accepted cost — the in-memory restore on Leave can't rewrite a save already written);
    remote players' local career managers are untouched (their native world is their own SP save).
- **D14 RECORDED (Cody, 2026-07-19) + BUILT same day: full native wallet unification.** M3.5a run
  №1 (Phase 1) found the seam D13 left open: the career manager is a second license-granting and
  money-spending path invisible to LocoMP — a native license purchase let the native validator
  approve a take the server then refused, and the AbandonJob rollback DESTROYED the physical
  leaflet (abandoned ≠ available in DV; the old "returned to the world" log line was wrong).
  Chosen over grant-sync-only and suppress-native-buying. What shipped (109/109 ×3, 0 warnings):
  - **Core**: MessageType **42 `LicenseGrantExternal` / 43 `FeeExternal`** (both
    world-source-gated like JobRegister), `EconomyEventKind.ExternalFee`,
    `CareerRegistry.TryGrantExternal` (idempotent, CHARGE-FREE — the register fee arrives
    separately, charging twice was the trap) + `TryChargeExternalFee` (policy-routed burn,
    overdraft-refused), ClientCareer senders. Grants accept ids the price catalog doesn't know —
    the game is the authority on what exists.
  - **Shim `LicenseSync`** (host-only, Harmony-free — LicenseManager's `JobLicenseAcquired`/
    `LicenseAcquired` EVENTS): native grants → server (reentry-guarded); server grants
    (panel shop, starting floor, resumed saves) → applied natively via
    `Globals.G.Types.TryGetJobLicense/TryGetGeneralLicense` + Acquire; join-time sweep mirrors a
    mature save's whole license set (the host's progression IS the world, like its jobs/consists).
  - **Shim `WalletMirror`** (host-only): saves native money on session start, restores on
    Leave/dispose; `Inventory.PlayerMoney` reconciled to the ledger every 0.75 s BUT ONLY while
    no register holds deposited cash (deposit → Buy → leftover-return ordering races otherwise);
    Harmony prefix+postfix on **both** `CashRegisterCareerManager.Buy` and
    `CashRegisterWithModules.Buy` (Buy is virtual — patching the base body catches nothing; cost
    read in the PREFIX because a committed transaction clears it) → finalized purchases become
    `FeeExternal`. Career-manager DEBT payments ride the same hook — part of the M3.5b debt audit
    landed free. Unmirrored native income is deliberately reverted by the reconcile (ledger =
    truth, 03 §9).
  - **Validator pre-gate** (in JobCapture): prefix on `JobValidator.ProcessJobOverview` refuses a
    doomed take BEFORE the game consumes the overview (leaflet kept, error sound, panel toast via
    new `TakeRefused` event) using the client mirror (not-on-board-yet / claimed-by-other).
    License refusals can't happen anymore (the native check IS the synced check). The optimistic
    rollback stays as a true-race backstop but now also RETRACTS the board entry (no ghost jobs)
    and logs honestly that the leaflet is lost.
  - **CareerConfigBuilder**: general licenses join the price catalog (career manager sells them;
    remotes need prices); `MaxConcurrentClaims = 99` in host-native mode — DV's own
    concurrent-jobs licensing governs, a stricter core limit would refuse takes the native
    validator already allowed (dedicated server keeps the default 3).
  - **New game ref: DV.Inventory** (targets ×2 + build.yml + release.yml heredocs).
- **M3.5a CLOSED 2026-07-19: run №2 PASSED — zero bugs, zero regressions** (whole D14 surface +
  all run-№1 regressions: mirror boot, native license buy → licensed claim with leaflet safe,
  panel-shop reverse sync, haul/turn-in, deposit/cancel settle, persistence + grace, bot).
- **M3.5b BUILT 2026-07-19 (uncommitted, STAGED 14:31): REAL-CAR REPLICATION — the pulled-forward
  M4 spine's first half.** Remote consists spawn as real TrainCars instead of ghost boxes:
  - **Core protocol v3→4 + save schema v2→3**: `CarDef` gains `GameId` (TrainCar.ID), `GameGuid`
    (CarGUID), `CargoId` (v2 string id) + `CargoAmount` — the identity job paperwork names cars by
    rides the wire from registration. `WithDerailed` preserves them (derail commits copy defs).
    110/110 ×3, 0 warnings.
  - **Shim `RealCarSync`** (replaces GhostConsists in TrainSync; the ghost class remains as the
    per-SET fallback when any livery can't resolve — the bot's synthetic `ghost-loco` kinds keep
    the old rig working unchanged). Spawn = the game's own savegame-restore primitive
    **`CarSpawner.SpawnLoadedCar`** (exact per-bogie track+span placement + carId/carGuid), deferred
    to the first admitted snapshot (the join burst carries a baseline). Post-spawn hardening:
    `preventDelete`, `preventAutoCouple` on every coupler, ALL rigidbodies kinematic (re-applied
    for 3 s while components initialize) — local physics never fights the remote authority (the
    incumbent's snap-back class). Drive = the proven ghost lerp (12/s + 80 m snap) on the real car
    root; `Bogie.SetTrack` keeps track occupancy current on edge change; registration cargo is
    mirrored onto the logic car (`LoadCargo`, capacity when amount unknown). Adjacent cars are
    physically coupled (position-based `TryCouple`, audio off); membership transactions re-map
    cars BY SERVER CAR ID across merges/splits (no despawn/respawn) then repair physical couplings
    to match the defs. Lost-car self-heal: respawn from next snapshot ×3, then ghost fallback.
  - **Interaction guard**: a manual chain-couple between a LOCAL car and a REMOTE-driven car is
    reverted immediately with a log line — the couple-request path is M3.5c; a silent half-couple
    would anchor local physics to a teleporting kinematic body. Entering a remote cab requests a
    control grant (`TryAnyCarId` covers both maps); input routing over the grant is M3.5c.
  - **Joined-client world handover**: on join the client CLEARS its own native cars (the host's
    world IS the session world; warning logged if you're inside one) and **`SaveSuppressor`**
    (false-prefix on `SaveGameManager.SaveAllowed`, the one gate all save paths consult) blocks
    native saves until Leave — a session can never leak into the player's SP save; reload the
    save after leaving. **Host D14 debt CLOSED**: `WalletMirror` hooks `SaveGameManager.AboutToSave`
    and writes the real pre-session balance into any mid-session save (reconcile re-mirrors ≤1 s).
  - **Host enrichment**: RegisterWorld fills identity + cargo per car and logs a paste-me
    `bot livery hint: --livery a,b,c` line (real livery ids from the live world).
  - **Bot**: `--listen` = bot HOSTS (NetServer over Loopback hub + UDP composite — the same wiring
    as the in-game host; the game joins as the CLIENT on one PC), `--livery a,b,c` (first = car 1,
    rest cycle) + `--cargo id[:amt]` register the consist so it spawns real. Headless smoke run
    passed: listen + self-join + registration with liveries + streaming.
  - **Banked M3.5c debts**: control-grant input routing (remote can sit in the cab, not drive);
    client couple/uncouple REQUESTS (today: auto-reverted); live cargo load/unload sync;
    mid-session host-spawned cars (new job chains!) are never registered — dynamic registration;
    booklet materialization + remote claim flow (the M3.5c core); derailed-at-registration spawn
    passes null tracks to SpawnLoadedCar (untested path — watch run A); crew-vehicle/garage
    spawns on a joined client re-create native cars after the clear.
- **M3.5c BUILT 2026-07-19 (uncommitted, STAGED 16:51): REMOTE CLAIM PARITY + MULTI-CREW — the
  pulled-forward M4 spine's second half.** Protocol v4→v5, messages 44–49. What shipped:
  - **v4 wire bug FOUND + FIXED by code reading**: registration (`RegisterTrainset` →
    `HandleRegister` → `Registry.Register`, all untouched since M2.1) silently STRIPPED
    GameId/GameGuid/CargoId/CargoAmount — the v4 fields rode defs and saves but never survived
    the only entry point. Every M3.5b remote spawn actually used the `LMP-N` fallback identity
    and spawned cargo-empty (liveries worked — Kind flows — which is why the runs looked right).
    Registration now rides the full CarDef codec; new regression test pins it.
  - **Remote claims on captured jobs** (the M3.5c core): a joined player claims from the panel →
    the host's JobCapture sees the JobState broadcast and calls `JobsManager.TakeJob` natively on
    their behalf (public API; DV's "taken" is global world state — no booklet prints, reentry-
    guarded via the existing `_applying`/`RunNative`). Their "Report delivery" DEFERS server-side:
    `ServerCareer` stashes the report and sends `JobCompleteRequest` (46) to the world source →
    host runs `JobsManager.TryToCompleteAJob` (DV's own task tree is the validator) →
    `JobCompleteReply` (47) commits the stashed report and mints the payout to the CLAIMANT (by
    key, so a mid-reconnect claimant still gets paid); refusals carry the native verdict as the
    claimant's toast; 15 s timeout → "host did not confirm — try again". **Released external
    claims (abandon/TTL/grace) retire the job EVERYWHERE** (broadcast as Expired; host abandons
    natively) — DV cannot re-shelve a taken job, so the board never advertises one no world can
    deliver. World-source direct reports keep the M3.5a path (no self-queries). Booklet
    materialization for remotes = deliberate M4 deferral (a booklet is an ITEM + task-tree
    reconstruction; the panel is the remote claim UX until then).
  - **Multi-crew cab controls** (`CabControlSync`, new): one uniform surface —
    `BaseControlsOverrider.GetControl(ControlType)` / `OverridableBaseControl` {Value, Set,
    ControlUpdated}; the 42-value ControlType enum IS the wire byte. Holder side: in a remote cab
    with the grant, lever moves send the existing `ControlInput` (28). Owner side: every control
    on own bound cars is watched; committed values broadcast as `ControlState` (44) which the
    server stores per (car, control) and REPLAYS in the join burst — a newcomer's replica levers
    match reality. Input becomes state only through the owner (03 §3): the owner's apply is
    deliberately un-guarded so its ControlUpdated echoes the committed state back out; replica
    applies ARE guarded (+ skipped while we're the occupying grant holder — never fight the
    player's hand). Sends rate-limited 10/s per touched control with trailing flush.
  - **Couple/uncouple requests**: `ChainHook` (new Harmony pair on
    `ChainCouplerCouplerAdapter.TryCouple/TryUncouple` — the single funnel under the chain FSM)
    intercepts chain acts involving a remote-driven car BEFORE physics and sends
    `CoupleRequest`/`UncoupleRequest` (48/49, car-relative ends via `Coupler.isFrontCoupler`);
    the server routes to carA's sim owner; the owner performs the real `CoupleTo`/`Uncouple`
    (explicit partners, ≤12 m sanity) and its NATIVE event drives the normal proposal→transaction
    path — one authority chain, no second commit path. The M3.5b physical-couple revert stays as
    a backstop and now also routes the intent.
  - **Live cargo sync**: host POLLS logic-car cargo at 1 Hz (same no-delegate-guessing posture as
    derail polling) → `CargoState` (45) → server folds it into the stored CarDef via
    `Registry.TryUpdateCargo` (SAME epoch — cargo is not membership; late joiners + saves carry
    the live load with NO schema change) → replicas re-mirror onto their logic cars.
  - **Dynamic registration + streamed-back rebind** (host, 2 s scan, one-scan settle): live sets
    whose cars are ALL unknown either re-bind to their existing server def by car GameId multiset
    (closes the banked "respawn rebinding" debt — re-registering would duplicate the set after
    DV's distance streaming rebuilds it) or register fresh (job-chain spawns, summons). Joined
    clients CULL natively spawned cars post-clear (crew vehicle/garage) with a log line —
    guarded by `RealCarSync.SpawningRemote` so our own spawns pass.
  - **Bot**: `--claim-first` / `--report-interval` / `--abandon-after` (remote career loop
    headless) + `--drive`/`--drive-value`/`--drive-seconds` (grant + throttle a host loco — the
    host watches their own lever move); in `--listen` mode a granted player's throttle input now
    drives the bot consist's speed (Cody can sit in its cab and DRIVE it). Headless listen smoke
    passed on the v5 registration.
  - Tests 110 → **122** (`RemoteParityTests`: registration identity regression, control
    relay/join-burst replay/non-owner drop, grant→input→owner→state echo, cargo def-fold + late
    join, couple/uncouple request routing with commit, deferred completion ok/refusal/timeout,
    external abandon + grace death, world-source direct path). ×3 stable, full sln 0 warnings.
  - **Banked debts**: physical overview leaflet behavior after a programmatic TakeJob = UNKNOWN
    (watch in run; the pre-gate already refuses host takes on claimed jobs either way) ·
    owner-side couple-request execution is core-tested but not one-PC-testable in-game (the
    requester must be a joined GAME client; bot owners ignore requests — friend session verifies
    live) · derailed-at-registration spawn path still untested (carried) · replica control apply
    assumes controls exist once SimController does (VR interactions unverified).
- **M3.5c RUNS CLOSED 2026-07-19 (one-PC wording): A/B/C all passed.** Run A = THE full remote
  job loop live (`remote completion check → COMPLETE — claimant gets paid`, host wallet
  untouched); Run B = input routing (bot drove Cody's L-013 throttle + brake by wire, grant
  refusal while held, ControlState broadcast observed); Run C = client cab (Cody drove the bot's
  train from its cab — 13 controls live — chain couple/uncouple intercepted + routed, world
  handover + SP save intact, no spawn fights). Eight live findings fixed same-day (see session
  log): claim eligibility, host license grants (NEW feature, msg 42 target peer), ghost-job
  persistence, route visibility + car identity (booklet essence on the wire), far-station expiry
  killing claims (world-is-truth retraction), `--drive-car` targeting, and the joined-client
  spawn cull (removed — unwinnable vs DV's spawners). A2 leaflet question ANSWERED: survives the
  programmatic take, self-cleans in the validator.
- **M3 remaining**: commit+push M3.5c (Cody's go) · M3.2 join phases (deferrable) · friend
  session upgrades exit wording + live-fires owner-side remote couple execution.

**M3.5b CLOSED 2026-07-19: Run B №2 PASSED — "test success!" (Cody).** Real cars spawned on the
joined client, and the proximity culling verified as a full round-trip: consist rolled out past
330 m → dematerialized, Cody caught up → it REmaterialized at its current synced position.
Combined with Run A + Run B №1's world-handover/save-protection passes, every M3.5b exit surface
is verified live. **Edge case also verified (Cody): SERVER CLOSED while the client was still
joined → saving STILL blocked, SP save untouched** — SaveSuppressor.Active only clears in
Leave(), so a dead server fails SAFE (blocked saves), never open. Known UX debt for later (M5's
graceful-mismatch family): after server death the client sits in a dead session (carless world,
saves blocked) until manual Leave — a "session lost, leave to restore" prompt would be kinder.
Remaining: commit + push (Cody's go), then M3.5c.

**M3.5b Run B №1 (2026-07-19): world handover + save protection PASSED; spawn KILLED BY DISTANCE
STREAMING → proximity materialization built (restage waiter armed).** Verified: `cleared 39 local
car(s)` (correctly excluded the 3 remote cars), save = no-op while joined, SP save verified intact
after Leave+reload. The consist spawned at ~359 m (`edge 894, ~359 m from you` — the walker had
rolled on since bot start) and DV's distance streaming destroyed the GameObjects within a second
(`preventDelete` does NOT cover ECS conversion — the M2 storm mechanism, now hitting OUR spawns).
**Fix = proximity materialization (the D10 interest-management shape, forced early):** remote
consists are DATA always, real cars only within **250 m** of the player; voluntary dematerialize
past **330 m** (hysteresis); a DV-destroyed spawned car = quiet "streamed out" (per-car
`OnCarAboutToBeDestroyed` hooks with an our-own-delete guard), 10 s cooldown then re-materialize
on approach. Far sets log once: `is ~N m away — materializes as real cars within 250 m`. Also
seen in the log (not chased): the client was evicted as id 2 during the save-load freeze and
self-re-handshook as id 3 — the career grace/rebind machinery absorbed it by design; watch it.

**M3.5b Run A №2 (2026-07-19): PASSED — real cars spawned and rolling, liveries correct,
interactable.** Two observations banked, both accepted: (1) large inter-car gaps = BOT artifact
(ConsistDriver's synthetic uniform 16 m car length; real host→client replication uses the owner's
actual bogie positions, so spacing will be exact — don't teach the bot car lengths); (2) remote
cars PHASE through local vehicles — accepted for now and safer than run №1's shove-derail; proper
remote↔local collision authority is M3.5c+ scope. Next: Run B (client side).

**M3.5b run №1 (2026-07-19): spawn pipeline PROVEN, placement WRONG — 3 fixes, RESTAGED 15:02.** The log shows the full path ran (`remote consist 61: 3 real
car(s) on the rails (edge 204)`), but the cars materialized ON/BESIDE Cody's own train — the
`--start-edge` hint points at the edge nearest the PLAYER, which is where his train sits. Evidence:
the couple-guard fired right after spawn (a spawned car's scan-based TryCouple grabbed HIS coupler),
and his FlatbedEmpty stress-derailed at 24.9 m/s (kinematic colliders shoving a local car). Also:
repeated DV "Point Set Traveller not moving even though velocity is" warnings from the kinematic
bogies. **Verified live as a bonus: the D14 AboutToSave fix — mid-session autosave wrote the real
$10,000, log line present.** Fixes built (0 warnings): (a) spawn DEFERS while any car position
overlaps existing cars (`CarSpawner.IsBoxOverlappingSimple`; the moving consist clears itself —
one log line while waiting); (b) adjacency coupling now uses EXPLICIT partner couplers
(`CoupleTo`, ≤8 m sanity) — never a scan that can grab a bystander; (c) remote bogies get
`DistanceTrackingEnabled = false` (kills the traveller warning); spawn log now includes
`~N m from you`.

## M3.5c in-game runs (Cody at the PC; payload staged 16:51, game was not running)

**Run A — remote career loop (normal rig: you host, the bot is the remote claimant):**
1. Host per usual on your career save. Regression first: board 1:1 with world jobs, wallet mirror
   lines, `host job capture installed`. NEW at load: `chain-coupler hook installed`.
2. `LocoMP.Bot --claim-first --report-interval 30 --at <coords>`. Expect: bot logs
   `claiming job N…` → `claimed job N`; HOST log: `<gameId> claimed by Bot — taken natively on
   their behalf`; panel shows the job claimed by Bot. **WATCH the physical overview leaflet** at
   that station's board — its fate after a programmatic take is the one unknown (if it lingers,
   putting it through the validator must refuse via the pre-gate: "claimed by Bot", leaflet safe).
3. Every 30 s the bot reports → host log `remote completion check → not finished`; bot logs the
   refusal toast. **THE loop**: physically haul the job's cars to the destination yourself (your
   physics, no booklet needed) → the next bot report → host log `… → COMPLETE — claimant gets
   paid` → bot logs the payout + wallet. Your own wallet must NOT move.
4. Abandon path: re-run the bot with `--claim-first --abandon-after 45`. On abandon: host log
   `<gameId> released — abandoned natively (…)`, job disappears from the board AND the world.
5. Regressions alongside: your own native take on a different job (booklet → validator) still
   works; load/unload any synced car at a warehouse → `car N cargo → … — announced`; complete a
   job so a chained one spawns new cars → `mid-session consist registered (…)`; drive far from a
   consist until `left the streamed world — unbound`, come back → `consist N streamed back in —
   re-bound to its live cars`.

**Run B — drive test (input routing; you host, STAY OUT of your loco's cab):**
1. `LocoMP.Bot --drive --drive-seconds 20 --at <coords>` → bot requests the grant on your first
   loco (`requesting control grant for car N (<livery>)`), then: **your throttle lever moves to
   ~0.35 by itself and the train brake releases** (the loco may creep — that's the point). After
   20 s: throttle back to 0, grant released.
2. While the bot holds the grant, enter that cab → your grant request is refused (log shows the
   holder). After release, cab entry grants normally again.
3. With no bot driving, move your own loco's throttle → the bot logs
   `control state: car N throttle = …` (the owner-state broadcast, join-burst-backed).

**Run C — client side (listen rig: bot hosts, you join; M3.5b regressions ride along):**
1. `LocoMP.Bot --listen --consist 3 --livery <ids> --behavior idle --start-edge <N>`; join
   `127.0.0.1` from the game. Expect the M3.5b flow: world clear, real cars, proximity
   materialization, saves blocked.
2. Enter the bot loco's cab → grant → `cab controls live: your inputs in car N now drive the
   owner's loco` → **move the throttle: the bot logs `throttle input 0.35 → 7.0 m/s` and its
   consist visibly speeds up/slows — you are driving the bot's train from its cab.**
3. Chain-interact on a bot car (unhook a chain): expect
   `chain uncouple on a remote-driven car — asked its simulating player (car N)` and NO local
   split (the bot ignores the request — interception + no-desync is the assertion; owner-side
   execution is core-tested and live-fires at the first friend session).
4. Leave → reload your save (SP integrity regression — quick check).
5. Watch everywhere: zero LocoMP exceptions; any wallet drift; double-fires on the chain hook.

## M3.5a run №2 — PASSED 2026-07-19 (checklist retired)

Full D14 surface + all regressions verified in-game by Cody: "All testing done - Zero bugs or
regressions!" Standing money facts for future sessions: host-native careers start at $2000
(matches SP; core/dedicated default stays $500); on Host the money display IS the LocoMP balance
and the SP amount restores on Leave; starting grants mint on FIRST profile sight only ("Fresh
career" toggle or delete the .lmps to re-mint).

1. Boot + mirror (regression): Host per-player on the new save. Expect the run-№1 lines
   (`built from the live world` — now with MORE purchasable licenses since general ones joined —
   `host job capture installed`, `offered N existing world job(s)`), plus NEW:
   `wallet mirror hooks installed`, `native money now mirrors the LocoMP wallet ($…; $… restored
   on leave)`, and `mirrored N natively-held license(s) to the career` (FH at minimum — the sweep).
   In-game money display == panel wallet from here on.
2. **The run-№1 killer, fixed — native license buy**: buy a license at the CAREER MANAGER with
   in-game money. Expect: purchase works natively, log shows `native purchase captured: $… at the
   career manager` and `native license grant captured: <id>`, panel licenses line gains it, panel
   wallet drops by the price, in-game money re-syncs to match (≤1 s). Then claim a job needing that
   license through the validator → booklet prints, MY JOB appears, **no refusal, leaflet safe**.
3. Panel-shop reverse sync: buy a cheap license from the LocoMP panel shop. Expect wallet drop +
   the game's own license UI/validator recognizes it (native grant applied; log stays quiet or
   shows the apply failure loudly — the latter is a bug).
4. Haul + turn-in (regression): NO validator cash print; `completed natively — reporting for
   payout`; wallet (both displays) up by base payment.
5. **Pre-gate**: with a session job visibly on the board, try to validate an overview for a job
   the board does NOT have yet (freshly generated — put it through within ~a second) — if you can
   catch one, expect the error sound + toast `not on the multiplayer board yet` and the LEAFLET
   SURVIVES. (Hard to time; skipping is fine — the important part is no leaflet is ever consumed
   on a refusal.)
6. Deposit/cancel economy edge: insert money at a register, CANCEL, walk away. Expect money
   returns and settles back to exactly the panel wallet (the reconcile waits for idle registers).
7. Persistence + grace (regression): Leave → money restores to the pre-session SP amount →
   re-Host → wallet/licenses/claims back (bought licenses included). Leave mid-job, re-host
   within 10 min → claim intact.
8. Bot regression: presence + trains unchanged (`--at` + `--start-edge` hints as before).
9. Watch for: double-take at the validator, native abandon at the career manager (should release
   the server claim; note any DEBT/fee side effects — M3.5b audit), job expiry → board entry
   vanishes, zero LocoMP exceptions, and any wallet drift between the two money displays that
   does NOT converge within a second.
- **Milestone:** **M2 — Trains: COMPLETE 2026-07-18** (one-PC exit wording; friend session upgrades
  to official). M2.1 core + M2.2 extractor + M2.3 Shim integration all verified in-game; the exit
  scenario (couple → merge → uncouple → derail → rerail, no snap-back) passed on run №5 with every
  criterion's log line present and zero LocoMP exceptions. M2.1 detail below:
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
2. ~~**M2.2 — world extractor.**~~ **DONE 2026-07-18 — exit criterion met in-game.**
   `Shim/TopologyExtractor` walks `RailTrackRegistryBase.Instance.OrderedRailtracks/OrderedJunctions`
   (EdgeId = registry index — the SAME numbering M2.3 uses for `BogieState.EdgeId`; junction id =
   game `junctionData.junctionId`), nodes via union-find over Branch pointers with 1 m positional
   verification of every join, writes `world-<Application.version>.lmpw` via the UMM panel button.
   **The real B99.7 extraction: 2,073 edges / 279.6 km / 1,886 nodes / 563 junctions — ALL health
   counters zero** (0 position mismatches ⇒ `Branch.first` = target's IN end is empirically proven;
   0 zero-length edges, 0 skipped/duplicate junctions; Player.log exception-free).
   TracksHash `B77208E53A86A3B95DE74FF0BEE9B093`, JunctionsHash `59887E6A2C1E9ED9940EB10DCB51F4F6`
   (compare on B100 to see if the numbering survived). `RealWorldTopologyTests` loads the real dump
   from `tests/data/` (git-ignored; vacuous on game-free machines) and passes: scale, sequential
   edge ids, unique junction ids, entry+branches share a node, one dominant connected component.
   61/61, full sln 0 warnings.
3. **M2.3 — Shim train integration: CODE-COMPLETE + STAGED 2026-07-18 (uncommitted), awaiting the
   in-game M2 exit run.** All planned scope built: supported-build gate live (runtime
   `Application.version` vs `SupportedBuilds=["99-build2702"]`, friendly inert-mod message on unknown
   builds; bot default updated); `Core.World.TopologyWalker` (seeded, junction-aware, `Behind(d)` for
   trailing bogies — the future M6 coaster) + `Bot --consist <n>` ghost train over the REAL topology
   (registers, streams current-epoch snapshots, throws junctions it crosses, survives churn); Shim:
   `TrackIndexMap` (registry-order edge ids + save-format junction ids + (edge,s)→world eval with
   SELF-CALIBRATING point-space — logs whether Vector3d points are absolute or shifted-local),
   `JunctionHook` (inner `Switch(SwitchMode, byte)` overload ONLY + suppressed FORCED remote apply;
   WorldStateSpike deleted), `GhostConsists` (box-car visuals, 12/s lerp + 80 m snap), `TrainSync`
   (host registers all game trainsets, token = game set id + 1; 20 Hz spline capture via
   `traveller.Span` + `TrackDirectionSign`; couple/uncouple via the game's PUBLIC
   `Coupler.Coupled/Uncoupled` events → trainset-end proposals deduped by lower car id; derail by
   POLLING `car.derailed` per tick; grants on `PlayerManager.CarChanged`; commits rebind bookkeeping
   only — host physics already happened). 69/69 game-free (incl. 10 km real-map walker soak + ghost
   end-to-end with 0 stale discards), full sln 0 warnings, payload staged. **Banked:** Krafs.Publicizer
   must set `IncludeCompilerGeneratedMembers="false"` or event `+=` dies with CS0229 (same-name
   backing field); new game refs DV.PointSet / net.smkd.vector3d / DV.ThingTypes (targets ×2 + CI ×2).
4. **M2 exit run №1 FAILED (2026-07-18) — root-caused + FIXED, restaged for run №2.** What worked in
   run №1: hosting, track index, **14 sets / 74 cars registered**, **point-space calibration:
   Absolute, 0.4 m err** (the self-check paid for itself), control grants on cab entry/exit (cars
   3/49/23 granted + released cleanly). What failed: Cody quit to the MAIN MENU mid-session — world
   teardown fired `Uncoupled` on every coupler (proposal storm → server committed splits → "unknown
   cars" resync spam), then the SESSION SURVIVED into the reloaded world with `_worldRegistered`
   still true and a `TrackIndexMap` full of destroyed RailTracks → the bot's ghost registered and
   streamed fine but `TryGetLocalPoint` failed on dead tracks → boxes never positioned → invisible.
   **Fixes (built, 0 warnings, restaged):** TrainSync detects world death (registry singleton null)
   → `WorldUnloaded` event → SessionController closes the session with a clear panel/log message
   (re-host in the new world; bot auto-reconnects + re-registers — tested behavior); proposal paths
   + resync guarded by `WorldAlive` + a `CarAboutToBeDeleted` despawn set (storm suppressed); ghost
   boxes start INACTIVE until first positioned (never an invisible box at origin silently) and an
   all-cars-unresolvable snapshot logs ONE loud "stale world map?" warning; ghost creation logged.
5. **M2 exit run №2 (2026-07-18): pipeline PROVEN, ghost spawned FAR AWAY — fixed.** Log showed
   "ghost consist 19 is on the rails (edge 0)" — everything worked, but the walker starts blind
   (LMPW has no coordinates) so the ghost rolled around kilometres from Cody. Also fixed from the
   same log: registry getters NRE internally when hosting from the loading screen (TryBuild now
   try/catches = "still loading"), and premature hosting latched `_worldRegistered` with 0 sets
   (now retries until trainsets exist; that run's 2nd host registered 18 sets / 83 cars, 0.1 m
   calibration). **New workflow piece: the host logs `ghost-train hint: --start-edge N` next to the
   `--at` line — paste BOTH into the bot.** 70/70, 0 warnings, restaged.
6. **M2 exit run №3 (2026-07-18): GHOST TRAIN VERIFIED IN-GAME** ("looks good!" — 15 sets/89 cars
   registered, hint `--start-edge 1176 ~4 m`, ghost consist 16 on the rails at edge 205 = spatially
   adjacent after warm-up; remote train motion proven through the whole pipeline). Quit-time storm
   leaked once more — scene-unload kills cars before the registry and skips CarAboutToBeDeleted →
   new `IsLeavingWorld` guard on `gameObject.scene.isLoaded` (built, 70/70; **restage pending** —
   game still running held the DLL lock). Also banked: game logs "Junctions hashes match" with OUR
   JunctionsHash; TracksHash VARIES per session (FDEBEB… vs B77208…) while numbering held — use it
   build-to-build only.
7. **M2 exit run №4 (2026-07-18): UNCOUPLE VERIFIED in-game; 3 bugs caught + fixed + restaged
   (18:21).** (a) Storms = DV DISTANCE STREAMING (far cars → ECS entities, GameObjects destroyed,
   genuine Uncoupled cascades; why car counts vary 74/83/89) → fixed with per-car
   `TrainCar.OnCarAboutToBeDestroyed` → despawn set + one-line unbind; (b) couple test made ZERO
   proposals — old dedupe assumed both couplers fire and dropped the higher-id side → now every
   event handled, same-pair collapsed within 0.5 s; (c) L-039's derail unreported — polling sat
   inside the snapshot loop's early-break → now polled for every live car first. 70/70, 0 warnings.
8. ~~**M2 exit FINALE.**~~ **PASSED 2026-07-18 (run №5) — M2 CLOSED (one-PC wording).** Couple
   contact 77+78 → merged set 20 (fresh id, epoch machinery live) → clean split at 17/18; derail
   reported + rerail requested (car 78 / L-014); grants clean; ghost beside the player; no
   snap-back, zero LocoMP exceptions; teardown tamed to one unbind line per set. Friend session
   upgrades the wording to official. Next milestone: M3 (07-ROADMAP).
5. **Deferred until contributors (Cody, 2026-07-18):** wire CI Steam/Nexus secrets; set `.ci/depot.json` manifest. Red `build.yml` on push accepted until then.
6. **Repo residuals, whenever** (05 §7): branch protection, DCO app (optional), repo topics.

## Push state
- **PUSHED 2026-07-20 (Cody's go): M4.4 (Comms radio) as three dependency-ordered commits** `5f073f4`
  (feat(core): protocol v8 + CommsActionRequest/Command + CarDeleteNotice + CommsActionKind +
  TryDeleteCar + FeeExternal target peer + 5 tests) → `887cd03` (feat(comms): CommsRadioHook +
  CommsRadioSync, WalletMirror isHost + client mirror, SessionController/Main wiring, bot
  `--rerail`/`--clear` + CHANGELOG) → `f9981f7` (docs). `b83414a..f9981f7`, in sync with origin/main.
  Each commit builds independently (Core has no Shim dep). Remote was unchanged since the M4.3 push.
  The recon doc (`research/comms-radio-recon.md`) lives in the private parent corpus, not the repo.
- **PUSHED 2026-07-20 (Cody's go): M4.3 (Shops) as three dependency-ordered commits** `8fd47af`
  (feat(core): protocol v7 + `ItemShopCatalog` 59 + ClientItems.ShopCatalog + catalog-on-join test) →
  `8870cd3` (feat(shops): `ShopCatalogBuilder`, `DrawShop()` + item toast, bot `--buy` + CHANGELOG) →
  `d742960` (docs). `cc72abd..d742960`, in sync with origin/main. Each commit builds independently
  (Core has no Shim dep; the Shim/bot commit only adds a builder + panel + flag). Remote was unchanged
  since the M4.2 push (no canary movement). Commit messages authored via `git commit -s -F <file>`
  (DCO sign-off + the PS-5.1-quote-mangling workaround). In-game verification rides the batched M4
  smoke pass.
- **PUSHED 2026-07-20 (Cody's go): M4.2 (Shim ItemSync) as two commits** `64a3382` (feat: ItemSync
  + PresenceShim helpers + SessionController wiring + bot `--grab-items` + CHANGELOG) → docs (STATE +
  SESSION, this commit). Only two commits (not the usual three) because M4.2 has NO Core changes —
  the game-free item core was M4.1. Both build clean; every seam is a public game event so there is
  no Harmony/patch ordering to sequence. Pushed ahead of the live run under the new batched-testing
  cadence (in-game verification joins the M4 milestone smoke pass). Remote was unchanged since the
  M4.1 push (no canary movement expected).
- **PUSHED 2026-07-19 (Cody's go): the debt/polish pass as three dependency-ordered commits**
  `bc3b62c` (Core/Transport: session-loss seam, 15 s timeout, Loopback UDP-parity dispose) →
  `b03a114` (mod/Shim/bot: session-lost prompt, chain-request execution + product adoption,
  `--derail-car`, input-drop once-log) → `c80df71` (docs). Remote unchanged since the M3.5c
  push (no canary movement). Pushed ahead of the smoke run on Cody's call — the checklist
  rides the next game session.
- **PUSHED 2026-07-19 (Cody's go): M3.5c as three dependency-ordered commits** `7b25f3d` (Core
  protocol v5 + tests) → `5f4ae98` (Shim/mod/bot in-game half) → `00677a8` (docs). Remote was
  unchanged since the M3.5b push (no canary movement). Push-tooling note: PowerShell 5.1 mangles
  embedded double quotes in `git commit -m` here-strings (commit 1's message lost a clause and
  had to be history-rewritten pre-push) — ALWAYS use `git commit -F <file>` for multiline
  messages from this shell.
- **PUSHED 2026-07-19 (Cody's go): M3.5b as three dependency-ordered commits** `102ba8f` (Core
  protocol v4) → `cb259bf` (Shim/mod/bot real-car replication) → `ab5cd4a` (docs). Remote was
  unchanged since the M3.5a push (no canary movement).
- **Everything through M3.1 is PUSHED** (M2 arc `ce41556..8594e4f`; M3.1 `53d642b`, Cody's go 2026-07-18). Post-push CI as always: `build.yml` red at the Steam step (accepted until contributors).
- **PUSHED 2026-07-19 (Cody's go): three dependency-ordered commits** `48e72a1` (Core career
  sync + tests) → `ae97710` (Shim/mod in-game integration) → `94d1957` (docs/CI), rebased over
  the canary bot's `55f2bb1` (`ci: record initial DV buildid 20251481` — the nightly watcher
  doing its designed job; first foreign commit on main). Ordered so every commit builds (the
  planned per-feature split wasn't honest: M3.3, D13, and D14 edits interleave within the same
  files, and file-level surgery would have produced non-building intermediates).
- Staged payload in the game's `Mods/LocoMP/` = **2026-07-19 13:22 build** = the verified commit.

## Blockers
- None. **M4.4 (Comms radio) BUILT + PUSHED 2026-07-20 (Cody's go)** (`5f073f4`/`887cd03`/`f9981f7`);
  161/161 ×3, 0 warnings, staged to Mods/ + dist/. M4.1–M4.4 all pushed. In-game verification joins the **batched
  M4 smoke pass** (comms Runs A/B/C banked below). **M4 exit progress:** ✅ a client completes a job →
  right wallet (M3.5a–c); ✅ a client buys a lantern → right wallet (M4.3); ✅ drop → another player
  picks it up (M4.2); ✅ server restart persists items/inventory (M4.1); ✅ comms-radio actions for
  all players + fees (M4.4). What's LEFT to close M4: **manual service** (07 §M4) — the last scope
  item. Follow-on slices when Cody wants them: remote SUMMON; held-item avatar display (M4.2 Option 2,
  protocol v9); live shop stock replication. Separately: M3.2 join phases (deferrable). D16 (M5 UI/UX)
  planned in `../10-M5-UIUX-PLAN.md`.

## M4 milestone smoke pass (BATCHED — run all M4 slice checklists in one game session)

Testing cadence changed 2026-07-20 (Cody): rather than a live run per burst, the M4 slices are
verified together in one consolidated pass at the milestone boundary. This section accumulates each
slice's checklist; run them back-to-back next time the game is up. So far:

### M4.2 — world-item loop (staged, pushed)

The one-PC rig proves the loop with the headless bot as the "other player" (no game world, so it
picks up / drops via the wire — exactly how it drove trains and jobs).

**Run A — host drops, bot carries, drops back:**
1. Host per usual on your career save. NEW at load: `host item capture installed — offered N world
   item(s) to the session` (N = whatever's already lying in your world; 0 is fine). Panel shows an
   `Items —` line once any exist.
2. **Drop a handheld item** (lantern, boombox, any grabbable) on the ground. Host log:
   `world item <id> (<prefab>) left the world locally`? NO — that's the pickup line. On a DROP the
   host is the source: no despawn; the item registers silently and the panel `Items — 1 in the
   world` ticks up. (Watch for a `request refused` line — if you see `register: only the world
   source…` the AcceptExternalItems wiring regressed.)
3. `LocoMP.Bot --grab-items --at <your coords> --drop-after 15`. Expect: bot logs
   `picking up world item <id>…` → `picked up item <id> (<prefab>)`; **the item VANISHES from your
   world** (host despawned it — a remote is "holding" it); panel flips to `0 in the world, 1
   carried`. After 15 s the bot logs `dropping item <id>…` → **the item REAPPEARS** near your --at
   point (panel `1 in the world, 0 carried`). That round trip IS the win condition.
4. Pick the reappeared item up YOURSELF (walk over, grab it natively). Host log: `world item <id>
   … left the world locally — despawning from the session`; panel `0 in the world`. (You now hold
   it natively; it's yours, in your SP save.)
5. Leave → the bot's replicas (none on the host normally) are cleaned; your own native items are
   UNTOUCHED (reload not required on the host). Re-host → the sweep re-offers whatever's still lying
   around.
6. Watch for: zero LocoMP exceptions; items that DON'T disappear when the bot grabs them (capture/
   despawn gap); items that reappear FROZEN or get stream-disabled when you walk away (the
   BelongsToPlayer keep-alive failing); a dropped item teleporting "home" instead of staying put
   (RespawnOnDrop fighting the placement — a recon-flagged risk to confirm).

**Deferred to a friend session (banked):** a real joined GAME client grabbing a replica natively
(only the HOST captures native grabs this slice — the bot drives the client side over the wire).

### M4.3 — shops (staged, awaiting push)

The one-PC rig proves the purchase win condition with the bot as the "client" (it has a wallet and a
possession over the wire; it buys, then routes the item through M4.2's world loop so the host sees it).

**Run A — bot buys a lantern (its wallet), drops it, host picks it up:**
1. Host per usual on your career save. NEW at load: `[shop] catalog: N item(s) for sale from M
   shop(s)`. Open the panel → a **Shop (N)** toggle now appears; expand it → the item list with
   prices (matches DV's shop prices; in a non-Career session most are $0). Your own wallet line is
   the D14 native-money view as before.
2. `LocoMP.Bot --buy <PrefabName> --at <your coords> --drop-after 15`. Use a real cheap item's
   prefab name (from the panel Shop list — e.g. a lantern). Expect: bot logs `buying <Prefab> from
   the shop…` → `bought item <id> (<Prefab>) — wallet now $X (the host's wallet is untouched)`.
   **THE proof:** the bot's wallet drops by the price; **YOUR wallet does NOT move** (per-player
   scope — the incumbent's gap closed for purchases). Panel `Items — 0 in the world, 1 carried`.
3. After 15 s the bot logs `dropping item <id>…` → the item **materializes in your world** near the
   `--at` point (M4.2's host ItemSync spawns it: `world item <id> (<Prefab>) materialized`); panel
   `1 in the world, 0 carried`. Walk over and grab it natively → `left the world locally —
   despawning from the session`; it's now yours in your SP save. That whole line — client buys →
   drops → host receives — is the M4 exit demo on one PC.
4. Refusal paths: `--buy NotARealPrefab` → bot toast/host log `purchase: … is not for sale`, nothing
   minted, no wallet move. Buy something pricier than the bot can afford (rich catalog) →
   `purchase: insufficient funds`, nothing minted.
5. Panel self-serve (no bot): with a rich-enough wallet, expand Shop → **Buy** an item → wallet
   drops, `Items — … carried` ticks up, a **Drop here** row appears → click it → the item spawns at
   your feet (M4.2 loop). NB on the HOST this exercises the "materialize a genuinely-remote world
   item" path (the host is the world source but did not natively drop this one).
6. Persistence: buy with the bot, DON'T drop (`--drop-after 99999`), Leave + re-host → the bot's
   possession resumes from the `.lmps` save (M4.1 cold-restart tail) — or drop it first and the
   WORLD item resumes at its pose. Either way the purchased item survives a restart.
7. Watch for: any wallet drift between the two money displays that doesn't converge in ~1 s; a
   bought item that never materializes on drop (the M4.2 spawn/keep-alive path); the catalog showing
   0 items (shop controller not up at host time — re-host once the world's fully loaded).

**Deferred to a friend session / later slice (banked):** a real joined GAME client buying from the
panel and physically holding the item (this slice routes the purchase through the world via drop —
materialize-into-inventory is the held-item slice); the host's real shelf stock decrementing with
LocoMP purchases (client buys are independent mints — live stock replication is a later slice).

### M4.4 — comms radio (staged, awaiting push)

**Run A — host fees (you host; use your own comms radio):** at load expect `[comms] comms-radio hook
installed` and, once the world's up, `[comms] host comms-radio fee capture installed`. Note your
wallet. (1) **Rerail**: derail a car (or find one), comms-radio Rerail it → the fee (`$…`) leaves
your wallet AND STAYS gone (`[comms] rerail <car>: $… charged to your wallet`) — before this slice it
refunded itself within a second. Free cases still free: a HandCar, or in restricted/newbie mode. (2)
**Clear**: comms-radio Clear a non-player car → wallet drops by the delete price (0 for a
player-spawned car); `[comms] car N deleted — removing it from the session`. (3) **Summon**: summon a
work train from a garage → wallet drops by the summon price; a non-garage vehicle is free. Watch: the
two money displays (native == panel) converge within ~1 s; zero LocoMP exceptions.

**Run B — delete removes the replica (listen rig: bot hosts, you join, OR normal rig + bot joins):**
with the bot joined and a consist synced, delete one of the host's cars → on the OTHER peer the
replica **vanishes** (previously it lingered as a ghost). Deleting a lone car removes the whole set;
deleting one car of a consist leaves the rest.

**Run C — remote action (normal rig: you host, the bot is the remote initiator):**
1. Host on your career save. Get a car's plate from the host log / a synced consist (e.g. `L-014`).
2. `LocoMP.Bot --rerail L-014 --at <coords>` (derail L-014 first). Expect: bot logs `asking the host
   to rerail car N (L-014) …`; HOST log `[comms] rerailed car N for player <bot>`; the car returns to
   the rails near your `--at`; **the BOT's wallet drops by the fee, YOURS does not** (the initiator
   pays — the M4 "for all players" invariant). The set-rerail then propagates via the normal path.
3. `LocoMP.Bot --clear L-014` → HOST `[comms] deleted car N for player <bot> — removed from the
   session`; the car vanishes for everyone; the bot's wallet drops (0 if it was player-spawned).
4. Affordability: run the bot with a near-empty wallet (buy something first, or a fresh profile on a
   pricey action) → the fee is refused server-side and logged; note whether the action still happened
   (the known banked gap — the bot has no native affordability gate; a real client's DOES via the new
   client wallet mirror).
5. Watch: zero LocoMP exceptions; no double-charge; the host's own wallet never moves for a remote
   action.

**Deferred to a friend session / later slice (banked):** a REAL joined game client using its own
comms radio (the client-side `OnUse` interception — the bot drives the wire directly, not a radio);
remote SUMMON (livery/garage/location resolution on the host); exact rerail placement (trimmed track
search vs the game's full valid-point solve); the affordability edge above (a real client is gated by
the wallet mirror, but a bot/mismatch could act then have the fee refused).

### M4.5 — manual service (staged, uncommitted)

Mostly a VERIFY pass — the metered fee already rides D14, so the checks confirm that rather than a new
feature. All host-side (no bot involvement; a serviceable loco is the host's own).

**Run A — metered service bills the wallet (the D14 confirmation):**
1. Host on your career save. Drive a loco needing fuel/repair into a manual-service bay.
2. Note your wallet ($). Service normally: connect the hose / turn the valve, deposit cash, hit the
   **Buy** button. The loco refuels/repairs.
3. **Confirm:** your wallet drops by the service cost and STAYS down after ~1 s (the reconcile does
   NOT refund it — the till-holds-cash guard + the `CashRegisterWithModules.Buy` → `FeeExternal` hook
   are doing their jobs). No LocoMP exceptions. (There's no new log line for the metered path — it's
   the existing D14 wallet hook; the proof is simply that the money stays spent.)
4. **License:** buy the Manual Service general license at a career manager if you don't hold it → it
   mirrors like any general license (join a bot afterward with auto-grant on and it inherits it).

**Run B — the guard (best-effort; only if you can trigger it):** `RefillAll()`/`RepairAll()` have no
in-game callers, so there's usually nothing to press. IF you have a way to invoke them (a mod/scene
button that calls them, or a free-service mode), expect a host log `[service] free refuel at the bay —
billed $X to your wallet` (or `… no priced deficit to bill` if the bay had no live prices) and your
wallet to drop accordingly — the service still applies, it's just no longer free. Its absence in normal
play is itself the expected result; note "no bypass path fired" and move on.

## Session log
- **2026-07-20** — **M4.5 (Manual service) — recon = VERIFY-NOT-BUILD; RefillAll/RepairAll guard BUILT (uncommitted).**
  Last M4 scope item. Recon (`research/manual-service-recon.md`) proved manual service is the INVERSE
  of comms-radio: the metered loop commits via `CashRegisterWithModules.Buy()`, one of the two `Buy`
  overrides D14's WalletMirror already patches → the refuel/repair fee has been economy-correct since
  D14 (no direct `RemoveMoney`, no free-service hole). `ManualService` license already mirrors (D14
  LicenseSync). Resource/damage STATE sync is out of scope (loco-sim layer LocoMP doesn't replicate;
  seams banked). Cody picked "also guard RefillAll/RepairAll" over verify-only: those two bypass the
  buy button (free full service) but have ZERO in-game callers — so a host-side Harmony prefix
  (`ManualServiceHook` + `ManualServiceSync`) defensively bills the equivalent cost (read off the bay's
  own modules: `BuyMaxLimit` × `Data.pricePerUnit`) as a self-scope `FeeExternal`, never suppressing
  the service, disarmed outside a session. NO Core/protocol/game-ref change. **161/161 ×3**, full sln 0
  warnings, STAGED. **Closes the M4 scope; only the batched smoke pass remains.** PUSHED `0e65c0a` feat / `8c70a0b` docs.
- **2026-07-20** — **M4.4 (Comms-radio actions for all players) BUILT + PUSHED (`5f073f4`/`887cd03`/`f9981f7`, Cody's go).** Cody chose "go
  for all three sub-slices" after the recon flagged the size + one-PC-testability split. Recon
  decompiled the three comms-radio modes → `research/comms-radio-recon.md`; the load-bearing find:
  each charges a DIRECT `Inventory.RemoveMoney`, which D14's WalletMirror reconcile REVERTS → host
  acted free in a session. Built: (1) host fees — `CommsRadioSync` + `CommsRadioHook` (OnUse prefix,
  confirm-state only; price snapshot → mode success event → FeeExternal target 0); (2) delete removal
  — `TryDeleteCar` + `CarDeleteNotice` so a deleted car clears replicas everywhere; (3) remote
  initiation — client suppress+route (CommsActionRequest/Command), host executes + charges the
  initiator via FeeExternal's new target peer, client WalletMirror for correct affordability. Bot
  `--rerail`/`--clear` drive the wire path (client radio interception = friend-session). Remote summon
  banked. Protocol v7→v8, no new game ref, prices reimplemented clean-room. 156→**161 ×3**, full sln 0
  warnings, STAGED. Comms Runs A/B/C banked in the M4 smoke pass. Push awaits Cody's go.
- **2026-07-20** — **M4.3 (Shops) BUILT + PUSHED (`8fd47af`/`8870cd3`/`d742960`, Cody's go).** Cody picked "Shops + purchases" as the next
  M4 slice (highest exit value — the "a client buys a lantern" half of the M4 exit). Recon-first
  over the shop decomp dumps confirmed the whole flow: `GlobalShopController.Instance.shopItemsData`
  holds every purchasable item + `basePrice`; the purchase TRANSACTION was already built + fuzzed in
  M4.1 (charge-then-mint into the buyer's possession, wallet-scope isolated). So this slice was
  front-end only: Core shipped the catalog to clients (protocol v6→v7, `ItemShopCatalog` msg 59,
  `ClientItems.ShopCatalog`, +1 test = 156); Shim added `ShopCatalogBuilder` (read-only walk of the
  live shops → `ItemConfig.ShopPrices`, no Harmony, no new game ref — GlobalShopController is in
  Assembly-CSharp) + `DrawShop()` panel (Buy per item, Drop-here on carried items) + item-rejection
  toast; bot gained `--buy <prefab>` (buy → mint in own possession → hold → drop, routing the item
  through M4.2's world loop). One-PC win-condition driver proven headless. Design call banked: a
  client's purchase is a pure LocoMP mint independent of the host's shelf (correct for the win
  condition; host-shelf stock drift + live restock are a later slice). 156/156 ×3, full sln 0
  warnings, STAGED. Shops Run-A checklist banked in the M4 smoke pass. Push awaits Cody's go.
- **2026-07-20** — **M4.2 (Shim ItemSync — world-item loop) BUILT, uncommitted.** Cody picked
  Option 1 of the M4.2 cut (world drops only; held-display + shops deferred). Recon-first over the
  ~30 decomp dumps confirmed every seam: `itemPrefabName` is the only identity (LocoMP mints netIds,
  done in M4.1), capture = public `StorageBase.ItemAdded/ItemRemoved` + `ItemBase.AboutToBeDestroyed`
  (NO Harmony), spawn = `Resources.Load` + `AddItemToWorldStorage`, and — the clincher — the
  `ItemDisabler` exempts `BelongsToPlayer` items on a non-paint-station parent, so keep-alive is FREE
  (no proximity band, no patch). Built `ItemSync` (host capture + reconcile-materialization +
  `_applying` guard + `_spawnedIds` so Dispose spares host natives), PresenceShim item-pose helpers,
  host `ItemConfig{AcceptExternalItems=true}` (the D13 `AcceptExternalJobs` analog — default gates
  the host's own registrations OFF), bot `--grab-items`. Defining-assembly check: everything is in
  already-referenced Assembly-CSharp + DV.Inventory — NO new game ref, NO protocol bump. 155/155 ×3,
  full sln 0 warnings, STAGED. Run checklist above rides the next game session.
- **2026-07-19** — **DEBT/POLISH PASS built (Cody's pick for the burst; M4/M3.2 deferred).**
  Swept every banked debt across STATE/SESSION/08-RISKS into a ledger, then closed the
  solo-closable ones: session-lost prompt (NetClient.Disconnected + 3 s recovery window, no
  auto-Leave — fail-safe preserved), disconnect timeout 15 s (root cause of the mid-load
  eviction), AbandonJob audit (decompiled B99.7: NO flat abandon fine — only accrued car service
  costs stage, identically for complete/abandon/expire; rollback-after-take stages zero; career-
  manager debt payments already ride the D14 Buy hook), bot executes remote chain requests +
  adopts transaction products by lead car (one-PC rig now fires the owner-side path),
  `--derail-car` fires the null-track spawn leg on demand, input-drop once-logging in
  CabControlSync (the "controls exist" debt was already guarded — re-audited, retired), Loopback
  server-dispose now notifies clients (UDP parity). Docs: R16 host-presence scoping, R8/R10
  refresh, O11 guest progression + O12 LMPS ratification for Cody. 125 → 129 tests ×3, full sln
  0 warnings. Uncommitted; smoke checklist banked in Where-things-stand.
- **2026-07-19** — **RUN C PASSED — M3.5c RUNS CLOSED.** Evidence, all with log lines and zero
  LocoMP exceptions: world handover (9 cars cleared, no post-clear spawn fights), real cars at
  60 m, cab entry → grant → `cab controls live … (13 controls)` → Cody DROVE the bot's train
  from its cab (bot console: throttle input → speed), grant released on exit, chain uncouple AND
  couple both intercepted + routed (`asked its simulating player`), leave → save reload intact.
  M3.5c complete (one-PC wording). Next: commit + push on Cody's go.
- **2026-07-19** — **Run C attempt №2 still spammed — the restoration locos are
  `playerSpawnedCar == true` (DV flags personal restoration projects as player-spawned), so the
  narrowed cull filter didn't exclude them. CULL REMOVED ENTIRELY** (back to pure M3.5b join
  behavior: one clear at join, whatever the client's world spawns afterwards coexists unsynced).
  Two melted runs = enough evidence that deleting world spawns on a live client is unwinnable;
  CHANGELOG claim corrected. 125/125, staged via waiter.
- **2026-07-19** — **Run B PASSED (with `--drive-car <plate>` added — the bot first drove L-014
  instead of Cody's L-013; "first loco on the wire" isn't "the loco you're next to"; pipeline
  itself worked). Run C blocker: the joined-client stray-spawn CULL fought DV's world spawners —
  the three RESTORATION locos (DE2/DH4/S060) respawned every frame (per-frame
  LocoRestorationController log spam = Cody's "repeatedly grants licences"), client slowdown.
  Fixed: cull narrowed to `TrainCar.playerSpawnedCar` only (crew vehicle/garage); world-driven
  spawns (station loco spawners, restoration controllers, streamer restorations) are left to
  coexist unsynced — deleting them is a fight we can't win; true world suppression = dedicated
  server (M6). 125/125, staged via waiter.**
- **2026-07-19** — **A4 PASSED — THE FULL REMOTE LOOP IS LIVE: `SM-FH-80: remote completion check
  → COMPLETE — claimant gets paid`; host wallet stayed $2000 (personal-scope payout = policy
  working). Same log surfaced finding №6: FAR-STATION EXPIRY under a claim** — GF-FH-32 (Golden
  Falls) was claimed + taken on behalf, then DV expired it natively while the host stayed at SM:
  in host-native mode the world's job lifecycle follows the HOST's presence, so a remote claim on
  a far station's job can die under its claimant (left a zombie: board claim + reports into a
  void until TTL). Fixed (125/125 ×3): `TryRetract` now retracts CLAIMED external jobs (the world
  is the truth — a claim on a dead native job can never complete; core-generated claims stay
  protected), ServerCareer drops pending completes + toasts the claimant ("the host world expired
  this job — claim released"), JobCapture retracts on native expiry regardless of claim state.
  CareerSessionTests' "claimed is protected" pin rewritten to the new rule. **BANKED for
  08-RISKS/M6:** remote claims are only reliable near the host in host-native mode — cars stream
  out and jobs expire with host distance; real fix = dedicated server (M6) or host-presence
  scoping; friend-session guidance = crew together around the host.
- **2026-07-19** — **run A finding №5: CAR IDENTITY in routes — "no loadable trains parked on the
  track" despite empties at the bay; the warehouse machine services THE job's exact logic cars
  (task.cars), not any empty of the right type. Route steps now carry the step's car span
  ("load [5× G-123 … G-130] @ SM-A1-L") from TaskData.cars (logic Car.ID). 125/125, staged via
  waiter.** Also recon-confirmed en route: WarehouseMachine is pure logic layer with NO player
  concept — readyForMachine flips on the JobTaken event, which the programmatic take fires, so
  take-on-behalf satisfies the machine by construction.
- **2026-07-19** — **run A finding №4: ROUTE VISIBILITY — remote claims got no booklet, so nobody
  could see the destination TRACK; fixed (125/125 ×3).** Cody's observation: the overview leaflet
  survives a programmatic take (and self-cleans if put through the validator), but the actual
  spotting tracks only exist in the BOOKLET the native claim swap prints — which a take-on-behalf
  never prints. Fix: JobCapture now extracts the booklet's essence at capture time —
  `Task.GetTaskData()` is DV's own uniform recursive flattening (nestedTasks holds live Task
  objects, not TaskData — DV quirk); leaf steps render as "load @ SM-A1-L, move → SM-B4-O" via
  `TrackID.FullDisplayID` — and rides it in the captured JobDef's task Param (opaque string, no
  protocol change; 500-char cap well under the 4096 wire cap; yard-name fallback on any
  extraction failure). Shown on: the claimant's MY JOB row, the others-row (the host hauling for
  a remote claimant needs it — the rig's A4 flow literally is that), and the bot's claim log
  (`route: …`). ALSO verified by this run: A2 leaflet fate answered (survives, self-cleans),
  host grants work live (`Shunting → Bot` — DV's SL jobs DO require the Shunting license, banked),
  claim → native take → refused-completion loop all green, ghost cleanup ran (3 retracted; 2
  claimed ghosts from the old bot's grace correctly refused retraction — they die on expiry).
- **2026-07-19** — **run A blocker №3: GHOST JOBS — bot claimed a board job with no native
  counterpart; root-caused + fixed (125/125 ×3, waiter re-armed).** The career save persisted the
  WHOLE board incl. available external jobs; a re-host resumes them, but DV's world has different
  jobs by then — a saved external with no live native job is claimable yet backed by nothing
  (take-on-behalf silently no-ops, completion can only reply "not found"). The earlier
  "already registered" sweep refusals were the smoke: only 3 saved entries matched the live
  world. Fix (both halves): (a) Core — `Capture` no longer persists AVAILABLE external jobs
  (they're live-world mirrors; the join sweep re-offers them every session; CLAIMED externals
  still persist for the grace story); (b) Shim — JobCapture reconciles a resumed board on
  JobAdded: available external + no native job (post-sweep) → `RetractJob` with a "ghost" log
  line (cleans saves written before (a)); plus a loud WARNING if a claim ever lands on a ghost
  anyway. Note: an existing save's CLAIMED ghost (e.g. the bot's, random key) self-heals via
  claim-TTL/grace expiry → external release → job dies.
- **2026-07-19** — **HOST LICENSE GRANTS built (run A follow-up, Cody: "we need a way for the bot
  to gain licences").** The eligibility scan confirmed the diagnosis: the board's jobs all gate
  on licenses a fresh profile doesn't hold, and no starting wallet ($2000) can buy them
  ($30k–$200k) — which is also the REAL design gap for a fresh friend joining a mature world
  (flag for Cody/00: guest progression on mature hosts; host grants are the interim answer).
  Implemented: `LicenseGrantExternal` (42) gains a target peer id (0 = own scope — the D14
  native-mirror path unchanged; v5 is unpublished so the shape change is free), world-source-
  gated, charge-free, idempotent; panel section "Grant licenses to a player (host)" (pick player
  → catalog Grant buttons, toast + log line). The BOT needed zero changes — its rescan already
  triggers on LicenseGranted, so a grant → claim happens automatically. Tests 122 → **124/124
  ×3** (grant unlocks a gated claim + host scope untouched + conservation; refusals for unknown
  peers / non-world-source). Payload restage waiter still armed on game exit.
- **2026-07-19** — **M3.5c run A №1: BLOCKED at the remote claim — bot's claim refused; fixed
  same hour (restage waiter armed).** Two findings: (a) `--claim-first` claimed the lowest-id
  board job blindly, but the bot is a FRESH profile holding only the starting-license floor
  (Transport requirements) while the host's mature save satisfies everything — the claim bounced
  on the license gate. Bot now claims only ELIGIBLE jobs (license-subset check against its
  mirror), logs each skip with the exact missing license (useful data: settles empirically what
  DV's SL/SU shunting jobs require), blacklists refused ids, and re-scans every 2 s + on
  JobAdded/LicenseGranted. (b) Server-side refusals go only to the requesting peer — the HOST
  log never showed the reason (diagnosis needed the bot console). SessionController now logs
  `_server.Career.RequestRejected` + `_server.Trains.ProposalRejected` as `[server] … refused
  (peer N)` lines. Also seen in the log, benign: panel license-shop Buy clicks refused on
  insufficient funds ($2000 vs $30k–$200k DV prices) — expected; and the mid-session native save
  correctly wrote the real SP balance (D14 AboutToSave regression passing live).
- **2026-07-19** — **M3.5c BUILT (remote claim parity + multi-crew) + STAGED 16:51, uncommitted.**
  Recon-first over B99.7: `JobsManager.TakeJob/TryToCompleteAJob` public (programmatic take +
  native task-tree validation — the remote-claim architecture unlock), `OverridableBaseControl`
  {ControlType, Value, Set, ControlUpdated} + `BaseControlsOverrider.GetControl` (one generic cab
  surface; the 42-value enum fits the wire byte), `ChainCouplerCouplerAdapter.TryCouple/
  TryUncouple` (the single chain funnel), logic-car cargo via polling. **Found a real M3.5b wire
  bug by code reading**: registration stripped identity/cargo (v4 added them to defs, never to
  the registration message) — remote spawns actually ran on fallback identity; fixed in the v5
  bump. Core: messages 44–49, deferred completion verification w/ timeout, external-release-
  retires-job semantics, cargo folded into defs epoch-stable, control state stored + join-burst
  replayed, couple/uncouple routed to sim owner. Shim: `CabControlSync` + `ChainHook` (new),
  JobCapture remote-claim mirror (take/abandon on behalf, CompleteQuery answered natively),
  TrainSync dynamic registration + GameId rebind (closes the banked respawn-rebinding debt) +
  1 Hz cargo polling + joined-client stray-spawn cull, panel remote Claim/Report. Bot:
  `--claim-first/--report-interval/--abandon-after/--drive`; listen-mode consist obeys throttle
  input. Tests 110 → **122/122 ×3**, full sln 0 warnings, headless listen smoke passed. Next:
  Cody's three runs (checklist above), then commit + push.
- **2026-07-19** — **M3.5b BUILT (real-car replication) + STAGED 14:31, uncommitted.** Recon-first
  over B99.7 nailed the whole surface as first-class API: `CarSpawner.SpawnLoadedCar` (savegame
  restore = identity-preserving per-bogie spawn), `Bogie.SetTrack`, `Coupler.preventAutoCouple`/
  `TryCouple`/`Uncouple`, `TrainCar.preventDelete`, `Globals.G.Types.TryGetLivery/TryGetCargo`,
  `SaveGameManager.SaveAllowed` + `AboutToSave`. Core: CarDef identity+cargo (protocol v4, save
  schema v3), 110/110 ×3. Shim: `RealCarSync` (spawn/kinematic-drive/couple-mirror/transaction
  re-map by car id/self-heal + per-set ghost fallback), joined-client world clear +
  `SaveSuppressor`, WalletMirror `AboutToSave` fix (D14 debt closed), host livery hint. Bot:
  `--listen` host mode (game joins as client — the one-PC client rig), `--livery`/`--cargo`;
  headless listen smoke passed. Full sln 0 warnings. Key design call: remote cars are KINEMATIC
  (owner physics is the only truth — forecloses the incumbent's snap-back class); local↔remote
  manual couples auto-revert until the M3.5c request path. Next: Cody's two runs (host rig with
  real liveries; bot-hosted client join incl. SP-save-integrity assertion), then commit + push.
  (13:14).** Cody's findings: board 1:1 with world jobs, FH granted; BUT a native career-manager
  license purchase was invisible to LocoMP → validator-approved take → server refusal → AbandonJob
  rollback DESTROYED the leaflet (log claimed "returned to world" — wrong; abandoned ≠ available,
  no lost-items-shed recovery). Root cause: D13 unified jobs but not licenses/money. D14 = full
  wallet unification (options were suppress / sync-grants-only / unify — Cody picked unify).
  Built: Core 42/43 external grant+fee (grant charge-free + idempotent, fee = policy burn),
  LicenseSync (event-driven, both directions + mature-save sweep, host-only), WalletMirror
  (PlayerMoney = ledger view; Buy() prefix+postfix on BOTH register overrides — virtual, base
  patch catches nothing; reconcile idles while registers hold cash), validator pre-gate (leaflet
  never consumed on a doomed take; backstop rollback now retracts the board entry + honest log),
  MaxConcurrentClaims=99 host-native (DV's own licensing governs), general licenses in catalog,
  DV.Inventory ref ×4 spots. 104 → **109/109 ×3**, full sln 0 warnings. Payload staged via
  game-exit waiter. Follow-up (Cody): host-native StartingBalanceCents → **$2000** to match SP —
  under D14 the wallet IS the license budget and $500 can't buy a license (value hardcoded in
  CareerConfigBuilder; not exposed via GameParams, re-check on B100; grant mints on FIRST sight
  only, so run №2 needs "Fresh career"). Restaged 13:22. **Run №2 PASSED same day — zero bugs,
  zero regressions; M3.5a CLOSED; PUSHED `48e72a1..94d1957` (Cody's go), rebased over the canary
  bot's first buildid commit `55f2bb1`.**
- **2026-07-18** — **M3.1 PUSHED (`53d642b`, Cody's go) + M3.3 built (Shim career integration),
  uncommitted.** Recon by reflection (scratch/dv-reflect.ps1 outside the repo): 02 item 4 = CLEAN —
  `StationProceduralJobsController.TryToGenerateJobs` is the single generation choke point.
  JobGenSuppressor (false-prefix + StopAll), CareerConfigBuilder (real stations/absolute
  positions/route distances/cargo-route specs/license catalog from the live world),
  PlayerKeyStore (persistentDataPath), host career resume (CAREER HALF ONLY in host mode — live
  world re-registers consists; full restore = dedicated path) + 2-min autosave, panel job
  board/shop/toasts. Core: route-constrained specs w/ distance payouts + server-side task
  proximity vs the claimant's own pose; CareerState carries the license catalog now. 102/102 ×3,
  full sln 0 warnings. Payload staging deferred — game running held the DLL lock (background
  waiter). Next: Cody's in-game M3 run (checklist above), then commit + push.
- **2026-07-18** — **M3.1 built (game-free career core), committed.** Protocol v3 (stable player
  key in the handshake — reconnect credential, never broadcast; career messages 29–39),
  `CareerRegistry`/`EconomyLedger`/`ProgressionPolicy` (both D3 presets behind one switch; exact
  integer-cents conservation), deterministic server-side job board (xorshift32 — System.Random
  differs net48 vs net8), claim TTL + license gates + claim limits + strict task order, reconnect
  grace w/ exact restore, persistence v1 (LMPS store — deliberate hand-rolled deviation from 03
  §7's MessagePack sketch, FLAGGED for Cody; atomic write + rotation; cold restart resumes world
  incl. parked consists w/ position baselines; deadlines saved as remaining-ms). ServerTrains now
  keeps the last admitted snapshot per set (join-burst baseline + save). Session-test catch:
  JobState must re-broadcast on claimant leave AND rejoin or others' boards hold a dead peer id.
  **Tests 70 → 100** (registry, session e2e incl. rejoin-mid-job, persistence incl. cold-restart
  e2e, 2,000-op × 2-preset conservation fuzz with mid-flight save/restore). Stable ×3, full sln
  0 warnings. Next: M3.2 (join queue/phases — may defer), M3.3 Shim career integration (needs
  game + the 02 item-4 job-generation recon).
- **2026-07-18** — **M2 EXIT PASSED (run №5) — MILESTONE 2 CLOSED.** Couple→merge (set 20)→split,
  derail report + rerail request, grants, ghost — all with log-line evidence, zero exceptions.
  5 in-game runs today flushed 8 real bugs (worth it — 3 were world-lifecycle classes M3 needs).
- **2026-07-18** — **M2.3 CODE-COMPLETE + staged (uncommitted).** Build gate (runtime version vs
  supported list), TopologyWalker + bot `--consist` ghost train (headless-proven over the real map),
  Shim TrainSync/TrackIndexMap/JunctionHook/GhostConsists wired into the session. 69/69, 0 warnings.
  Publicizer CS0229 event trap fixed (`IncludeCompilerGeneratedMembers=false`). Awaiting the M2 exit run.
- **2026-07-18** — **M2.2 CLOSED.** In-game extraction: 2,073 edges / 279.6 km / 563 junctions, all
  health counters zero, hashes banked for B100. Dump → `tests/data/`, exit test live + green, 61/61.
- **2026-07-18** — **M2.2 code half built + staged (uncommitted).** Reflection-nailed the B99.7 track
  API (`RailTrackRegistryBase.Instance` Ordered arrays + hashes; `Branch {track, first}`;
  `junctionData.junctionId`; `curve.length` in BezierCurves.dll). `TopologyExtractor` = union-find
  over Branch pointers, positional verification of every join (1 m), LMPW out via the mod-folder
  button. `RealWorldTopologyTests` = the M2.2 exit test (vacuous until a dump lands in tests/data/).
  New refs DV.Utils + BezierCurves ×4 spots. 61/61, 0 warnings, staged. Awaiting the in-game run.
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
