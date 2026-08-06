# Changelog

All notable changes to LocoMP are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> Release CI **fails** if the version being released has no section here. Every release version needs
> its own `## [x.y.z]` heading (05 §2). Keep an `## [Unreleased]` section at the top between releases.

## [Unreleased]

### Added
- **Session health counters for the host (M5.2 diagnostics backend).** The server can now report a
  one-glance snapshot of a running session — players online, joiners waiting, trainsets, jobs and items
  tracked, admins and bans, whether joins are paused, and the internal health gauges (economy/item
  accounting still balanced, stale-update count). It's the data behind M5.2's Diagnostics panel; live
  bandwidth and per-player latency come in a later slice. (No protocol change.)
- **Host moderation is now enforced by the server (M5.2 backend).** A session now has an owner (the
  host — or the first player to join a dedicated server) who can kick a player, session-ban them (the
  ban follows their reconnects but is lifted when the session ends), pause and resume new joins, and
  promote another player to admin so they can moderate too. The owner can never be kicked, banned or
  demoted, so a host can't lock itself out. Every action is authorised on the server — a non-admin's
  request is refused — and a kicked or banned player is told why. The in-game menu that drives all of
  this arrives with M5.2's UI; this release lands the machinery it sits on. (Network protocol bumped —
  both sides must run this build.)
- **A full server now holds your place in line instead of turning you away.** Joining a server at
  its player cap used to bounce you with an instant "server full". Now you wait in a real admission
  queue: the loading cover shows your live position ("waiting for a free slot — position 2 of 3"),
  the line moves in arrival order as slots free, and your eventual admission is exactly a normal
  join. Losing your connection while waiting — or reconnecting with a hiccup — keeps your spot
  rather than sending you to the back. The instant refusal only remains when the waiting line
  itself is full. (Network protocol bumped — both sides must run this build.)
- **First-party UI foundation (M5.0).** A MULTIPLAYER button now appears in the game's own main
  menu, and a LocoMP entry in the pause menu — both cloned live from Derail Valley's own buttons,
  so they look, animate and (in VR) point-and-click exactly like the game's, while LocoMP ships
  none of the game's assets. They open LocoMP's new screen framework: a root screen with the
  Direct Join · Friends · Host · Settings tabs (tab content arrives slice by slice — M5.1 brings
  the real join/host forms; Friends unlocks with the Steam transport). Opening the in-game screen
  never pauses the world: in multiplayer the session keeps running behind any menu, always.
  Under the hood this adds the presentation seam the whole M5 milestone builds on — a view-model
  the screens bind to (the session backend now pushes state changes instead of being polled), a
  themed widget kit, and a reusable "readiness gate": a blocking cover for the moments the world
  is deliberately not yet interactable (joining, reconnecting, session teardown) that only ever
  clears on the REAL completion signal — if something stalls, it says so and offers to keep
  waiting or back out, never a silent hang and never a timer pretending the work finished.
  The familiar dev panel (Ctrl+F10) is untouched and remains the primary surface for this build;
  set `LOCOMP_DEBUG=0` to hide it once the real screens land.
- **Real join and host screens (M5.1).** The Direct Join and Host tabs now carry actual forms
  instead of placeholders: name, address and port with a remembered recent-server list (your last
  five, one click to refill — names and addresses persist between game runs; passwords never do),
  and the full set of hosting options — port, password (typed masked, in both forms), max players,
  autosave cadence, the career preset, license auto-grant, fresh-career and bandwidth-saving
  toggles. Both screens drive exactly the same code path as the dev panel, so nothing can behave
  differently between the two. While a join is in flight you now get a real loading cover with
  live stages — connecting → world → career → items — driven by the server actually saying "your
  join data is fully sent" (a new end-of-burst signal on the wire), never by a timer guessing; if
  it stalls, the cover names the stage it stalled at and offers keep-waiting or back-out. And when
  a join is refused because the two sides don't match, the screen now shows exactly what you have
  and what the server needs (game build, LocoMP version, protocol, mod set) with advice per case —
  the server has always known; now it tells you instead of a one-line error. Typing in any LocoMP
  field no longer triggers game hotkeys. (Network protocol bumped — both sides must run this build,
  and older/newer mixes still refuse with a readable reason in both directions.)

- **Abandoned trains can finally be claimed, cleaned up, and recovered.** When a player
  disconnects, their trains stay parked where they stood — but until now nobody could do anything
  with them: no verb worked, and a consist split near your yard throat was stranded forever.
  Three verbs now exist. CLAIM: step into the cab of a parked train and it becomes yours — you
  simulate it, drive it, couple it, exactly as if you had spawned it. DELETE: the comms radio's
  Clear mode works on parked cars now (the host's radio too — it used to refuse on any car another
  player had brought); the server retires the car for everyone. RERAIL: pointing the radio's
  rerail mode at a parked derailed car claims the wreck for you and your game performs the real
  rerail — recover it, then drive it or walk away and it parks again when you leave. Parked-car
  deletes and non-host rerails are free for now; proper billing arrives with the server-side
  price table. (No protocol change — older clients coexist, they just lack the new verbs.)

