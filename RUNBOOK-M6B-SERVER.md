# LocoMP — M6-B dedicated-server in-game smoke runbook

**Purpose:** the first real in-game validation of the headless **dedicated server** track (M6-B.1 join +
persistence, B.2 server-owned trains, **B.3 drivable server trains**). Everything here is game-free-verified
(full suite green, real-exe headless smoke) but has **never been joined from Derail Valley** — this is that
pass. It needs only **you + your PC** (no friend): the server is a standalone process you Direct-connect to.

**How to use:** top to bottom. Each run lists its **setup**, **steps** (with the exact command and the log
line to expect), a **PASS** line, and **WATCH** (failure signatures). Fill in the results table at the
bottom. If something fails, copy the offending log line into the table — don't fix in-game; triage after.

> Convention: `[tag] …` in monospace = a log line in the server console / bot console / DV `output_log.txt`.
> Two terminals: one for `LocoMP.Server`, one for `LocoMP.Bot` (plus the game).

---

## 0. Setup (once)

**Prereqs**
- Derail Valley **B99.7** (`99-build2702`), LocoMP enabled in UMM. The staged mod must speak **protocol
  v10** (this milestone's bump) — re-stage from `repo\dist\LocoMP\` or rebuild if unsure. A v9 mod is
  rejected at handshake against a v10 server.
- Build the server + bot from the current tree:
  `dotnet build src\LocoMP.Server -c Release` and `dotnet build tools\LocoMP.Bot -c Release`.
  Exes: `src\LocoMP.Server\bin\Release\net8.0\LocoMP.Server.exe`,
  `tools\LocoMP.Bot\bin\Release\net8.0\LocoMP.Bot.exe`.
- A world topology `.lmpw`. The repo ships `tests\data\world-99-build2702.lmpw`; both server and bot find
  it automatically when run from the repo, or pass `--world <path>` / set `LOCOMP_WORLD_FILE`. (For the
  real map, extract in-game via the mod panel first.)

**Handshake note:** joining from the real game must match protocol/build/mod-version/**mod-list hash**. If
DV sends a non-empty mod-list hash, start the server with `--modlist-hash <hash>` (the server logs the
value it expects on a reject). A mismatch is a clean, logged reject — not a crash.

---

## B.1 · Join a headless server + persistence

1. **Start the server:**
   `LocoMP.Server.exe --port 8877` (add `--modlist-hash …` if needed).
   Expect `[server] listening on UDP 8877 …` and `Board: N job(s)`.
2. **Join from DV:** LocoMP Direct-connect to `127.0.0.1:8877`. You should be admitted, see presence work,
   and see a **populated job board** (the server fills it with zero players connected, so it's there on
   arrival). The server console logs `admitted <you> (id 1)`.
3. **Persistence — cold restart:** note a job on the board, `stop` the server (or Ctrl+C), restart it with
   the same `--save` path, rejoin. The board + world **resume** (`loaded world … Board: N job(s)`).
4. **Reconnect grace:** restart *your game* (not the server) and rejoin within the grace window — your
   career/claims restore.

**PASS:** you join a standalone server, get a real board, and the world survives both a server restart and
a game restart. **WATCH:** handshake reject (check build/mod-version/`--modlist-hash`); empty board (re-host
once fully loaded); a corrupt/foreign save should fall back to a fresh world with a notice, never a crash.

---

## B.2 · Server-owned trains roll (no bot)

1. **Start the server with its own trains:**
   `LocoMP.Server.exe --port 8877 --spawn-trains 3 --train-cars 3 --train-speed 10`
   Expect `[server] driving 3 server-owned train(s) of 3 car(s) at 10 m/s along <world> (2073 edges).`
2. **Join from DV.** Three consists roll through the valley on their own — no bot, no second player. They
   move smoothly (snapshot-driven) and everyone who joins sees them at their live positions (join burst).

**PASS:** server-driven trains are visible and moving from a fresh solo join. **WATCH:** trains that never
appear (no `.lmpw` — pass `--world`); trains frozen at spawn (topology walk stalled — check the edge count
in the banner is non-zero); a train that stutters/teleports (snapshot cadence — note it).

---

## B.3 · Claim + drive + release a server train  *(rig: server + bot; you watch from the game)*

The new capability: a client can **borrow** one of the server's ambient trains, **drive** it, and **hand it
back**. In-game claim/drive UX for a *real player* is a later Shim slice, so this pass drives it with the
**bot** — you host nothing, you just **watch from your game** as an ambient train gets taken over.

1. Server running as in B.2 (`--spawn-trains 3`). Join from DV so you can watch.
2. In a second terminal, borrow one train and drive it for 20 s, then release:
   `LocoMP.Bot.exe --host 127.0.0.1 --claim-server-train --world <same .lmpw> --consist-speed 12 --drive-seconds 20`
   Expect the bot log in order:
   - `found server train <id> (3 car(s)) — asking to take it over`
   - `now driving server train <id> — the server has stopped driving it`
   - `server train <id>: streaming from edge <e> at 12 m/s`
   - after 20 s: `released server train <id> back to the server (it resumes its route)`
3. **What to watch in the game:** the moment the bot claims it, **that train's motion changes** — it now
   follows the bot's route (different speed/branches) instead of the server's. When the bot releases it, the
   server takes over again and it resumes on the server's schedule. (There's a small position discontinuity
   on hand-back — the server resumes from where the train was borrowed; that's expected this slice.)
4. **Reclaim on disconnect:** re-run the bot **without** `--drive-seconds` (it holds the train), watch it
   drive, then **Ctrl+C the bot**. The train must return to the server (resume moving), not freeze dead.
5. **No theft (optional, needs a 2nd client):** with the bot already driving a train, a second joiner
   claiming the *same* train is refused — only the server's own ambient trains are takeable, never a train
   another player is driving.

**PASS:** the bot borrows a server train, visibly drives it, and hand-back (release *and* Ctrl+C) returns it
to the server which resumes. **WATCH:** the train not changing behaviour on claim (ownership flip didn't
reach the game — check the server console for the owner change); the train **freezing** after the bot
disconnects (reclaim-on-disconnect regressed — the load-bearing check); the bot logging `no longer own
server train …` mid-drive (it lost ownership unexpectedly).

---

## Results

| Run | Result | Notes / offending log line |
|---|---|---|
| B.1 join + board | ☐ pass ☐ fail | |
| B.1 persistence (server restart) | ☐ pass ☐ fail | |
| B.1 reconnect grace (game restart) | ☐ pass ☐ fail | |
| B.2 server trains roll | ☐ pass ☐ fail | |
| B.3 claim + drive (visible takeover) | ☐ pass ☐ fail | |
| B.3 release → server resumes | ☐ pass ☐ fail | |
| B.3 reclaim on bot Ctrl+C | ☐ pass ☐ fail | |
| B.3 no-theft (2nd client) | ☐ pass ☐ fail ☐ n/a | |

**Deferred to later slices (don't count against M6-B):** a *real player* claiming/driving a server train
from inside the game (Shim UX); physically coupling to a server train; junction-throwing as a server train
crosses switches (movement is snapshot-correct regardless); a real DV career via `--config`; container +
SVHost deploy.

**If anything fails:** paste the log line into the table and stop poking that path — report back and we
triage + fix same-session.
