# LocoMP — performance baseline (02 §9 budgets)

**First recorded:** 2026-07-20 · **Rig:** headless Loopback/Core, no game (CLAUDE.md hard rule 8) ·
**Harness:** `tests/LocoMP.Core.Tests/BudgetBench.cs` (+ `CountingTransport.cs`) · **Toolchain:** dotnet
8.0.423, Release · **Machine:** the dev workstation (numbers below; timing is machine-dependent).

The 2026-07-19 audit (§6) flagged that the §9 budgets had **"no measurement harness or recorded
numbers."** This is that harness and those numbers. Re-run any time with:

```
dotnet test tests/LocoMP.Core.Tests -c Release --filter FullyQualifiedName~BudgetBench \
  -l "console;verbosity=detailed"
```

## What is (and isn't) measured

`CountingTransport` wraps the server side of the Loopback hub and tallies every byte the server sends,
bucketed by recipient — so **wire sizes are deterministic** (a pure function of the messages the server
chose to emit) and safe to assert against budgets. The world is seeded the real way (a world-source host
registers consists + world items over the wire; the career board auto-generates jobs), then a fresh
client joins and we weigh exactly what it receives.

- **Deterministic (asserted):** late-join snapshot bytes; per-message relay sizes.
- **Machine-dependent (recorded, loosely bounded):** host tick cost.
- **NOT measured here (needs the game):** the ≤1.5 ms/frame *client* main-thread cost — that's
  Unity-side Shim work (`RealCarSync` lerps, replica spawns), only measurable in-game with a profiler.
  Left for an in-game pass; flagged so "budget met" is never claimed for it from this doc.

## The budgets (02 §9)

| Budget | Target |
|---|---|
| Late-join snapshot | ≤ **10 MB** compressed, streamed with progress UI |
| Steady-state bandwidth | ≤ **128 kbps** down/client @ 32 players, relevance active |
| Host-mode tick overhead | ≤ **2 ms** / tick |
| Join time | ≤ **60 s** connect→playable |
| Client frame cost | ≤ 1.5 ms/frame @ 8 players & 200 cars *(not measured here — game-side)* |

## Results (protocol v9, 2026-07-20)

### 1. Late-join snapshot — the number that decides M3.2

Bytes the server sends to **one** joining client against a mature world:

| Scale | trains | cars | jobs | items | players | join bytes | msgs | KB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| small  |  8 |  40 |  20 |  30 |  5 |  4,002 |  69 |  3.9 |
| medium | 30 | 150 |  50 | 150 |  9 | 15,389 | 263 | 15.0 |
| large  | 60 | 360 | 100 | 400 | 17 | 38,228 | 623 | 37.3 |

**Largest measured: 37.3 KB — ~270× under the 10 MB budget.** The burst scales ~linearly with world
size; to approach 10 MB you'd need a world ~270× the "large" case (≈16,000 consists) — impossible for a
real DV session (the *entire* extracted map is 2,073 track edges). Join time over UDP for 37 KB is
sub-second, well inside the 60 s budget.

### 2. Per-message relay sizes (the bandwidth model's inputs)

| Message | Bytes to one recipient |
|---|---:|
| Player pose relay | 30 B |
| 5-car trainset snapshot | 107 B |

Tight — these are the hand-rolled hot-path packets (hard rule 4), not MessagePack.

### 3. Steady-state DOWN bandwidth per client

Model: every other player emits a pose each tick and every moving consist emits a snapshot each tick,
**all relayed to every client at 30 Hz** — i.e. the *current* behaviour, with D10 interest management
**not yet active** (`ServerTrains`/`ServerCareer` broadcast fan-outs; audit §6).

| players | moving trains | kbps down/client | vs 128 kbps |
|---:|---:|---:|---|
|  8 |  30 |   820.8 | **6.4× over** |
| 16 |  60 |  1,648.8 | 12.9× over |
| 32 | 100 |  2,791.2 | 21.8× over |
| 32 | 200 |  5,359.2 | **41.9× over** |

