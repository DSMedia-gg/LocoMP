# Changelog

All notable changes to LocoMP are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> Release CI **fails** if the version being released has no section here. Every release version needs
> its own `## [x.y.z]` heading (05 §2). Keep an `## [Unreleased]` section at the top between releases.

## [Unreleased]

### Added
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
