# syntax=docker/dockerfile:1
#
# LocoMP dedicated server - headless, game-free. Builds ONLY LocoMP.Server and its game-free
# dependencies (Core + Transport). The Shim and the machine-specific Directory.Build.targets
# (Derail Valley assembly resolution) are deliberately absent from the build - a server image never
# touches the game install, the same discipline that keeps the PR pipeline off Steam.
#
# Build from the REPO ROOT (the context must include src/ + the build props + tests/data):
#   docker build -f docker/Dockerfile -t locomp-server:dev .
# or via compose:
#   docker compose -f docker/compose.yml up -d --build

# ---- build stage: publish the framework-dependent server ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Central build config only (the version source + central package versions). No Directory.Build.targets
# is copied, so LocoMpGameProject stays false (its default) and no game references are ever resolved.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/LocoMP.Core/ ./src/LocoMP.Core/
COPY src/LocoMP.Transport/ ./src/LocoMP.Transport/
COPY src/LocoMP.Server/ ./src/LocoMP.Server/

RUN dotnet publish src/LocoMP.Server/LocoMP.Server.csproj -c Release -o /app --nologo

# ---- runtime stage: just the .NET runtime + the published server ----
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /opt/locomp
COPY --from=build /app ./

# The world save - and any operator-supplied world/career - live on a volume so they survive rebuilds.
# No topology is baked in: the extracted .lmpw is a game artefact (gitignored, produced by the in-game
# mod panel), so for server-owned trains you mount a real one and add `--world /data/world.lmpw
# --spawn-trains N` (see docker/compose.yml). Out of the box the server runs bare - presence + the job
# board + persistence - which needs no topology.
VOLUME ["/data"]

# Raw game UDP. Remap in compose if you like.
EXPOSE 25701/udp

# ENTRYPOINT is the exe; CMD is the default arguments (override in compose). docker stop sends SIGTERM,
# which Program.cs handles → the world is saved on the way down (see stop_grace_period in compose).
ENTRYPOINT ["dotnet", "/opt/locomp/LocoMP.Server.dll"]
CMD ["--port", "25701", "--save", "/data/locomp-server.save", "--soak-report", "300"]