### Changed
- **Shared careers now release a departing player's carried host items.** When a guest carrying
  one of the host's real items disconnected from a shared-career session, the item used to stay
  locked to the (communal) crew inventory forever, leaving the host's hidden original in limbo.
  It now drops where the carrier was last seen, immediately — same rule per-player careers have
  always had. Purchases and other crew-owned items are untouched: they still belong to the whole
  crew and never dump on anyone's departure.
- **Bots can keep a stable identity across reconnects** (`--player-key`): required for testing
  the reconnect-grace flows; with `--count`, each bot gets a distinct derived key.
- **Bot consists now sit buffer-to-buffer like real trains** (`--car-geometry`). When a host
  renders a bot's consist as real cars, the host log prints a measured spacing hint — each car's
  real coupler pitch plus where its bogies actually sit — and pasting that line onto the bot
  makes every livery line up with vanilla-perfect gaps at both ends of every car. (The game
  places a spawned car by its bogie positions, so guessed bogie offsets shifted car bodies even
  when the overall spacing was right. The older pitch-only `--car-lengths` hint still works.)
  Coupled cars in a bot consist are now streamed at their COMPRESSED-under-tension coupled spacing
  (the game's tight-coupled rest with the buffers touching, re-derived from the coupler joint —
  meaningfully tighter than both the fresh-spawn gap and the earlier estimate) rather than the wider
  spawn gap, so the chain visual hooks flush on sight instead of rendering gapped and unlinked until
  the consist is claimed. A new `--coupled-gap <m>` flag overrides that spacing for the whole
  consist, so the buffer-to-buffer flush can be dialled in live against a running game without a
  rebuild. (Real player hosts already stream their physically-coupled bogie positions, so this only
  ever affected synthesised bot consists.)
- **Remote trains ride smoother at speed.** Between position updates, a remote consist's smoothing
  target is now dead-reckoned FORWARD along the rail from the last update's speed (bounded so a
  stalled stream never runs a train away, and clamped to the track so it can't drift off a curve),
  instead of always chasing the last-known — and already slightly stale — position. A constant-speed
  train's visible lag collapses toward zero, with no protocol change.

