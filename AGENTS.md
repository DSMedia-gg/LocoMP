# AGENTS.md - agent guide for LocoMP

Instructions for AI coding agents working on this repository, and worth a skim for humans too.
Read [CONTRIBUTING.md](CONTRIBUTING.md) first; this file adds the commands, the boundaries and
the invariants an agent must hold.

## Build + test commands

```sh
# The default loop: game-free build + tests. Works on any OS, needs only the .NET 8 SDK.
# This is exactly what CI runs on every PR, and it must pass before you open one.
dotnet test LocoMP.NoGame.slnf

# Full build including the game-touching Shim + mod. Needs a local Derail Valley install:
cp Directory.Build.targets.EXAMPLE Directory.Build.targets   # git-ignored; set DvInstallDir inside
dotnet build LocoMP.sln -c Release
```

Prefer the game-free solution filter for anything that does not need the game. If you change
`Shim` or mod code, build the full solution at least once before opening a PR.

## Hard boundaries

- **Never commit game assemblies, game code or game assets** - not as test fixtures, not as
  decompiled snippets, not at all. Game DLLs resolve from the local install via the git-ignored
  `Directory.Build.targets`; that is the only sanctioned way to touch them.
- **Never copy code from other Derail Valley multiplayer mods.** Studying them is fine and
  encouraged; copying breaks our pure-MIT licensing.
- **Never commit machine-specific files** - `Directory.Build.targets`, local paths, IDE state.
- **Preserve work you did not create.** If the worktree is dirty with someone else's changes,
  work around them; do not revert or sweep them into your commits.

## Layering (CI and reviewers enforce this)

- `LocoMP.Core`, `LocoMP.Transport`, `LocoMP.Api`: **no UnityEngine, no `DV.*`**. These run
  headless in tests and the dedicated server.
- `LocoMP.Shim` is the **only** assembly that references the game (Harmony patches +
  Publicizer). Game-touching projects set `<LocoMpGameProject>true`.
- `LocoMP.Api` exposes **DTOs only**; never leak a game type across the public API boundary.
- Dependency ceiling: `netstandard2.0` / `net48` (the game is Unity 2019.4 Mono). Package
  versions are central in `Directory.Packages.props`, and LiteNetLib is pinned **exactly 1.3.5**
  because 2.x cannot load under Unity 2019.4 Mono - do not bump it.

## Protocol + state invariants

- **Consist transactions:** trainset membership only ever changes via server-committed
  transactions that bump the per-trainset epoch; snapshots stamped with a stale epoch are
  discarded, never applied. Do not add any train-state path that bypasses this.
- **Serialization:** hand-rolled packets on hot paths; MessagePack with
  `MessagePackSecurity.UntrustedData` for bulk data. Never BinaryFormatter, never Newtonsoft
  `TypeNameHandling`, never MessagePack Typeless.
- **Versioning:** the release version lives in `Directory.Build.props` and nowhere else (CI
  stamps it everywhere it needs to go). The wire protocol version lives in
  `src/LocoMP.Core/Protocol/ProtocolVersion.cs`, is deliberately separate, and is bumped only on
  incompatible protocol changes.

## Conventions

- Conventional commits (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `ci:`, `chore:`), small
  and focused.
- **DCO sign-off on every commit** (`git commit -s`); CI rejects unsigned commits.
- Update `CHANGELOG.md` under `## [Unreleased]` for any user-visible change.
- Match the existing code style; `.editorconfig` is authoritative.

## Repo map

| Path | What lives there |
|---|---|
| `src/LocoMP.Core` | netstate, authority, transactions, jobs, persistence (game-free) |
| `src/LocoMP.Transport` | `ITransport` implementations: LiteNetLib UDP, Steam relay, Loopback |
| `src/LocoMP.Api` | public Mod API facade (DTOs only) |
| `src/LocoMP.Shim` | Harmony patches + game integration (the only game-referencing code) |
| `src/LocoMP` | the Unity Mod Manager mod: entry point, UI, `Info.json` |
| `src/LocoMP.Server` | headless dedicated server (`net8.0`) |
| `tests/LocoMP.Core.Tests` | the game-free suite CI runs: Loopback harness, fuzzing, invariants |
| `tools/LocoMP.Bot` | headless test client that joins a real session (no game needed) |
| `docker/` | container packaging for the dedicated server |

If a task seems to require breaking one of the rules above, stop and raise it with a maintainer
in the PR or an issue instead of working around it. We would much rather answer a question than
untangle a workaround.
