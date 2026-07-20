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
LocoMP.Server --port 8877
```

Binds UDP 8877, generates a small built-in job board, and persists to `locomp-server.save` beside the exe
(autosave every 60 s + a save on clean exit). Type `help` at the prompt for console commands
(`status`, `save`, `stop`).

Build/run from source:
```
dotnet run --project src/LocoMP.Server -- --port 8877
```

### Options
`--port` (8877) · `--key` · `--save <path>` · `--password` · `--max-players` (32) · `--build`
(99-build2702) · `--mod-version` · `--modlist-hash` · `--name` · `--autosave-seconds` (60) · `--preset`
(perplayer|shared) · `--tick-hz` (30) · `--world`/`--config` (reserved — see below). `--help` for details.

## Solo-test recipe (no friend needed)

1. **Start the server:**
   ```
   LocoMP.Server --port 8877
   ```
2. **Populate it with a train** — run a bot that joins *first* (so it becomes the world source) and
   registers a real consist:
   ```
   LocoMP.Bot --host 127.0.0.1 --port 8877 --consist 3 --livery LocoDiesel,BoxcarBrown,BoxcarBrown
   ```
3. **Join from Derail Valley** as a second client (LocoMP's Direct-connect to `127.0.0.1:8877`). You'll see
   the bot's train, the job board, and presence. Restart your game — or the server — and rejoin: the world
   persists.

Run more bots (`--count`, `--behavior wander`, `--claim-first`, `--grab-items`, …) to populate presence,
jobs, and items. See `LocoMP.Bot --help`.

## Known limitations (this alpha slice, M6-B.1)

- **A fresh server has no trains of its own.** Trains come from whichever client registers them (a bot, or
  a second game client). Server-owned *kinematic* trains — so no bot is needed — are the next slice
  (kinematic coaster).
- **The career board is a synthetic placeholder** (stations Alpha/Bravo/Charlie/Delta; jobs need no
  license). A real Derail Valley career — actual yards, cargo economy, license gates, route distances — is
  *exported from the game* (a Shim/extractor slice, like the topology `.lmpw`); `--config` is the reserved
  hook and currently falls back to the built-in default with a notice.
- **Joining from the real game** requires the client's handshake to match exactly: protocol version, game
  build (`--build`), mod version, and mod-list hash. If DV sends a non-empty mod-list hash, pass it with
  `--modlist-hash`. A mismatch is a clean reject (logged), not a crash.
- No container/deploy, interest management, or rate-limiting yet — friend-scale + local testing.

## How it's verified

Game-free, headless: `tests/LocoMP.Core.Tests/DedicatedServerIntegrationTests.cs` stands the server up over
real UDP in-process, joins with a real client, asserts a non-empty board arrives, and proves the world
survives a cold restart through the save file.
