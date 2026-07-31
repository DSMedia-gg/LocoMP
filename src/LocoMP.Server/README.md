# LocoMP.Server — headless dedicated server (alpha)

A standalone, game-free process that runs the real LocoMP session (`NetServer` + persistence + the
deterministic career board) over LiteNetLib UDP. No Unity, no Derail Valley install, no game assemblies —
it's the same Core stack the tests and the bot exercise, wired into a persistent server.

**Why it exists (pulled forward from M6 Track B):** it lets you test multiplayer **solo**. You join it
from your own game as a client, against a world that persists across game *and* server restarts — no second
player needed for the whole joined-client surface (world handover, reconnect grace, persistence, presence,
the job board).

## Run it

```
LocoMP.Server --port 8877 --spawn-trains 3
```

Binds UDP 8877, generates a small built-in job board, **drives 3 trains of its own** along the extracted
world topology, and persists to `locomp-server.save` beside the exe (autosave every 60 s + a save on clean
exit). Type `help` at the prompt for console commands (`status`, `save`, `stop`). Drop `--spawn-trains` for
a bare server (presence + jobs only).

Build/run from source:
```
dotnet run --project src/LocoMP.Server -- --port 8877
```

### Options
`--port` (8877) · `--key` · `--save <path>` · `--password` · `--max-players` (32) · `--build`
(99-build2702) · `--mod-version` · `--modlist-hash` · `--name` · `--autosave-seconds` (60) · `--preset`
(perplayer|shared) · `--tick-hz` (30) · `--world` (reserved — see below) · `--config <file.lmpc>` /
`--dump-config <file.lmpc>` (real career — see below) · `--soak-report <s>` / `--duration <s>` (unattended
soak — see below). `--help` for details.

### Career config (`--config`)

By default the server runs a small synthetic board (Alpha/Bravo/Charlie/Delta, license-free jobs).
`--config <file.lmpc>` loads a REAL career instead — actual yards, cargo economy, license gates, route
distances, station world-locations for the task-proximity gate. The `.lmpc` is the config file's
authoritative source (including the progression preset). A missing, corrupt, or foreign file logs a
notice and falls back to the built-in default, so the server always starts.

To produce one before the in-game exporter exists, `--dump-config <file.lmpc>` writes the built-in default
to a file and exits — a seed + a way to exercise `--config` end-to-end:
```
LocoMP.Server --dump-config career.lmpc      # write the default career, then exit
LocoMP.Server --config career.lmpc           # load it
```
(A tool that EXPORTS a real career straight from a running game — like the topology `.lmpw` — is a later
Shim/extractor slice; the file format + loader are done.)

## Solo-test recipe (no friend, no bot needed)

1. **Start the server with its own trains:**
   ```
   LocoMP.Server --port 8877 --spawn-trains 3
   ```
   The server drives 3 consists along the extracted topology. (`--spawn-trains` needs a `.lmpw` — pass
   `--world <path>`, set `LOCOMP_WORLD_FILE`, or run from the repo, which finds `tests/data/world-*.lmpw`.
   Extract the real map in-game via the mod panel.)
