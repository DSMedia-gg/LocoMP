# LocoMP dedicated server — container (M6-B Track B)

A container image for the **headless, game-free** `LocoMP.Server`. It runs the full session (presence +
the job board + persistence, optionally server-owned trains) over raw UDP — no Unity, no Derail Valley
install. This is the packaging half of the M6-B "container on SVHost" deliverable; **deploying** it is a
separate, deliberate step (never automatic — hard rule 7).

## Files

- `Dockerfile` — two-stage: publish `LocoMP.Server` (+ game-free `Core`/`Transport`) on the SDK image,
  run it on the .NET runtime image. The Shim and the machine-specific `Directory.Build.targets` are
  deliberately outside the build — a server image never touches the game install.
- `compose.yml` — one service: the UDP port, a `./data` volume for the world save, a graceful
  `stop_grace_period`, and `restart: unless-stopped`.
- `../.dockerignore` — keeps the build context small and game-free.

## Build + run (from the repo root)

```bash
docker compose -f docker/compose.yml up -d --build   # build the image + start detached
docker compose -f docker/compose.yml logs -f         # follow the [soak] health line
docker compose -f docker/compose.yml down            # SIGTERM → the server saves the world, then stops
```

Out of the box the server runs **bare** — presence + a generated job board + persistence — on UDP
**25701**, saving to `./data/locomp-server.save`, printing a `[soak]` health line every 5 minutes. A fresh
join from the game gets a populated board immediately.

### Add trains / a real career

The extracted world topology (`.lmpw`) is a **game artifact** (produced by the in-game mod panel, and
gitignored — it is not shipped in the repo), so it's operator-supplied, exactly like a real career
(`.lmpc`). Drop the files in `./data` and uncomment the `command:` in `compose.yml`:

```yaml
command: ["--port", "25701", "--save", "/data/locomp-server.save",
          "--config", "/data/career.lmpc", "--world", "/data/world.lmpw",
          "--spawn-trains", "3", "--soak-report", "300"]
```

`--spawn-trains N` makes the server drive its own consists along the mounted topology; `--config` loads a
real career instead of the built-in placeholder. Both are optional and independent.

## Deploying to SVHost (when you're ready — your call)

This is a **raw-UDP game server**, so — unlike the HTTP services on SVHost — it is **not** fronted by
Traefik or Cloudflare (neither proxies arbitrary game UDP). It needs a direct UDP port plus a router
port-forward, like the other SVHost game servers (Palworld `8211/udp`, ARK `7777/udp`, Minecraft).
Port **25701** sits in the Helm pool (25700–25899), so it's a natural fit for eventual Helm packaging.

Suggested shape (matching the SVHost service convention):
1. Copy `docker/` (or just `compose.yml`) to `~/locomp/` on SVHost; create `~/locomp/data/`.
2. `docker compose up -d --build`.
3. Forward UDP 25701 on the router to SVHost, and open it in the host firewall.
4. Join from Derail Valley: Direct-connect to `<your-ip>:25701` (LAN: SVHost's LAN IP). The handshake
   must match protocol/build/mod-version/mod-list-hash — pass `--modlist-hash <hash>` if DV sends one
   (the server logs the value it expects on a reject).

**Graceful shutdown:** `docker compose down` / `docker stop` sends SIGTERM, which the server handles by
saving the world before exiting (`stop_grace_period: 20s` gives the save time to flush). Autosave still
runs every `--autosave-seconds` (default 60) as a backstop.

## Notes / not-yet

- **No image published to a registry** — built locally from source. Pushing to GHCR is a later step if
  wanted.
- **No reverse proxy / TLS** — it's game UDP, not HTTP. Access control is the port-forward + (optionally)
  `--password`.
- **`docker build` not yet run here** — authored + `dotnet build`-verified (the server publishes clean,
  0 warnings); the image build itself needs a running Docker daemon. Build it on first deploy.
- **Verified game-free:** `DedicatedServerIntegrationTests` + `SoakTests` stand the same server up over
  real UDP / Loopback in CI; `SoakReporterTests` cover the health line. See `../src/LocoMP.Server/README.md`.