### Fixed
- **Hosting no longer bloats your save with other players' trains.** While you host, the game spawns
  every joined player's (and every test bot's) consist as real cars in your world — and an autosave
  during the session used to write those foreign trains into your own single-player save, so they came
  back as permanent cars in your world and the save grew with every session (a handful of extra
  trainsets and cars per host). Replicas of trains that other players are simulating are now excluded
  from your save; your own trains save exactly as before. (The trains stay fully visible and physical
  during play — they're only omitted from the save file itself.)
- **Keep driving a train whose owner disconnects.** If you were driving another player's consist on
  a granted cab and its owner left the session, your controls used to go dead and you had to leave
  and re-enter the cab to take it back. The set now transfers to you as its owner the moment the
  previous owner leaves, so your drive continues uninterrupted. (You could always re-claim it by
  re-entering; this just removes the interruption. If you can't actually simulate the cars at that
  moment, it falls back to parking the set exactly as before.)
- **A train that never moved no longer lingers as an un-removable ghost.** A consist that was
  registered but never reported a single position — then lost its owner — used to sit forever as a
  set nobody could see, adopt, or clear, with hosts quietly re-requesting it on a loop. Since it
  never physically existed anywhere, it is now retired when its owner leaves; the money and item
  books are unaffected (there was never a car to account for).
- **A parked train nearby no longer spams the log with endless "re-syncing" messages.** When a train
  is left parked with no one driving it (its owner drove off or disconnected), your game periodically
  re-checks it so it appears the moment you walk up to it. That check used to re-announce itself and
  re-pull the train's full data every ten seconds forever, filling the log. Now, once your game has
  the train's position, the re-check is quiet and much less frequent — you still see the train appear
  as you approach, without the noise. (Only a train your game has never received any data for still
  asks for it promptly, as before.)
- **Join churn can no longer grow a server's memory and save file forever.** Every player key the
  server ever saw used to keep a career profile and wallet for good — deliberate for real players
  (careers persist across disconnects), but join spam or ordinary public-server traffic with
  throwaway identities accumulated dead profiles without bound (a 24-hour churn soak grew the
  save to 3.6 MB of ghosts). Now, when a departed player's reconnect grace runs out and their
  career is completely untouched — starting money to the cent, starting licenses, no jobs, no
  items — the profile is evicted and its starting grant is burned back out of the economy, so the
  money books still balance exactly. Anyone who ever earned, spent, or held anything keeps their
  career forever, exactly as before; a returning evicted player just gets the identical fresh
  start they would have found anyway. Old bloated saves drain automatically the first time they
  are loaded.
- **Chain couplers on remote consists can no longer wedge half-dead.** The once-a-second truth
  sweep now heals the chain VISUALS as well as the couplings: a chain whose animation state
  contradicts its coupler re-runs the game's own state restore, and two deeper wedges are now
  caught too — a "coupled" chain whose hook AND tensioner had both gone dead (it looked attached
  while refusing all interaction, and could lie about being coupled after a split), and pairs
  where both sides (or neither side) claimed to own the chain. Healed and programmatically
  coupled pairs restore SCREWED TIGHT, matching what the game does for its own committed
  couplings — a loose restore let a single grab uncouple a train that should have needed the
  screw loosened first. Deliberate loosening by a player is left alone.
- **Joining a session no longer trips the game's loco-restoration system.** Entering another
  player's session clears your own world's trains (your save is untouched and returns when you
  reload it). If one of those trains was a restoration-project loco, the game's restoration
  tracker fought the clear: an error mid-delete, then a "last resort" respawn that dropped its
  loco back into the session's world. Restoration tracking is now put to sleep for the session
  before the clear — your restoration progress is unaffected and everything returns with your
  save.
- **A restarted bot can no longer poison the host's world save.** Bot consists used a fixed
  identity, so a bot re-run after an unclean exit could collide with cars an earlier run left in
  the host's save — real-car spawning then failed permanently ("same key already added") and the
  consist fell back to ghost boxes forever. Bot identities are now unique per launch.
- **A failed connection attempt can no longer crash the client.** If the server vanished (or was
  unreachable) at exactly the wrong moment during a connect attempt, the network library could
  report the failure without a connection object attached — and LocoMP's handler crashed on it,
  taking the whole client down (found when the 24 h soak's entire bot swarm died this way at
  teardown). A failed attempt that was never admitted is now simply ignored; the normal retry and
  timeout paths carry on.
- **Reconnecting quickly after a crash no longer locks you out.** If your game died and you rejoined
  before the server noticed the dead connection, you were refused with "player key already in
  session" until a timeout that could take minutes. Your player key is your credential: presenting
  it (with the server password) now evicts the dead connection on the spot and drops you straight
  back into your own career — the slot is handed over, never given to someone waiting in the queue.
- **Consists nobody owns can be coupled and uncoupled again.** A split half whose driver left (or
  whose owning bot/peer disconnected) was permanently un-recouplable — every couple attempt was
  refused because there was no simulation owner to carry it out. Chain requests on such "parked"
  consists are now handled by the server itself: it commits the coupling (or uncoupling) directly,
  every player sees the chain move, and the result stays parked until someone claims it. Where one
  side of the coupling DOES have a live owner, the request now routes to that owner instead of
  being refused outright.
- **Unhooking another player's train now really unhooks it.** Grabbing a chain on a
  remotely-driven car used to leave the visuals lying: the game's chain animation ran as if the
  act had happened, but the real uncouple was still travelling to the train's owner — so the
  chain snapped back onto a hook it had logically left, and both cars' couplers went dead to
  further interaction. Chain gestures on remote cars now take effect locally the moment you make
  them (harmless — those cars are position-driven replicas) while the owner is asked in
  parallel; if the owner's side refuses, the coupling visibly snaps back within a few seconds
  instead of silently disagreeing. The once-a-second coupler truth sweep also logs every
  correction it makes now, so a future session can see it working (or failing) in the log.
- **Two paint cans no longer flood the session with phantom items.** Some items re-seat
  themselves the moment they touch a valid surface, with no player input — and every re-seat
  used to register a brand-new shared item and despawn the old one (two stationary objects
  produced ~40 identities in minutes, drowning the log and growing a dedicated server's item
  store without bound). Item registration now waits for an item to actually settle, and a
  momentary bounce keeps its existing identity with zero network traffic. Really picking an item
  up is recognised and still takes effect immediately.
- **Distant remote trains no longer crash the game's own train optimizer.** When a remote
  consist rolled out of range and its local stand-in was removed, Derail Valley's physics-sleep
  sweep could still reach the dying cars a frame later and throw. Remote stand-ins (and cars
  mid-teardown) are now exempt from that sweep entirely — they are position-driven and have no
  local physics for it to manage. This also stops the optimizer quietly re-enabling gravity on
  them while they exist.
- **A version mismatch now says so instead of hanging.** Connecting to a server on a different
  network protocol used to die at the socket — ten silent seconds, then "timed out", with the
  mismatch screen never getting a chance to speak. The socket-level key no longer embeds the
  protocol version, so mismatched builds actually connect far enough for the server to answer
  with exactly what you have vs. what it needs (one caveat, one time: builds older than this one
  still use the versioned key and will still time out against it).
- **World extraction now survives the game's unmappable tracks.** Extracting world geometry for
  the bandwidth-saving "only stream nearby trains" option always came up short on this game
  build — 139 special tracks (turntables and friends) never expose their positions, and the old
  all-or-nothing rule threw ALL geometry away because of them, so the option silently never
  worked. Geometry is now carried per track: the ~93% of the network that can be mapped filters
  normally, trains on the few bare tracks are simply streamed to everyone, the extractor names
  each track it could not map, and both host and server report an honest "X of Y placeable"
  instead of pretending nothing was extracted. Existing extracted files keep loading.

- **A save that cannot be written no longer crashes the server (or the host).** Pointing the
  dedicated server's `--save` at an unwritable location used to kill the process with an unhandled
  error during shutdown — losing the world state it was trying to save, and any autosave failure
  mid-run had the same crash waiting. A failed save is now a logged warning ("world changes since
  the last good save are not on disk"), the disk is retried on the normal autosave cadence, the
  shutdown message tells the truth instead of claiming success, and the server exits with a
  distinct code (1) so scripts notice. The host's "career saved" log line is likewise only printed
  when the save actually reached disk.
- **Couplers on other players' trains now correct themselves instead of staying wrong forever.**
  When a train you could see was split up, the leftover half could keep a coupler that still believed
  it was attached — the car would fight an invisible joint, render its coupler hanging straight down,
  and refuse to ever couple again. The one repair pass that existed ran only at the moment of the
  split, skipped exactly the broken case (a coupler holding something that isn't part of that train),
  and never re-checked. Remote trains now re-assert the truth about their couplers every second, in
  both directions: anything attached that shouldn't be is detached, anything detached that should be
  attached is re-coupled. The same sweep also notices a train the server told us about that never
  received a position — or a parked train you're walking toward that nobody is streaming — and asks
  the server to re-send it, so it appears where it should instead of never appearing at all.
- **Items no longer vanish for the session when the player carrying them disconnects.** If a friend
  picked up one of the host's items and then crashed or lost connection, the item was simply gone —
  the host's real object stayed hidden with no way to get it back short of ending the session. An
  item now remembers where it came from, and that decides what happens when its carrier leaves: an
  item that belongs to the host's world drops back into the world **immediately**, where the carrier
  was last seen (or where it was originally picked up, if the server never saw them move); a
  **bought** item stays theirs through the usual reconnect window — rejoin in time and it is still in
  your hands — and only drops to the ground once that window closes and they are considered gone for
  good. The same rule now covers a server restart, which counts as everyone disconnecting at once:
  host items return to the world right away, purchases wait out a fresh reconnect window. In a
  shared-career session nothing is released at all — the crew's pooled inventory outlives any single
  player, as it always did. (Save format and network protocol both updated; old saves are refused
  cleanly and a fresh start is offered, as usual for pre-release saves.)
- **Cars split off from a train no longer go missing for anyone who joins later.** When you uncoupled a
  train and drove away with one half, the half you left behind stopped being sent to anybody — so a friend
  joining afterwards simply never saw those cars, for the rest of the session. The abandoned half now keeps
  the position its cars already had, so it shows up where you left it. The same fix means asking the game to
  re-send a train you have lost track of now actually restores it (it used to send the train's description
  without its position, which was not enough to put it back in the world), and a train left parked keeps its
  position across a server restart even if nobody was driving it.
- **The dedicated server's overnight health check now reports the truth.** Its memory-leak watch compared
  a snapshot taken on an *empty* server against later readings taken under load, so a perfectly healthy
  run finished by reporting failure — useless for the one job it has, which is telling you unattended
  whether something went wrong. It now measures what memory is actually still being held onto (after a
  full clean-up, with object finalizers given the chance to run — otherwise objects merely *waiting* to be
  cleaned up read as a leak), which is a steady, quiet number instead of a jagged one, so the threshold
  could be tightened from 4× to 2×. Verified on a 6.5-minute run with 8 clients and 144 joins: memory sat
  flat at 2.5 MB and the run correctly passed. Use `--soak-report 30` or higher — the check briefly pauses
  the server each time it runs. The garbage collector's mode is now pinned so a dev machine and a
  container behave the same way; re-check the numbers in the container before relying on them there. Two separate faults, both found by playing:
  leaving a session could quietly turn the session's spending money into your *real* career balance (a
  $10,000 career came back holding $2,000, permanently, at the next autosave); and if you left while a cash
  register still held money you'd put in, that money was handed back **on top** of your restored balance,
  minting money out of nothing every time you did it. Your real balance is now put back before anything can
  save over it, and if a machine is still holding your cash the mod waits and tells you so, restoring the
  moment you take your wallet back out.
- **A crash no longer follows you out of a session.** Quitting to the menu while hosting could throw an
  error during teardown, because a routine "does this exist yet?" check on one of the game's systems would
  *create* the thing it was checking for, on a world that was already gone.
- **Comms-radio fees now name the car they charged for.** Rerail and clear fees were logged as `rerail ?` /
  `clear ?`, which made a charge impossible to match up with what was charged; they now carry the car's
  plate (e.g. `clear CFF059`).
- The mod now prints a per-build identifier when it loads, so you can confirm the build you installed is the
  one actually running. Worth knowing if you use Vortex: it re-deploys the mods folder from its own copy, so
  a manually-copied build can be silently replaced.

### Added
- **"Export career (.lmpc)"** button beside the world extractor: reads your live game — real
  stations, route distances, license catalog and prices, plus which cargo actually leaves each yard
  for which destinations at what consist sizes, mined from every station's own job rules — and
  writes the career file the dedicated server loads with `--config`. A fresh server then generates
  jobs that route real cargo along the real map instead of the built-in placeholder board. Payouts
  use a simple, documented per-car + per-kilometre formula (not the game's exact economy — close
  enough for pacing, easy to tune later). The file is stamped with the preset currently selected in
  the host panel.

### Changed
- **Other players' trains track their true position noticeably closer.** The render smoothing
  constant, not the network rate, was three-quarters of a remote train's lag (~3 m at 100 km/h —
  felt hardest when coupling up to a friend's consist). Smoothing is now ~2.5× tighter, cutting
  typical lag from ~108 ms to ~58 ms with no protocol change. If this makes remote trains look
  jittery rather than laggy on your connection, say so — that observation decides whether the next
  step (predictive extrapolation) is worth building.

- Spatial interest management — **trains** (D10, Burst 2): the payoff. The server now streams a moving
  consist only to the players near it, instead of sending every train's position to everyone. Measured on
  a test session where 4 of 24 consists were nearby, this cut the train traffic reaching that player by
  **83%** — and trains are roughly 96% of everything a session sends, so this is the change that makes
  bigger sessions and slower connections viable. A train that goes out of range disappears from your
  world and is rebuilt, in the right place, when you approach again; a train you are driving is never
  affected, however far you roam. Turn it on with the new **"Only stream nearby trains/players"** host
  toggle, or `--interest` (plus `--interest-radius`, `--interest-players`) on the dedicated server —
  **off by default**, so nothing changes unless you opt in.
- World files (`.lmpw`) now record where each piece of track actually is, which is what lets the server
  judge whether a train is near you. **Existing world files still work** — they simply don't carry the
  new positions, and a server using one falls back to sending every train to everyone (as before) and
  says so at startup. Re-extract your world from inside the game to get the benefit.
- Container packaging for the dedicated server (M6-B): a game-free two-stage `docker/Dockerfile` +
  `docker/compose.yml` that build and run the headless server on the .NET runtime image — no Unity, no
  game install. Out of the box it's a bare server (presence + job board + persistence) on UDP 25701 with
  a periodic health line; mount a real world/career to add server-owned trains and a real board. The
  server now also shuts down gracefully on SIGTERM (`docker stop`), saving the world on the way out
  instead of being hard-killed. (Building the image and deploying it are manual, deliberate steps.)
- Soak / unattended-run tooling (M6-B): the dedicated server can now run a long, hands-off endurance test
  and watch its own health. `--soak-report <seconds>` prints a periodic line (players, trains, jobs, items,
  memory) that calls out the instant an internal accounting invariant breaks or memory runs away, and
  `--duration <seconds>` makes the server stop and save cleanly on its own after a set time — so you can
  point a swarm of bots at it overnight and tell from the exit code and final summary whether the world
  stayed sound. Backed by an accelerated in-process soak test (hundreds of join/leave + claim/drive/item
  waves in milliseconds) proving the money and item ledgers stay balanced, trains never leak or multiply
  under churn, and a fresh joiner still sees the whole world after all of it.
- Spatial interest management — the mechanism (D10, Burst 1): the server can now relay a player's
  movement only to the OTHER players near them, instead of broadcasting everyone's position to everyone.
  A player who walks out of range has their avatar hidden for you (and re-shown when you meet again),
  rather than lingering as a distant ghost — the groundwork for scaling past small co-op sessions
  (the perf baseline measured broadcast-everything at 6–42× over the bandwidth budget at 32 players).
  This first cut proves the whole "who can see whom" machinery on player movement; the big win — the same
  filtering applied to trains, which are ~96% of the traffic — comes in the follow-on. **Off by default**
  (a server opts in), so nothing changes for existing sessions. Network protocol is now v11 (a new
  "hide this" message). Measured: with filtering on, a client ~2 km from half the players receives half
  the movement traffic.
- Real careers on the dedicated server (M6-B): the headless server can now load a real Derail Valley
  career via `--config <file.lmpc>` — actual yards, cargo economy, license gates, route distances and
  station locations — instead of the built-in Alpha/Bravo placeholder. `--dump-config <file>` writes the
  built-in default to a file as a starting point, and a missing/corrupt/foreign config falls back to the
  default so the server always runs. (The tool that EXPORTS a `.lmpc` from a running game is a later
  mod-side slice; the file format, loader, and a seed writer are here now.)
- Drivable server trains (M6-B.3): the dedicated server's own trains are no longer just scenery — a
  player can now **claim one and drive it**, then hand it back (or just disconnect) and the server picks
  its route back up. Taking over a server train can't be blocked by, and can't steal, another player's
  train — only the server's ambient ones are up for grabs. The bot gains `--claim-server-train` to run
  the whole borrow → drive → release loop against a dedicated server on the one-PC rig, so you can watch
  an ambient train get taken over and driven from your own game. Network protocol is now v10 (a new
  "hand it back" message). (In-game, a real player claiming/driving a server train from within the game
  needs the mod-side UX, which comes with the Shim work; the wire path and the bot rig are here now.)
- Dedicated server (alpha, M6-B pulled forward): `LocoMP.Server` is now a real, standalone headless
  server you can host and join from the game — no second player needed to test. It runs the full session
  (presence, the job board, persistence) over UDP with no game install, generates a starter career board
  out-of-the-box, saves the world to disk (autosave + on exit), and reloads it on restart. Console
  commands `status` / `save` / `stop`. With `--spawn-trains N` it **drives its own trains** along the
  extracted world topology, so you can watch trains roll through the valley solo — no bot needed
  (`LocoMP.Server --port 8877 --spawn-trains 3`). See `src/LocoMP.Server/README.md`. (Alpha limits: the
  career board is a synthetic placeholder; a real Derail Valley career exported from the game comes next.)
- Personal items stay yours (M4.6): a personal essential you set down in the world — your map, comms
  radio, wallet, compass or the DV guide — is now "look, but don't touch". Everyone in the session can
  SEE it lying where you left it, but only you can pick it back up; another player's attempt is refused
  ("that's someone's personal item"). Job paperwork is deliberately exempt — a booklet is shared crew
  work, so anyone can still grab one. Under the hood, when another player carries off one of the host's
  real items it's now hidden and restored rather than destroyed and re-spawned, so it can't fight the
  game's own item lifecycle. Network protocol is now v9. Also fixes a host-side frame-rate hitch: the
  comms-radio hook no longer scans the whole scene every frame while you hold the radio.
- Comms-radio actions in multiplayer (M4): the rerail, clear (delete), and summon tools now work in a
  session and cost money through the shared economy. Rerailing a derailed car, deleting a car, or
  summoning a work train charges the player who did it — previously these were silently FREE in a
  session (the fee was being refunded by the money mirror). A deleted car now disappears for everyone
  (it used to linger as a ghost on other players' screens). And any player — not just the host — can
  rerail or delete one of the host's cars with their own comms radio: the host performs it and the
  fee comes out of the initiator's wallet. Joined players' on-screen money now shows their real
  session balance, so the radio's "can I afford this" check is correct. The bot gains `--rerail
  <plate>` and `--clear <plate>` to drive the remote-action path on the one-PC rig. Network protocol
  is now v8. (Remote *summon* — spawning a work train at a joined player's location — is deferred to a
  later slice; summoning from the host works.)
- Shops (M4): buy from the game's shops in multiplayer. A joining player picks an item from the
  session panel's Shop list (the catalog is read from the host's live world) and the price comes out
  of THEIR wallet, not the host's — then the item is theirs to carry and drop wherever they like,
  where anyone can pick it up. This closes the incumbent's headline gap for purchases: a *client*
  buys a lantern and the cash lands in the right wallet. The bot gains `--buy <item>` to run the
  whole buy-then-drop loop on the one-PC test rig. (This slice covers world-dropped purchases;
  showing a bought item in a player's hands, and live shop stock, come next.) Network protocol is
  now v7 (the join burst carries the shop catalog so a client can price its Buy buttons).
- Handheld items sync (M4.2): drop a lantern (or any world item) and everyone in the session sees
  it appear where you left it; another player can pick it up and it vanishes from the world for
  everyone, then reappears when they set it down again. The host's real items are mirrored onto the
  session automatically — no new keypresses, and items you leave lying around are offered when
  players join. (First slice: world-dropped items. Seeing what's in a player's hand, and buying
  from shops, come next.) The bot gains `--grab-items` to pick up and re-drop items on the one-PC
  test rig.
- Session-loss prompt: when the host disappears, a joined client's panel now says so plainly
  ("SESSION LOST — Leave to restore your world, then reload your save") instead of sitting on
  "connecting…" forever. Native saving stays blocked until you leave — a dead session still
  fails safe, it just tells you now. A link drop that recovers within a few seconds (e.g. a
  save-load freeze re-handshake) continues silently.
- Bot: honors remote couple/uncouple requests on its consists (split/merge commits through the
  normal transaction path, and the bot keeps driving the product containing its lead car), so the
  one-PC rig live-fires the owner-side half of chain interception. `--derail-car <n>` streams a
  consist car as derailed at the `--at` point — a joining client then exercises the off-rail
  (null-track) spawn path.

- Remote claim parity (M3.5c): players who JOIN a session can now claim the host world's real jobs
  from the board — the host takes the job natively on their behalf, "Report delivery" is verified
  by the host's own game (the native task tree is the validator, so nobody gets paid for an
  unfinished haul), and the payout lands in the claimant's policy-routed wallet. A released
  external claim (abandon, claim TTL, or reconnect-grace lapse) retires the job everywhere — the
  game cannot re-shelve a taken job, so the board never advertises one it can't deliver.
- Multi-crew cab controls (M3.5c): with a control grant, a remote player's lever moves in a
  replica cab drive the owner's real locomotive — every cab control in the game rides one uniform
  surface — and the owner's committed control state mirrors back onto everyone's replica levers
  (and into the join burst, so a newcomer's cab reads true). Physical chain couples/uncouples
  involving a remote-driven car are routed to the simulating player as requests and committed
  through the normal transaction path.
- Live cargo sync (M3.5c): loading or unloading a synced car announces the new load to everyone
  (and into late-join defs and saves); remote replicas mirror it onto their logic cars.
- Mid-session consist registration (M3.5c): trains that appear after hosting started — new job
  chains, crew vehicle summons — register automatically, and a consist DV's distance streaming
  destroyed and later rebuilt is re-bound to its existing sync identity by car id instead of
  being duplicated. (Native cars a joined client's own world spawns mid-session — restoration
  locos, station spawners — deliberately coexist unsynced: DV respawns them endlessly if
  deleted, and real world suppression belongs to the dedicated server.)
- Host license grants (M3.5c): the host can grant catalog licenses to any connected player from
  the session panel — charge-free, explicit, and logged. A fresh guest joining a mature world
  faces a board of license-gated jobs no starting wallet can unlock; the host hands out what's
  needed. The host log now also shows every server-side refusal of any player's proposal
  (`[server] … refused (peer N): reason`) — previously a remote player's rejection was visible
  only on their own screen.
- Bot: `--claim-first` / `--report-interval` / `--abandon-after` exercise the remote career loop
  headlessly, and `--drive` requests a control grant on a host locomotive and pushes its throttle
  over the wire. In `--listen` mode a throttle input from a granted player now drives the bot's
  consist speed — you can sit in its cab and drive it. The claim rig only claims jobs its
  license set allows, logging exactly what each skipped job would need.

- Real-car replication (M3.5b): consists simulated by other players now spawn as REAL train cars —
  correct liveries, the source world's car identity (ids/guids, so job paperwork can name them),
  and their loaded cargo — placed per-bogie on the exact track and span from the sync stream and
  driven kinematically from the owner's snapshots (local physics never fights the remote
  authority). Falls back to the old placeholder boxes per consist when a car type can't be
  resolved. Network protocol is now v4 (car definitions carry identity + cargo).
- Joined-client world handover: joining a session clears the local world's own cars (the host's
  world is the session world) and blocks ALL native game saves until you leave, so a session can
  never leak into your singleplayer savegame — reload your save after leaving to restore your own
  world. On the host, a mid-session save now writes the real pre-session balance instead of the
  mirrored session wallet.
- Bot: `--listen` hosts a session headlessly (join it from the game — the one-PC client test rig),
  `--livery` registers the ghost consist with real car types so it spawns as real cars, and
  `--cargo` loads its wagons. The in-game host log prints a paste-me `--livery` hint.
- Native economy unification for the host (D14): while hosting, the game's money display is a live
  view of the LocoMP wallet (the pre-session balance is restored on leave) and the native career
  manager is the shop — license purchases and fees paid at any cash register burn from the
  server-side ledger, native license grants sync into the career (both directions, including a
  join-time sweep of licenses the save already held), and job takes are pre-gated at the order
  validator so a take the server would refuse is rejected *before* the game consumes the overview
  (previously the rollback destroyed the physical leaflet). Two new world-source-gated protocol
  messages carry the mirrored grants and fees. Host-native careers start at $2000, matching the
  game's own career mode — the wallet doubles as the license budget.
- M3 career core (game-free): server-authoritative jobs and economy behind the **progression policy
  layer** — per-player careers (default) and shared "classic co-op" ship as one switch, routing every
  payout, fee, and license to the right wallet/scope. Jobs are generated deterministically
  server-side (same seed ⇒ same board on any runtime) and claimed exclusively with a TTL, license
  gates, and a per-player claim limit; task steps are validated strictly in order and the final
  delivery mints the payout into the policy-routed wallet — money is only ever minted (payouts,
  starting grants) or burned (fees), and the test suite asserts exact conservation after every
  operation, including a 2,000-op fuzz in both presets. Network protocol is now v3: the handshake
  carries a stable per-player key (the profile/reconnect identity — never broadcast to other
  players), and career state syncs over ten new message types.
- Reconnect grace: a disconnected player's claims are held for 10 minutes (configurable) and restore
  exactly — claim, task progress, wallet, licenses — when the same player key returns; the hold
  lapsing returns the jobs to the board for everyone.
- Persistence v1: a versioned binary server store (schema-checked, bounds-checked) capturing
  profiles, wallets, licenses, the job board with remaining claim time, consists with their last
  known spline positions, junctions, and turntables. Saves are written atomically with a rotating
  backup chain, an interval autosaver serves both frontends, and a cold server restart resumes the
  world: consists come back parked at their saved positions and a rejoin continues a claimed job
  mid-haul across the restart.
- M2 train-sync core (game-free): consist replication built on server-committed **trainset
  transactions with epochs** — couple/uncouple/derail/rerail all retire or re-stamp the trainset, and
  any snapshot carrying a stale (id, epoch) is discarded by construction, never applied. Includes
  spline-space bogie snapshots (derailed cars stream a 6-DOF pose), simulation ownership with
  park/claim, per-cab control grants with input routing to the sim owner, junction sync (duplicate
  throws coalesce only when the resulting state is identical), turntable sync, and a resync escape
  hatch. Verified by a 1,000-transaction fuzz with zero stale-snapshot applications. Network protocol
  is now v2.
- World-topology data model and versioned binary codec — the contract between the in-game world
  extractor and the future dedicated server, which must load track data without a game install.
- In-game world extractor ("Extract world topology" in the mod panel): dumps the live rail network —
  every track edge with its length and the full junction map, using the game's own stable track
  ordering and junction ids — to a topology file the dedicated server can load. Every graph
  connection is positionally cross-checked during extraction and health counters are logged, so a
  bad dump announces itself instead of shipping.
- In-game train sync (the M2 exit, verified live): sessions register every consist in the world and
  stream their positions in spline space; coupling, uncoupling, derailing, and comms-radio rerailing
  are translated from the game's own events into server-committed transactions (no snap-back by
  construction); junction throws sync both ways (observing only the game's inner switch path, so one
  throw is one message); control grants follow cab entry/exit; consists simulated by other players
  render as placeholder ghost cars gliding on the real track splines. Robust to Derail Valley's
  world lifecycle: distance streaming (far cars leaving the simulation), world unloads (the session
  closes itself instead of going stale), and a supported-build gate that turns the mod off politely
  on game builds it has not been verified against.
- Ghost-train test rig: `LocoMP.Bot --consist <n>` drives a synthetic consist along the extracted
  topology (junction-aware, seeded, reconnect-safe) so train sync is testable end-to-end on one PC;
  the host logs paste-ready `--at` and `--start-edge` hints so the ghost spawns next to the player.

### Changed
- Transport disconnect timeout raised 5 s → 15 s: DV's save-load freezes could outlast LiteNetLib's
  default and evict a healthy client mid-load. A genuinely dead peer lingers a few seconds longer,
  which the existing park-on-disconnect + reconnect grace absorb.
- A grant holder's control input that can't resolve to a live control on the owner's car (interior
  unloaded, unverified VR rigs) is now logged once per control instead of dropped silently.

### Fixed
- Consist registration was silently stripping car identity and cargo from the wire (a v4 gap):
  every remote spawn fell back to synthetic car ids and spawned empty. Registration now carries
  the full car spec — network protocol is v5.
- Resumed career saves no longer advertise "ghost" jobs: available host-captured jobs are pure
  mirrors of the live world and are not persisted anymore (the join sweep re-offers them each
  session), and a resumed board is reconciled against the world on hosting — saved entries with
  no native counterpart are retracted instead of sitting claimable while backed by nothing.
  Claimed captured jobs still persist for the reconnect-grace story.
- Remote-player name tags no longer read as doubled text up close: the drop-shadow copy sits at a
  quarter of its previous offset with near-zero depth separation (the old 3 cm behind-the-text gap
  parallaxed visibly off-axis).
- M1 presence networking (game-free): hand-rolled packet codec, `NetServer`/`NetClient` session
  stack (handshake v1 with password, roster, server-authoritative pose relay, time sync), and the
  full LiteNetLib UDP transport with localhost integration tests.
- `tools/LocoMP.Bot` — headless test player(s) for one-PC development and future soak testing:
  joins a live session over UDP, streams synthetic avatar poses (orbit/wander/idle), supports
  swarms (`--count`), join/leave churn (`--churn`), and mismatch testing (`--build`/`--password`).

## [0.0.2] - 2026-07-18

Walking skeleton (milestone M0). Not a playable release.

### Added
- Repository scaffold per the pipeline design: layered projects (`Core`/`Transport`/`Api`/`Shim`/
  `LocoMP`/`Server`) with the game-free vs game-touching split enforced by target frameworks.
- Single version source (`Directory.Build.props`) and central package pinning
  (`Directory.Packages.props`); LiteNetLib pinned exactly to `1.3.5`.
- CI workflows: `pr.yml` (game-free build + tests + DCO check), `build.yml` (DepotDownloader + TOTP →
  full build → API-compat check), `release.yml` (package → GitHub Release → `repository.json` → Nexus),
  and `canary.yml` (nightly game-buildid watcher).
- `LocoMP.Core` protocol version + version-handshake check, with a game-free unit test.
- `LocoMP.Shim` game-adapter spike: UMM entry point + Harmony patches that log live world state
  (car positions, junction throws).
- Contributor scaffolding: DCO, clean-room guidance, issue/PR templates, AI-assistance disclosure.