2. **Join from Derail Valley** (LocoMP's Direct-connect to `127.0.0.1:8877`). You'll see the server's
   trains rolling through the valley, the job board, and presence. Restart your game — or the server — and
   rejoin: the world persists.

Want more players/activity? Run bots too (`LocoMP.Bot --host 127.0.0.1 --count 4 --behavior wander`,
`--claim-first`, `--grab-items`, `--consist 3 --livery ...`). See `LocoMP.Bot --help`.

**Watch a server train get driven (M6-B.3):** run one bot that borrows and drives one of the server's
own trains, then hands it back:
```
LocoMP.Bot --host 127.0.0.1 --claim-server-train --world <same .lmpw> --drive-seconds 20
```
It claims an ambient server train, drives it along the topology for 20 s (its trajectory diverges from
the server's route — visible from your game), then releases it and the server resumes. Ctrl+C also hands
it back (reclaim-on-disconnect).

## Endurance soak (unattended, M6-B soak exit)

For a long hands-off run, the server watches its own health and can stop itself:

```
LocoMP.Server --port 8877 --spawn-trains 4 --soak-report 30 --duration 86400
LocoMP.Bot --host 127.0.0.1 --count 8 --behavior wander --churn 15 --duration 86400
```

`--soak-report <s>` prints a health line every `s` seconds — players, trains, jobs, items, memory, and the
internal accounting oracles (money conservation = the ledger balances mint − burn exactly; item
conservation = live items == spawned − despawned). It **latches** to `⚠ UNHEALTHY` the instant an oracle
breaks or memory drifts past 2× its baseline, so a leak can't slip by unwatched. `--duration <s>`
self-terminates the server after `s` seconds (0 = run until Ctrl+C/`stop`), which also **avoids the env
trap** where a detached server exe outlives its shell and locks `LocoMP.Core.dll` against later builds.

At shutdown the server prints `[soak] PASS …` (exit code 0) or `[soak] FAIL …` (exit code **2**) — so an
overnight run's exit code alone tells you whether the world stayed sound.

### What the `heap` column actually is

The **retained set** — what survives a full collection — not whatever happens to be allocated at that
instant. Each report does `GC.Collect()` → `WaitForPendingFinalizers()` → `GC.Collect()` before reading.
Both halves matter:

- **Collecting first**, because the instantaneous heap sawtooths. Measured here: it climbed ~3.3 MB per
  20 s to 14–17 MB then collapsed on gen2 every ~100 s — a peak/floor ratio of ~4.3×, *larger than any
  useful leak threshold*, so the verdict depended on where in the sawtooth the sample landed. A leak is a
  rising floor; this measures the floor. Post-collection the same workload reads a flat 2.4–2.5 MB.
- **Two collections with the finalizers drained between them**, because an object with a finalizer
  survives the collection that finds it unreachable and is only reclaimed by the next one. A single
  collect counts every finalizable object as live — and a build-up of those looks exactly like the leak
  you are hunting.

Two consequences worth knowing:

- **Use `--soak-report 30` or higher for a real soak.** The forced collection blocks; it cannot cause a
  false FAIL, but frequent stalls stop the run representing unattended behaviour. The server prints a
  NOTE below 30 s.
- **The 2× threshold is calibrated on workstation GC** (pinned in the csproj so a dev PC and the
  container agree) against a measured 6.5-minute run: retained went 2.0 → 2.5 MB (1.25×) and then held
  flat across 144 joins. **Under Docker with a memory limit the GC's heap hard limit becomes a percentage
  of that limit**, so re-baseline in the container before trusting the number there.

## Known limitations (this alpha, M6-B.1/B.2/B.3)

- **Player takeover of a server train is wire-level + bot-only so far (M6-B.3).** A client can claim one
  of the server's ambient trains, drive it (the server stops, and hands it back on release/disconnect),
  and no one can steal or block another player's train — all proven headless and drivable via the bot
  (`--claim-server-train`). What's *not* here yet: the in-game mod UX for a real player to request and
  physically drive a claimed server train from within Derail Valley (a Shim slice). Physical coupling to
  a server train, and junction-throwing as it crosses switches (movement is snapshot-correct regardless),
  remain later refinements.
- **The default career board is a synthetic placeholder** (stations Alpha/Bravo/Charlie/Delta; jobs need
  no license). `--config <file.lmpc>` now loads a real career (see above), but the tool that *exports* a
  `.lmpc` straight from a running game — actual yards, cargo economy, license gates, route distances, like
  the topology `.lmpw` — is still a later Shim/extractor slice. Until then, `--dump-config` gives you a
  default seed.
- **Trains need a topology to walk.** Without `--spawn-trains` (or a `.lmpw`), the server runs bare
  (presence + jobs + persistence); trains can still come from a registering client (bot or game).
- **Joining from the real game** requires the client's handshake to match exactly: protocol version, game
  build (`--build`), mod version, and mod-list hash. If DV sends a non-empty mod-list hash, pass it with
  `--modlist-hash`. A mismatch is a clean reject (logged), not a crash.
- **Container packaging is here (`../../docker/`)** — a game-free two-stage image + compose for SVHost
  (raw UDP, not behind Traefik/CF; graceful SIGTERM save). The image build + the actual deploy are your
  call (not automated). Rate-limiting is still a later slice — friend-scale + local testing for now.

## Interest management (`--interest`)

Off by default. With `--interest` the server only sends a client the trains (and, with
`--interest-players`, the players) near it, instead of broadcasting everything to everyone. Measured
effect on train traffic — the dominant channel, ~96% of what a session sends — is an **83% reduction**
for a client whose neighbourhood holds 4 of 24 consists (`docs/PERF-BASELINE.md` §3c).

```
LocoMP.Server --world world-99-build2702.lmpw --spawn-trains 8 --interest
```

- `--interest-radius <m>` — enter radius (default 500). The leave radius is 1.5× it; the gap is a
  hysteresis band so a train hovering at the boundary doesn't flicker in and out.
- `--interest-players` — also filter player poses. Separate because poses are only ~4% of the traffic.

**Filtering trains needs a `.lmpw` with world geometry** (codec v2 — re-extract from inside the game).
Without one — no `--world`, a v1 file, or a topology whose build doesn't match `--build` — the server
keeps broadcasting trains and **says so in the startup banner**:

```
[server] interest management ON — enter 500 m / leave 750 m, players broadcast,
         trains BROADCAST (no world geometry — extract a fresh .lmpw)
```

That fallback is deliberate: a train the server can't place must be sent to everyone, never hidden
from everyone. World **items** are not filtered — they don't stream, so they cost ~0 bandwidth.

## How it's verified

Game-free, headless: `tests/LocoMP.Core.Tests/DedicatedServerIntegrationTests.cs` stands the server up over
real UDP in-process, joins with a real client, asserts a non-empty board arrives, and proves the world
survives a cold restart through the save file. `ServerOwnedTrainTests.cs` covers server-owned trains: they
ride the join burst and move under the server's snapshots, a player can **claim and drive** one (the
server stops driving it, the driver's snapshots reach a watching client, and a release hands it back), a
second player can't steal a train someone's already driving, and a disconnecting borrower's train returns
to the server rather than stranding. `SoakTests.cs` is the endurance proof — an accelerated in-process
soak (hundreds of join/leave + claim/drive/release + item spawn/despawn waves over a `ManualClock`, in
milliseconds) asserting after every wave that money + item conservation hold, trainsets never leak under
churn, the consist epoch invariant never trips, and a late joiner still converges to the full world; and
`SoakReporterTests.cs` covers the `--soak-report` throttle + the unhealthy-latch/exit-code logic.
