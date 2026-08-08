# Contributing to LocoMP

Thanks for your interest. This document outlines the following: the no-copying rule, DCO
sign-off, game-assembly rules, layering, commit hygiene and what CI runs. A few rules keep the
project easy to maintain and legally tidy; read them once and you are set.

## 1. Do not copy code from other mods

LocoMP is written independently under MIT. You are welcome to **study** other Derail Valley
multiplayer mods (they are Apache-2.0; reading and learning from them is encouraged), but **never
copy their code** into LocoMP - that would break our pure-MIT licensing. Record any lessons
learned in design notes or PR descriptions, never as pasted code.

## 2. Sign off your commits (DCO)

We use the [Developer Certificate of Origin](https://developercertificate.org/). Every commit
must be signed off:

```sh
git commit -s -m "feat: add join-queue admission"
```

This appends a `Signed-off-by: Your Name <you@example.com>` line certifying you wrote the change
(or have the right to submit it) under the project's licence. CI rejects unsigned commits.

## 3. Never commit game assemblies

Game DLLs are resolved from your **local** Derail Valley install via a git-ignored
`Directory.Build.targets` (copy `Directory.Build.targets.EXAMPLE`). Do not commit game code or
assets, ever - not even for tests.

## 4. Respect the layering

- `LocoMP.Core`, `LocoMP.Transport` and `LocoMP.Api` **must not** reference UnityEngine or
  `DV.*`. They run headless in tests and the dedicated server.
- Only `LocoMP.Shim` and the `LocoMP` mod may touch game types (they set
  `<LocoMpGameProject>true`).
- `LocoMP.Api` exposes **DTOs only** - never a game type across the public API boundary.

## 5. Commit + PR hygiene

- **Conventional commits** (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `ci:`, `chore:`),
  small and focused.
- Update **`CHANGELOG.md`** under `## [Unreleased]` for any user-visible change.
- Keep tests green: `dotnet test LocoMP.NoGame.slnf` needs **no** game install and must pass.
- Version numbers live in `Directory.Build.props` only; never hand-edit them elsewhere. The wire
  protocol version (`src/LocoMP.Core/Protocol/ProtocolVersion.cs`) is deliberately separate and
  is bumped only on incompatible protocol changes.

## 6. Building + what CI checks

See [README.md - For contributors](README.md#for-contributors) for the build steps. The game-free
subset builds and tests on any OS with just the .NET SDK; the Shim + mod additionally need your
game install.

On every PR, CI runs that same game-free suite plus the DCO sign-off check - so a green local
`dotnet test LocoMP.NoGame.slnf` and signed-off commits are all a PR needs. Nothing in the PR
pipeline touches Steam or the game; the full game-touching build runs separately against private,
metadata-only reference assemblies.

If anything here is unclear, [open an issue](https://github.com/DSMedia-gg/LocoMP/issues) and
ask; we are happy to help you get a first PR over the line.
