# LocoMP — Multiplayer for Derail Valley

[![PR](https://github.com/DSMedia-gg/LocoMP/actions/workflows/pr.yml/badge.svg)](https://github.com/DSMedia-gg/LocoMP/actions/workflows/pr.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**LocoMP** is a clean-room, MIT-licensed multiplayer mod for [Derail Valley](https://www.derailvalley.com/),
built by **DSMedia**. Server-authoritative core, VR + desktop crossplay, and a first-class Mod API.

> **Status: pre-alpha (M0 — walking skeleton).** Nothing to play yet. This repository currently
> contains the project scaffold, CI pipeline, and a game-adapter spike. Follow the milestones in the
> roadmap for what lands next.

## Disclaimers

- **Not affiliated.** LocoMP is an independent, fan-made project. It is **not** affiliated with,
  authorized, or endorsed by **Altfuture s.r.l.**, the developers of Derail Valley.
- **AI-assisted development.** Parts of LocoMP are developed with AI assistance (Anthropic's Claude),
  under human direction and review. This disclosure is a DSMedia policy for our own artifacts.
- **You need a legal copy of Derail Valley.** LocoMP references the game's assemblies by name and
  resolves them from *your* local install at build time. No game code or assets are ever committed
  to this repository or shipped in a release. See [Building from source](#building-from-source).

## Vision (why another one?)

A prior community multiplayer mod exists and is studied for its lessons, but LocoMP is written
independently (clean-room) with a different foundation: the **consist is the replication atom**, all
train-membership changes are **server-committed transactions with epochs** (no couple/uncouple
snap-back), item/economy/persistence are **server-authoritative and per-player by default**, and a
**headless dedicated server** is a first-class target. Public release is gated on a measurable
beat-the-incumbent checklist, not vibes.

## Building from source

**Prerequisites:** a legal Derail Valley install, [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)
(one-time doorstop install), and the .NET SDK (8.x).

```sh
# 1. Point the build at your game install (this file is git-ignored; never commit it).
cp Directory.Build.targets.EXAMPLE Directory.Build.targets
#    then edit DvInstallDir inside it.

# 2. Game-free build + tests (no game install needed — this is what CI runs on every PR):
dotnet test LocoMP.NoGame.slnf

# 3. Full build, including the game-touching Shim + mod (needs the game install from step 1):
dotnet build LocoMP.sln -c Release
```

Game assemblies are **never** committed or redistributed (there is no game EULA license to do so;
norm-compliance is the posture). `LocoMP.Core`, `LocoMP.Transport`, and `LocoMP.Api` never reference
the game or Unity — only `LocoMP.Shim` and the `LocoMP` mod do.

## Project layout

| Project | Targets | Role |
|---|---|---|
| `src/LocoMP.Core` | `netstandard2.0;net48` | netstate, authority, transactions, jobs, persistence — **no Unity/game refs** |
| `src/LocoMP.Transport` | `netstandard2.0;net48` | `ITransport`: LiteNetLib 1.3.5 · Steam relay · Loopback |
| `src/LocoMP.Api` | `netstandard2.0;net48` | public Mod API facade — **DTOs only, no game types** |
| `src/LocoMP.Shim` | `net48` | the **only** game-referencing assembly (Harmony + Publicizer) |
| `src/LocoMP` | `net48` | the UMM mod: entry, UI, `Info.json` |
| `src/LocoMP.Server` | `net8.0` | headless dedicated server (later milestones) |
| `tests/LocoMP.Core.Tests` | `net8.0` | Loopback harness, transaction fuzzing, economy invariants |

## Contributing

We use the **Developer Certificate of Origin** — sign off every commit with `git commit -s`.
LocoMP is **clean-room**: never copy code from other Derail Valley multiplayer mods (studying them is
fine and encouraged). See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE). Release archives include the license and a `SOURCE.txt` link back to this repository.

## Links

- Nexus Mods: https://www.nexusmods.com/derailvalley/mods/1598
- Source: https://github.com/DSMedia-gg/LocoMP
