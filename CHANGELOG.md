# Changelog

All notable changes to LocoMP are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> Release CI **fails** if the version being released has no section here. Every release version needs
> its own `## [x.y.z]` heading (05 §2). Keep an `## [Unreleased]` section at the top between releases.

## [Unreleased]

## [0.0.2] - 2026-07-18

Walking skeleton (milestone M0). Not a playable release.

### Added
- Repository scaffold per the pipeline design: layered projects (`Core`/`Transport`/`Api`/`Shim`/
  `LocoMP`/`Server`) with the game-free vs game-touching split enforced by target frameworks.
- Single version source (`Directory.Build.props`) and central package pinning
  (`Directory.Packages.props`); LiteNetLib pinned exactly to `1.3.5`.
- CI workflows: `pr.yml` (game-free build + tests + DCO check), `build.yml` (DepotDownloader + TOTP →
  full build → API-compat check), `release.yml` (package → GitHub Release → `repository.json` → Nexus),
  and `canary.yml` (nightly game-buildid watcher).
- `LocoMP.Core` protocol version + version-handshake check, with a game-free unit test.
- `LocoMP.Shim` game-adapter spike: UMM entry point + Harmony patches that log live world state
  (car positions, junction throws).
- Contributor scaffolding: DCO, clean-room guidance, issue/PR templates, AI-assistance disclosure.