### 3b. Interest management — MEASURED (D10 Burst 1, protocol v11)

The §3 numbers above are the *broadcast-everything* model. Burst 1 of interest management (players
only) is now in and measured, not modelled — `BudgetBench.Interest_management_cuts_a_distant_clients_
pose_bandwidth`. Two equal clusters of players ~2 km apart, all streaming poses at 30 Hz; the bytes the
server actually sends to a probe in one cluster, filtering **off vs on**:

| filtering | bytes to the probe (steady interval) | vs broadcast-all |
|---|---:|---|
| OFF (broadcast-all) | 9,600 B | — |
| ON (spatial, 200 m enter / 300 m leave) | 4,800 B | **50%** |

The probe receives exactly its near cluster and none of the far one — the far half of the pose traffic
is gated out. This is deterministic (a pure function of who is in range), so it is asserted, not just
recorded. **Scope of Burst 1:** players only (~4% of the total steady-state bandwidth), so this proves
the *mechanism* end-to-end; the dominant channel — railed-train snapshots (~96%, §3) — is gated in
Burst 2 (it needs coarse world geometry added to the extracted topology to place a spline-space train in
the world). Off by default; a host/dedicated server opts in via `InterestConfig`.

### 4. Host tick cost

**~24.8 µs/tick** (`server.Poll` + relay, 8 players actively moving, over 2,000 ticks) — **~80× under**
the 2 ms budget on this machine. Comfortable even allowing an order of magnitude for the 32-player relay
fan-out.

## Verdicts & what they mean for the roadmap

1. **Join snapshot: comfortable → M3.2 compression/chunking is correctly deferred.** At 37 KB worst-case
   the ≤10 MB budget is a non-issue for any realistic world, and the 60 s join budget is met with room to
   spare. What M3.2 *would* still buy: collapsing the **623 individual reliable sends** of a large join
   into one phased/streamed unit (nicer over lossy real UDP, and a hook for a staged loading screen —
   M5.1). That's a polish/UX motive, **not** a size or time pressure. **Recommendation: keep M3.2
   deferred; when M5.1 wants a staged loading bar, do the *phasing* (cheap) and skip the *compression*.**
   A **join queue** (admission control) has small independent value but is friend-scale-irrelevant.

2. **Bandwidth: over budget by 6–42× at scale → interest management (D10) is the genuine next
   architecture priority**, exactly as audit §6 warned. Note the nuance: at **friend scale (8 players ≈
   0.8 Mbps down)** it's over the *mod's own* budget but tolerable on a modern home connection, so **the
   M5 private alpha (P0, ≤8) is not blocked**. But the **32-player ceiling (D10) is unviable** without
   relevance filtering. This is already scoped as **M6 Track B** ("interest-management tuning toward
   16+"); this data says it should **lead** the scaling work and precede any 16+ tester session.
   **Status (2026-07-20): interest management Burst 1 (the mechanism, players only) is BUILT and
   measured (§3b) — a distant client's pose stream is halved in the two-cluster test. The dominant
   channel (railed-train snapshots, ~96%) is Burst 2, which needs coarse edge geometry in the topology;
   only then does the 6–42×→<1× headline win land.**

3. **Host tick: no concern** (80× headroom). Revisit only if the 32-player relay loops or future
   per-tick snapshot assembly change the picture.

**Bottom line:** the one flagged "M3 gap" (M3.2 join snapshot) is measured as a non-issue; the real
scaling gap is interest management, which is already on the M6 plan. Measuring first re-pointed the
effort. The client frame-cost budget (§9) remains unmeasured and wants an in-game profiler pass before
M5's alpha bar.

<!-- Numbers generated with AI assistance (Claude, Opus 4.8) per D11 / AI-disclosure policy. -->
