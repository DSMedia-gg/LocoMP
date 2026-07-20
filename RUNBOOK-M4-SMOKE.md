# LocoMP — M4 in-game smoke runbook

**Purpose:** verify the whole M4 milestone (items, world items, shops, comms-radio, manual service,
**+ M4.6 locked essentials**) in one consolidated game session — the batched-per-milestone cadence (D8,
2026-07-20). Every M4 slice is built + staged; this is the live proof before M4 closes. Part B tacks on
the other banked checks that should ride the same session (D15 licenses, the debt/polish pass).

> **2026-07-20 update — M4.6 added (protocol v9).** Three new things to prove in this pass, all
> game-free-verified (0 warnings, 164/164 ×3) but never run in-game: **(a)** locked "look-but-don't-touch"
> personal essentials → new run **A5**; **(b)** the host's own dropped item is now HIDDEN, not destroyed,
> when a remote carries it off (host-native fix) → folded into **A1**; **(c)** the comms-radio discovery
> scan is throttled off the per-frame path → a host-FPS WATCH folded into **A3**.

**How to use:** work top to bottom. Each run lists its **rig**, **steps** (with the exact bot command
and the log line to expect), a **PASS** line, and **WATCH** (failure signatures). Fill in the results
table at the bottom as you go. If anything fails, copy the offending log line into the results table —
don't fix in-game; we triage after.

> Convention in this doc: `[tag] …` in monospace = a log line you should see in the UMM console / DV
> `output_log.txt`. `$…` = a money amount. "your wallet" = the money display (native counter + panel
> line; they should read the same and converge within ~1 s of any change).

---

## 0. Setup (once, before any run)

