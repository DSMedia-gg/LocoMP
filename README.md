# LocoMP - Multiplayer for Derail Valley

[![PR](https://github.com/DSMedia-gg/LocoMP/actions/workflows/pr.yml/badge.svg)](https://github.com/DSMedia-gg/LocoMP/actions/workflows/pr.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**LocoMP** is an MIT-licensed multiplayer mod for [Derail Valley](https://www.derailvalley.com/),
built by **DSMedia**. A server-authoritative core, per-player careers by default, a headless
dedicated server and a first-class Mod API.

> **Status: pre-alpha, but playable for testing.** 2 players can install the mod, load the same
> world, see each other and run trains together over a direct connection, and there is an early
> headless dedicated server as well. It is early and rough, and the "Friends via Steam" join path
> is still to come, so connections are direct IP:port (UDP 8877) for now.

This README is split by audience: [players](#for-players) first, then
[contributors](#for-contributors), then the [project background](#about-the-project). Jump to
whichever fits you.

## For players

You do not need to build anything to play. The short version: grab a packaged `LocoMP` folder,
drop it into Derail Valley's `Mods\` folder via
[Unity Mod Manager](https://www.nexusmods.com/site/mods/21), then one player hosts from their
loaded world and everyone else joins by IP on UDP port `8877`. Everyone needs the same LocoMP
build and the same game build; a mismatch is refused cleanly with a message, not a crash.

- **[Playtest guide](PLAYTEST.md)** - the full step-by-step: what you need, installing, hosting,
  joining over LAN or the internet, and what to expect from a pre-alpha. If you were handed a
  build to try, start here.
- **[Dedicated server](src/LocoMP.Server/README.md)** - a headless, game-free server you can run
  on any PC or in a [container](docker/README.md). Also the easiest way to test solo: run it
  locally and join it from your own game.
- **Reporting bugs** - logs are gold.
  [Section 7 of the playtest guide](PLAYTEST.md#7-reporting-bugs-this-is-the-valuable-part)
  covers exactly what to grab and where to send it.

You need your own legal copy of Derail Valley; LocoMP ships no game code or assets.

## For contributors

Code, tests, docs and good bug reports are all welcome. To build, you will need the following:
the .NET SDK (8.x) for the game-free projects, plus a legal Derail Valley install and
[Unity Mod Manager](https://www.nexusmods.com/site/mods/21) (one-time doorstop install) if you
want to build the game-touching half.

```sh
# 1. Game-free build + tests (no game install needed - this is what CI runs on every PR):
dotnet test LocoMP.NoGame.slnf

# 2. For the full mod, point the build at your game install
#    (this file is git-ignored; never commit it):
cp Directory.Build.targets.EXAMPLE Directory.Build.targets
#    then edit DvInstallDir inside it.

# 3. Full build, including the game-touching Shim + mod:
dotnet build LocoMP.sln -c Release
```

Game assemblies are **never** committed to this repository or redistributed publicly. Local builds
resolve them from *your* install; CI compiles against metadata-only, method-body-stripped
reference assemblies (see `tools/refs-export`) generated from a licensed copy and held privately -
reference use only, never shipped in any artifact, removed on Altfuture's request. `LocoMP.Core`,
`LocoMP.Transport` and `LocoMP.Api` never reference the game or Unity; only `LocoMP.Shim` and the
`LocoMP` mod do.

### Project layout

| Project | Targets | Role |
|---|---|---|
| `src/LocoMP.Core` | `netstandard2.0;net48` | netstate, authority, transactions, jobs, persistence - **no Unity/game refs** |
| `src/LocoMP.Transport` | `netstandard2.0;net48` | `ITransport`: LiteNetLib 1.3.5 · Steam relay · Loopback |
| `src/LocoMP.Api` | `netstandard2.0;net48` | public Mod API facade - **DTOs only, no game types** |
| `src/LocoMP.Shim` | `net48` | the **only** game-referencing assembly (Harmony + Publicizer) |
| `src/LocoMP` | `net48` | the UMM mod: entry, UI, `Info.json` |
| `src/LocoMP.Server` | `net8.0` | headless dedicated server |
| `tests/LocoMP.Core.Tests` | `net8.0` | Loopback harness, transaction fuzzing, economy invariants |

### Before your first PR

- Read **[CONTRIBUTING.md](CONTRIBUTING.md)** for commit conventions, DCO sign-off, the layering
  rules and what CI checks.
- Pointing an AI agent at the repo? **[AGENTS.md](AGENTS.md)** gives it the build commands and
  the invariants it must not break.

## About the project

**Why another multiplayer mod?** LocoMP is written from scratch around a few firm ideas: the
consist is the unit of replication, train-membership changes are server-committed transactions
(no couple/uncouple snap-back), items, economy and persistence are server-authoritative and
per-player by default, and a headless dedicated server is a first-class target rather than an
afterthought.

**Disclaimers:**

- **Not affiliated.** LocoMP is an independent, fan-made project. It is **not** affiliated with,
  authorised or endorsed by **Altfuture s.r.l.**, the developers of Derail Valley.
- **AI-assisted development.** Parts of LocoMP are developed with AI assistance (Anthropic's
  Claude), under human direction and review. This disclosure is a DSMedia policy for our own
  artefacts.
- **You need a legal copy of Derail Valley.** LocoMP references the game's assemblies by name and
  resolves them from *your* local install at build time. No game code or assets are ever
  committed to this repository or shipped in a release.

## License

[MIT](LICENSE). Release archives include the licence and a `SOURCE.txt` link back to this
repository.

## Links

- Nexus Mods: https://www.nexusmods.com/derailvalley/mods/1598
- Source: https://github.com/DSMedia-gg/LocoMP

Questions, ideas or something broken?
[Open an issue](https://github.com/DSMedia-gg/LocoMP/issues) and we will get back to you as soon
as we can.
