# CI setup & first-push runbook

Everything CI needs to go green, in order. All of this is gated on Cody (hard rule 7 — externally visible).
The game-free `pr.yml` needs **none** of this; it works the moment the code is pushed.

## 1. Secrets (Settings → Secrets and variables → Actions → Secrets)

| Secret | Used by | Notes |
|---|---|---|
| `STEAM_USERNAME` | build, release | CI Steam account that owns **only** Derail Valley, no payment methods (05 §6). |
| `STEAM_PASSWORD` | build, release | " |
| `STEAM_SHARED_SECRET` | build, release | Steam Guard shared secret (base64). Export via Steam Desktop Authenticator / SDA. Feeds `CyberAndrii/steam-totp`. |
| `NEXUS_API_KEY` | release | Personal API key (any team member's works — Upload API). Pairs with the non-secret `file_id: 7674930`. |
| `DISCORD_WEBHOOK` | canary | *Optional.* Canary breakage alerts. |

## 2. Variables (same page → Variables)

| Variable | Used by | Notes |
|---|---|---|
| `UMM_ZIP_URL` | build, release | Direct link to a UnityModManager release zip that contains `UnityModManager.dll` + `0Harmony.dll` (NOT in the Steam depot). Confirm the exact URL for the pinned UMM version. |

## 3. Steam depot account (bootstrap 05 §7 item 2)

- Confirm the second Steam account owns Derail Valley (app 588030).
- Export its Steam Guard **shared secret** → `STEAM_SHARED_SECRET`. Anonymous login does **not** work for DV.

## 4. First push

```sh
cd S:/DSMedia/Projects/LocoMP/repo
git add -A
git commit -s -m "feat: M0 walking-skeleton scaffold, CI pipeline, and Shim spike"
git push origin main
```

`git commit -s` is required (DCO — `pr.yml` enforces it on PRs). The push adds 45 files (plus a one-line
LICENSE update) on top of the existing initial LICENSE commit.

## 5. Branch protection & DCO (bootstrap 05 §7 item 1)

- Protect `main`: require the `Build & test (game-free)` and `DCO sign-off` checks; require PRs.
- **Caveat:** `release.yml` and `canary.yml` push to `main` (repository.json / initial buildid). Either allow
  the Actions bot to bypass protection, or switch those pushes to a PAT / an auto-merged PR.
- DCO: `pr.yml` already checks sign-off. The DCO GitHub App is optional belt-and-suspenders.

## 6. Prove the pipeline

- **`build.yml`** runs on push to `main` — proves DepotDownloader + TOTP and the full Shim build.
- **`canary.yml`** — trigger once manually (workflow_dispatch); it records the first DV buildid into
  `.ci/dv-buildid.txt` (M0 exit). Then set `.ci/depot.json` → `manifest` to the pinned B99.7 manifest id
  for reproducible builds.
- **`release.yml`** — dry-run by tagging `v0.0.2` (bootstrap 05 §7 item 4). Confirm the Nexus
  archive-old-version behavior, and verify `Nexus-Mods/upload-action` input names against its README.

## 7. Hardening residuals (not blocking)

- Pin GitHub Actions to commit SHAs (currently pinned to major tags).
- Pin the DepotDownloader tool version in `build.yml`/`release.yml`.
- Add a lockfile + `--locked` restore if you want fully reproducible NuGet restores.
