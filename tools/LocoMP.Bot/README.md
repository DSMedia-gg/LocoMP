# LocoMP.Bot — headless test player

A simulated LocoMP client that joins a real session over UDP and streams synthetic avatar poses.
It exists because the daily rig is **one PC** (03 §11, hard rule 8): the "second player" doesn't
need the game — only the host does. The same tool scales from single-avatar bring-up to the M6
dedicated-server soak runs (many bots, hours, Linux container).

## The one-PC workflow (M1.3 avatar bring-up)

1. Launch Derail Valley and host a LocoMP session (embedded server).
2. The host log prints your position as a ready-made argument (`--at x,y,z`).
3. In a terminal — the built exe needs only the .NET 8 **runtime**, no SDK:

   ```
   tools\LocoMP.Bot\bin\Release\net8.0\LocoMP.Bot.exe --at 671,132,591
   ```

   (Building it in the first place — `dotnet build tools/LocoMP.Bot` or any solution build — is
   what needs the SDK; `dotnet run` rebuilds and therefore also wants the SDK.)

4. In-game: a bot avatar orbits those coordinates. Ctrl+C leaves gracefully.

## What it's for over time

| Use | Invocation sketch |
|---|---|
| Avatar/name-tag bring-up (M1.3) | `--at <coords>` (one orbiting bot) |
| Join/leave churn against a live host | `--count 8 --churn 15` |
| Mismatch-screen testing | `--build WRONG`, `--mod-version 9.9.9`, wrong `--password` |
| Interest-management spread (M2+) | `--count 16 --behavior wander --radius 200` |
| Dedicated-server soak (M6) | `--count 20 --duration 86400` against `LocoMP.Server` on SVHost |

`--help` lists every flag. Defaults target `127.0.0.1:8877` (the canonical LocoMP port,
`NetDefaults.Port`) with the protocol-versioned connect key.

## Design notes

- **No game, no Unity** — net8.0 console over `LocoMP.Core` + `LocoMP.Transport` only. Runs on
  Windows and Linux.
- `BotClient` takes an injected transport factory, so its lifecycle logic (join, churn, reconnect
  with backoff, rejection handling) is unit-tested over Loopback in the game-free suite — the tool
  can't silently rot as the protocol evolves.
- Behaviors (`orbit`/`wander`/`idle`) are pure pose math behind `IBotBehavior`; new ones never
  touch networking. Wander is seeded (`--seed`) so a soak failure can be replayed.
- A handshake **rejection stops the bot** (it's a config mismatch; retrying spams the same refusal).
  A connect **timeout retries with backoff** (a soak run must survive a server restart).

Dev tool only — never shipped in the mod zip.
