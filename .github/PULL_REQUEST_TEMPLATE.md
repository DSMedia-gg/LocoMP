<!-- Thanks for contributing to LocoMP! Please keep PRs small and focused. -->

## What & why

<!-- What does this change do, and why? Link any related issue (Fixes #123). -->

## Checklist

- [ ] Commits are **signed off** (`git commit -s`) — DCO required, CI enforces it.
- [ ] **Clean-room:** no code copied from other Derail Valley multiplayer mods (studying is fine; copying is not).
- [ ] `CHANGELOG.md` updated under `## [Unreleased]` (for any user-visible change).
- [ ] `dotnet test LocoMP.NoGame.slnf` passes locally (game-free — no install needed).
- [ ] Layering respected: `Core`/`Transport`/`Api` stay free of UnityEngine and `DV.*`.
- [ ] No game assemblies or assets committed.
