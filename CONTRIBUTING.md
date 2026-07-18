# Contributing to LocoMP

Thanks for your interest. A few rules keep this project legally clean and easy to maintain.

## 1. Clean-room — do not copy other mods

LocoMP is a **clean-room, MIT** implementation. You may **study** other Derail Valley multiplayer
mods (they are Apache-2.0 — reading and learning from them is encouraged), but **never copy their
code** into LocoMP. Copying breaks our pure-MIT licensing. Record any lessons learned in design notes
or PR descriptions, never as pasted code.

## 2. Sign off your commits (DCO)

We use the [Developer Certificate of Origin](https://developercertificate.org/). Every commit must be
signed off:

```sh
git commit -s -m "feat: add join-queue admission"
```

This appends a `Signed-off-by: Your Name <you@example.com>` line certifying you wrote the change (or
have the right to submit it) under the project's license. CI (`pr.yml`) rejects unsigned commits.

## 3. Never commit game assemblies

Game DLLs are resolved from your **local** Derail Valley install via a git-ignored
`Directory.Build.targets` (copy `Directory.Build.targets.EXAMPLE`). Do not commit game code or assets,
ever — not even for tests.

## 4. Respect the layering

- `LocoMP.Core`, `LocoMP.Transport`, `LocoMP.Api` **must not** reference UnityEngine or `DV.*`.
  They run headless in tests and the dedicated server.
- Only `LocoMP.Shim` and the `LocoMP` mod may touch game types (they set `<LocoMpGameProject>true`).
- `LocoMP.Api` exposes **DTOs only** — never a game type across the public API boundary.

## 5. Commit + PR hygiene

- **Conventional commits** (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `ci:`, `chore:`), small
  and focused.
- Update **`CHANGELOG.md`** under `## [Unreleased]` for any user-visible change.
- Keep tests green: `dotnet test LocoMP.NoGame.slnf` needs **no** game install and must pass.
- Never hand-edit version numbers outside `Directory.Build.props` (05 §2).

## 6. Building

See [README.md → Building from source](README.md#building-from-source). The game-free subset builds
and tests on any OS with just the .NET SDK; the Shim + mod additionally need your game install.
