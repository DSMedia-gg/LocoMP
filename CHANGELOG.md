# Changelog

All notable changes to LocoMP are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> Release CI **fails** if the version being released has no section here. Every release version needs
> its own `## [x.y.z]` heading (05 §2). Keep an `## [Unreleased]` section at the top between releases.

## [Unreleased]

### Added
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

### Fixed
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
