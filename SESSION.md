# SESSION log — LocoMP

Append one entry per work burst (newest at top). Pair with `STATE.md` (current state) — this file is the
narrative history. See `../CLAUDE.md` for the discipline.

---

## 2026-07-18 — M0 walking skeleton (scaffold)

**Goal:** stand up the repo per `../05-PIPELINE.md` §1, get the game-free build+test green, author the CI
pipeline, and prove the Shim seam compiles against Derail Valley B99.7.

**Done:**
- Cloned `DSMedia-gg/LocoMP` into `repo/` (kept the planning corpus private in the parent — layout Option A).
- Seven layered projects; layering enforced by target frameworks. Trivial-but-real Core unit
  (`VersionHandshake`) + Loopback transport test → **5/5 green**, no game needed.
- Central package pinning (LiteNetLib **=1.3.5**); single version source; `net48` compiles on any OS via
  `Microsoft.NETFramework.ReferenceAssemblies`.
- Four CI workflows. `pr.yml`'s build+test verified locally (identical to the CI command).
- Shim spike: `Main.Load` (UMM) → `WorldStateSpike` logs `TrainCar` positions + `Junction.Switch` throws.
  **Full solution builds against the local B99.7 install**, mod output carries no game assemblies.
- Repo hygiene: MIT, DCO, clean-room CONTRIBUTING, AI-disclosure README, issue/PR templates, CHANGELOG.

**Decisions this session (implementation-level; none change 00's D1–D12):**
- Repo lives at `…/LocoMP/repo/`; planning corpus stays in the parent, out of the public repo (Cody chose Option A).
- Game refs isolated behind `LocoMpGameProject=true` + a git-ignored `Directory.Build.targets`; the reference
  list is committed as `.EXAMPLE`. Only `$(DvInstallDir)` is per-contributor.
- Version 0.0.2 (0.0.1 was the Nexus bootstrap). Protocol version 1, tracked separately.

**Learned / notes:**
- LiteNetLib ships its own `DeliveryMethod` enum → collides with Core's; the adapter must translate, never
  import both. Fixed by fully-qualifying LiteNetLib types in the transport adapter.
- `Junction` is in `DV.RailTrack.dll` (not Assembly-CSharp); `CarSpawner` raises `CarSpawned`/`CarAboutToBeDeleted`
  and is a `DV.Utils.SingletonBehaviour<>`. Verified by reflection-only load (metadata only — clean-room safe).
- The Shim's compile-time refs (UnityModManager + 0Harmony) are NOT in the Steam depot → `build.yml` must
  also fetch the UMM distribution (bundles both). Parameterized as the `UMM_ZIP_URL` repo variable.

**Next:** push (gated) → wire Steam/Nexus secrets → in-game Shim run. See `STATE.md` → Next.