**Prereqs**
- Derail Valley **B99.7** (runtime `99-build2702`), LocoMP mod enabled in UMM. The current payload is
  already staged to `…\Derail Valley\Mods\LocoMP\` (**M4.6 / protocol v9** build — verified 2026-07-20:
  the staged `LocoMP.Shim.dll` is SHA-256 byte-identical to the clean-rebuilt, test-green source). If in
  doubt, re-stage from `repo\dist\LocoMP\` or rebuild.
- **Rebuild the bot from the current tree** so it speaks protocol **v9** (matches the staged mod — a v8
  bot will be rejected at handshake):
  `dotnet build tools\LocoMP.Bot -c Release`. Binary: `tools\LocoMP.Bot\bin\Release\net8.0\LocoMP.Bot.exe`.
- A **career save** you don't mind poking (the mod blocks native saves during a joined session, and a
  host session restores its own money on Leave — but use a scratch save if you're cautious).

**The two rigs** (each run says which it needs)
- **Normal rig** — *you* host in-game, the *bot* joins. The bot is the "other player" over the wire.
  Used wherever a remote player buys / grabs / initiates.
- **Listen rig** — the *bot* hosts (`--listen`), *you* join from the game (the game is the client).
  Used for the client-side paths (world handover, session-loss).

**Getting your coordinates (`--at`):** when you host, the log prints
`[session] your absolute position: --at X,Y,Z  ← paste into LocoMP.Bot`. Copy that whole `X,Y,Z` into
the bot's `--at`. (The bot has no world sense; `--at` is where it anchors / drops.)

**Bot basics**
- Defaults: connects to `127.0.0.1:8877`, build `99-build2702`, name `Bot`. On one PC you normally pass
  no `--host`/`--port`.
- Run it from a **separate terminal** while the game is running. Ctrl+C stops it (used deliberately in
  a session-loss test).
- `--help` lists every flag.

**Sanity check (do this first — proves the rig):**
1. Host in-game on your save. Expect `[session] hosting on UDP 8877 …` and the `--at` line.
2. New terminal: `LocoMP.Bot.exe --at <your coords>`
3. **PASS:** a bot avatar appears and orbits you; host log shows a peer joining. If not, nothing below
   will work — check the port/firewall and that the bot was rebuilt.

---

## Part A — M4 milestone smoke pass

### A1 · M4.2 — world-item loop  *(rig: normal)*

The bot is the "other player": it picks up / drops over the wire, so the loop is provable on one PC.

1. Host on your save. At load expect `host item capture installed — offered N world item(s) to the
   session` (N = items already lying in your world; 0 is fine). Panel shows an `Items —` line once any
   exist.
2. **Drop a handheld item** (lantern, boombox, any grabbable) on the ground. The host is the source, so
   there's no despawn — it registers silently and the panel `Items — 1 in the world` ticks up.
   (If you see `register: only the world source…`, the `AcceptExternalItems` wiring regressed — flag it.)
3. `LocoMP.Bot.exe --grab-items --at <your coords> --drop-after 15`
   Expect: `picking up world item <id>…` → `picked up item <id> (<prefab>)`; **the item VANISHES from
   your world** (a remote is "holding" it); panel `0 in the world, 1 carried`. After 15 s: `dropping
   item <id>…` → **the item REAPPEARS** near `--at` (`… materialized`); panel `1 in the world, 0 carried`.
4. Pick the reappeared item up **yourself** (walk over, grab). Host log `world item <id> … left the
   world locally — despawning from the session`; panel `0 in the world`. It's now yours in your SP save.
5. Leave → your own native items are UNTOUCHED (no reload needed on the host). Re-host → the sweep
   re-offers whatever's still lying around.

> **M4.6 mechanism note (host-native hide-not-destroy).** The item you dropped in step 2 is the host's
> REAL native item. When the bot carries it off (step 3), the fix now **hides** it (`SetActive(false)`,
> log `[items] native item N hidden (a remote carried it off)`) instead of destroying it; on the bot's
> drop it's **re-shown as the SAME object** at the new pose (log `native item N shown again (a remote
> dropped it back)`), NOT a fresh replica. On Leave, any still-hidden native is reactivated so your world
> is whole. Visually identical to before (vanish → reappear) — the point is the mechanism no longer
> fights DV's item lifecycle.

**PASS:** the drop → bot-grab (vanish) → bot-drop (reappear) → self-grab round trip completes with zero
LocoMP exceptions, and the log shows the **hidden → shown again** pair (not a destroy + respawn).
**WATCH:** items that DON'T vanish when the bot grabs (capture/despawn gap); items that reappear FROZEN
or get stream-disabled when you walk away (BelongsToPlayer keep-alive failing); a dropped item
teleporting "home" instead of staying put (RespawnOnDrop fighting placement). **New for M4.6:** any
`Cannot set parent while being destroyed` in the log (the exact bug the hide fix removes — should NEVER
appear now); a **DUPLICATE** item after the bot drops it back (ReShowNative missed → a replica spawned
alongside the re-shown native); an item stuck **invisible** after the bot grabs it and never coming back
even after you Leave (hidden native not restored — check the Dispose reactivation).

### A2 · M4.3 — shops  *(rig: normal)*

The M4 exit demo half: a *client* buys, the cash lands in the *client's* wallet.

1. Host. At load expect `[shop] catalog: N item(s) for sale from M shop(s)`. Open the panel → a
   **Shop (N)** toggle appears; expand → item list with prices (in a non-Career session most are $0).
2. `LocoMP.Bot.exe --buy <PrefabName> --at <your coords> --drop-after 15`
   (`<PrefabName>` from the panel Shop list — a cheap item, e.g. a lantern.)
   Expect: `buying <Prefab> from the shop…` → `bought item <id> (<Prefab>) — wallet now $X (the host's
   wallet is untouched)`. **THE proof:** the bot's wallet drops by the price; **YOUR wallet does NOT
   move** (per-player scope — the incumbent's gap closed).
3. After 15 s the bot drops it → the item **materializes in your world** near `--at`; grab it natively.
   Client buys → drops → host receives = the M4 exit demo on one PC.
4. **Refusals:** `--buy NotARealPrefab` → `purchase: … is not for sale`, nothing minted, no wallet move.
   Buy something pricier than the bot can afford → `purchase: insufficient funds`, nothing minted.
5. **Panel self-serve (no bot):** with a rich-enough wallet, expand Shop → **Buy** an item → your wallet
   drops, `Items — … carried` ticks up, a **Drop here** row appears → click → it spawns at your feet.
6. **Persistence:** `--buy <Prefab> --drop-after 99999` (bot holds it), Leave + re-host → the bot's
   possession resumes from the `.lmps` save. Or drop it first → the WORLD item resumes at its pose.
   Either way the purchased item survives a restart.

**PASS:** step 2's wallet-scope isolation holds, the bought item flows to the host on drop, refusals
mint nothing, and a purchase survives a re-host.
**WATCH:** wallet drift between the two money displays that doesn't converge in ~1 s; a bought item that
never materializes on drop; the catalog showing 0 items (shop controller wasn't up at host time —
re-host once the world's fully loaded).

### A3 · M4.4 — comms radio

**Run A — host fees**  *(rig: normal — just you + your comms radio)*
At load expect `[comms] comms-radio hook installed`, then once the world's up `[comms] host comms-radio
fee capture installed`. Note your wallet.

> **M4.6 perf WATCH (folded in here).** The host-side radio discovery used to run three
> `FindObjectOfType` scans EVERY frame until it hooked; it's now throttled to once/second and anchored on
> the always-active `CommsRadioController`. So: (1) the `fee capture installed` line should appear within
> ~1 s of the radio being available (not instantly, not never); (2) **watch host frame-rate while the
> comms radio is out and while you switch modes** — there should be NO stutter/FPS dip tied to holding or
> cycling the radio. A hitch on mode-switch, or the hook never installing, is the regression to report.
1. **Rerail:** derail/find a car, comms-radio **Rerail** it → `[comms] rerail <car>: $… charged to your
   wallet`; the fee leaves AND STAYS gone (before M4.4 it refunded within a second). Free cases stay
   free: a HandCar, or restricted/newbie mode.
2. **Clear:** comms-radio **Clear** a non-player car → wallet drops by the delete price (0 for a
   player-spawned car); `[comms] car N deleted — removing it from the session`.
3. **Summon:** summon a work train from a garage → wallet drops by the summon price; a non-garage
   vehicle is free.

**PASS:** each fee sticks; the two money displays converge within ~1 s; zero exceptions.

**Run B — delete removes the replica**  *(rig: normal + bot joined, or listen rig)*
With the bot joined and a consist synced, delete one of the host's cars → on the OTHER peer the replica
**vanishes** (previously it lingered as a ghost). Deleting a lone car removes the whole set; deleting one
car of a consist leaves the rest.
**PASS:** no ghost replica survives a delete.

**Run C — remote action**  *(rig: normal — you host, the bot initiates)*
1. Host. Get a car's plate from the log / a synced consist (e.g. `L-014`).
2. `LocoMP.Bot.exe --rerail L-014 --at <coords>` (derail L-014 first, e.g. with `--derail-car`).
   Expect: bot `asking the host to rerail car N (L-014) …`; **host** `[comms] rerailed car N for player
   <bot>`; the car returns to the rails near `--at`; **the BOT's wallet drops by the fee, YOURS does
   not** (the initiator pays — the "for all players" invariant).
3. `LocoMP.Bot.exe --clear L-014` → host `[comms] deleted car N for player <bot> — removed from the
   session`; the car vanishes for everyone; the bot's wallet drops (0 if player-spawned).
4. **Affordability:** run the bot with a near-empty wallet on a pricey action → the fee is refused
   server-side and logged; note whether the action still happened (known banked gap — the bot has no
   native affordability gate; a real client's DOES).

**PASS:** the initiator pays, the host's own wallet never moves for a remote action, no double-charge,
zero exceptions.

### A4 · M4.5 — manual service  *(rig: host-only; no bot)*

Mostly a VERIFY pass — the metered fee already rides D14, so this confirms it rather than a new feature.
At load expect `[service] manual-service guard installed (…)`.

**Run A — metered service bills the wallet (the D14 confirmation):**
1. Drive a loco needing fuel/repair into a **manual-service bay**. Note your wallet.
2. Service normally: connect the hose / turn the valve, deposit cash, hit the **Buy** button. The loco
   refuels/repairs.
3. **PASS:** your wallet drops by the service cost and STAYS down after ~1 s (the reconcile does NOT
   refund it). There's **no new log line** for this path — it's the existing D14 wallet hook; the proof
   is simply that the money stays spent, with zero exceptions.
4. **License:** if you don't hold **Manual Service**, buy it at a career manager → it mirrors like any
   general license (join a bot afterward with auto-grant on — see B1 — and it inherits it).

**Run B — the guard (best-effort; only if you can trigger it):** `RefillAll()`/`RepairAll()` have no
in-game callers, so there's usually nothing to press. If you have a way to invoke them (a scene button
/ free-service mode), expect `[service] free refuel at the bay — billed $X to your wallet` (or `… no
priced deficit to bill` if the bay had no live prices) and your wallet to drop — the service still
applies, it's just no longer free.
**PASS (expected):** "no bypass path fired" in normal play is itself the pass — note it and move on.

### A5 · M4.6 — locked personal essentials  *(rig: normal + bot)*

The v9 feature: a DV **personal essential** (Map, comms radio, wallet, compass, DV guide) set down in the
world is **look-but-don't-touch** — everyone SEES it, but only its owner can pick it up. Job paperwork is
deliberately EXCLUDED (it's shared crew state — anyone can grab a booklet). The bot is the "other player"
who must be REFUSED.

> **Read first — the likely snag.** Some DV essentials auto-return to your inventory the instant you drop
> them (`RespawnOnDrop`). If the item you try never actually rests on the ground, it never becomes a world
> item and there's nothing to sync — that's not a failure of this feature, it's DV's drop behaviour. **Part
> of this run is discovering WHICH essentials stay set-down** (try the comms radio and the map first; note
> what stays vs snaps back). If none stay put on your save, mark A5 `n/a — no essential rests in world` and
> we'll find another trigger (e.g. placing it on a surface) next session.

1. Host on your save; join the bot (`LocoMP.Bot.exe --grab-items --at <your coords> --drop-after 15`).
2. **Set down a personal essential** (comms radio / map) so it rests in the world. Watch the host log:
   the item registers and the panel `Items —` line ticks up. On the joiner side (bot) the item is flagged
   locked — you'll see it materialize with `world item N (<Prefab>) materialized (locked — owner only)`
   in a joined GAME client's log (the bot logs the refusal instead, next step).
3. **The proof — the bot must NOT be able to take it.** With `--grab-items` running, the bot tries to pick
   up every world item it sees, including the essential. Expect the server to **refuse**: bot log shows a
   rejection carrying `item N is a personal item — only its owner can take it`; the essential **stays put**
   (panel item count doesn't drop; it never shows as "carried"). A free item dropped nearby (lantern) is
   still grabbed normally — so you can see the bot IS working, just blocked on the essential.
4. **Owner still reclaims it natively.** Walk over and pick the essential up **yourself** — that works
   (the lock only blocks over-the-wire pickups; the owner grabbing natively is the intended reclaim path).
   Panel count drops; it's back in your inventory.
5. **Job paper is NOT locked (the exclusion).** Take a job so you hold a **booklet**, then drop it. The bot
   SHOULD be able to pick it up (no `personal item` refusal) — job paperwork syncs as a normal shareable
   item. (If booklets don't rest in world either, note it and skip — same RespawnOnDrop caveat.)
6. **Persistence (optional):** with a locked essential resting in the world, Leave + re-host → it resumes
   from the `.lmps` save STILL locked (the v5 save byte). Cheap to check if step 2 produced a stable item.

**PASS:** the essential is visible to the bot but pickup is **refused** with the `personal item` message
and it never moves; a normal item / job booklet nearby is still grabbable; you can reclaim the essential
natively; zero LocoMP exceptions.
**WATCH:** the bot successfully CARRYING an essential (lock not enforced — the load-bearing failure); a
booklet being REFUSED (the job-item exclusion misfiring → crews can't share paperwork); the essential not
registering at all (RespawnOnDrop — see the snag note, likely `n/a` not a fail); a locked item losing its
lock across a re-host (v5 save byte not round-tripping).

---

## Part B — also-pending banked checks (ride the same session)

### B1 · D15 — guest license progression (auto-grant)  *(rig: normal + bot)*
1. While hosting, the panel host section shows an **"Auto-grant my licenses to joining players"**
   checkbox (visible idle and live). The host grant list offers only licenses you **hold** (not the
   whole catalog).
2. With auto-grant **ON**, join the bot → the bot receives your license set in the join burst (client
   career log shows the licenses).
3. Mid-session, **buy a license natively** at a career manager → it propagates live to the joined bot.

**PASS:** newcomers inherit the host's held licenses; a live native purchase propagates.

### B2 · Debt / polish pass  *(rig: listen — bot hosts, you join — for 1–3; normal for 4)*
1. **Session-loss UX:** join the bot host (`LocoMP.Bot.exe --listen`, then host-join from the game),
   then **Ctrl+C the bot** → within ~15–20 s the panel flips to **"SESSION LOST — Leave to restore your
   world, then reload your save"**; saving stays blocked; Leave → reload → your SP save is intact.
2. **Chain requests:** unhook a chain between two bot cars → the split actually HAPPENS (host-side
   `remote uncouple request honored`); re-hook → merge.
3. **`--derail-car`:** re-run the bot with `--derail-car 2` → `spawning with DERAILED car(s)` log; a
   car sits off-rail near you (a ghost-box fallback with a log line is also a pass — the path is guarded
   either way).
4. Normal host-rig regression rides along (host + bot-joins, trains/jobs behave as in the M3 runs).

**PASS:** dead session announces itself + never leaks to the SP save; chain requests execute; the
derail path fires.

---

## Results

| Run | Result | Notes / offending log line |
|---|---|---|
| Sanity (bot orbits) | ☐ pass ☐ fail | |
| A1 · M4.2 world items | ☐ pass ☐ fail | |
| A2 · M4.3 shops | ☐ pass ☐ fail | |
| A3 · M4.4 Run A (host fees) | ☐ pass ☐ fail | |
| A3 · M4.4 Run B (delete removal) | ☐ pass ☐ fail | |
| A3 · M4.4 Run C (remote action) | ☐ pass ☐ fail | |
| A4 · M4.5 Run A (metered fee) | ☐ pass ☐ fail | |
| A4 · M4.5 Run B (guard) | ☐ pass ☐ n/a | |
| A5 · M4.6 locked essential refused | ☐ pass ☐ fail ☐ n/a | which essentials rest in world? |
| A5 · M4.6 booklet NOT locked | ☐ pass ☐ fail ☐ n/a | |
| A1 · M4.6 hide-native (no destroy err) | ☐ pass ☐ fail | |
| A3 · M4.6 comms FPS (no per-frame scan) | ☐ pass ☐ fail | |
| B1 · D15 auto-grant | ☐ pass ☐ fail | |
| B2 · debt/polish | ☐ pass ☐ fail | |

**All A-runs green → M4 milestone closes** (one-PC wording; the friend-session deferrals below upgrade
it to official).

**Deferred to a friend session (NOT testable on one PC — don't count against M4):** a real joined GAME
client grabbing a replica / buying + physically holding an item / using its own comms radio; remote
summon; the host's real shelf stock decrementing; VR interaction.

**If anything fails:** paste the log line into the table and stop poking that path — report back and
we'll triage + fix same-session (the M3 run cadence: find, root-cause, fix, restage).
