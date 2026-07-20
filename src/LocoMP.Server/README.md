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
(perplayer|shared) · `--tick-hz` (30) · `--world`/`--config` (reserved — see below). `--help` for details.

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

## Known limitations (this alpha, M6-B.1/B.2)

- **Server-owned trains are ambient (kinematic).** They roll along the topology and everyone sees them,
  but a player can't yet *claim* or physically couple to one (the server is their sole authority — a
  claim/couple is a safe no-op). Player takeover of a server train is a later refinement.
- **The career board is a synthetic placeholder** (stations Alpha/Bravo/Charlie/Delta; jobs need no
  license). A real Derail Valley career — actual yards, cargo economy, license gates, route distances — is
  *exported from the game* (a Shim/extractor slice, like the topology `.lmpw`); `--config` is the reserved
  hook and currently falls back to the built-in default with a notice.
- **Trains need a topology to walk.** Without `--spawn-trains` (or a `.lmpw`), the server runs bare
  (presence + jobs + persistence); trains can still come from a registering client (bot or game).
- **Joining from the real game** requires the client's handshake to match exactly: protocol version, game
  build (`--build`), mod version, and mod-list hash. If DV sends a non-empty mod-list hash, pass it with
  `--modlist-hash`. A mismatch is a clean reject (logged), not a crash.
- No container/deploy, interest management, or rate-limiting yet — friend-scale + local testing.

## How it's verified

Game-free, headless: `tests/LocoMP.Core.Tests/DedicatedServerIntegrationTests.cs` stands the server up over
real UDP in-process, joins with a real client, asserts a non-empty board arrives, and proves the world
survives a cold restart through the save file.
