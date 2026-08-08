# LocoMP - performance baseline

**First recorded:** 2026-07-20 · **Rig:** headless Loopback/Core, no game ·
**Harness:** `tests/LocoMP.Core.Tests/BudgetBench.cs` (+ `CountingTransport.cs`) · **Toolchain:** dotnet
8.0.423, Release · **Machine:** the dev workstation (numbers below; timing is machine-dependent).

An early internal audit flagged that the project's performance budgets had **"no measurement harness
or recorded numbers."** This is that harness and those numbers. Re-run any time with:

```
dotnet test tests/LocoMP.Core.Tests -c Release --filter FullyQualifiedName~BudgetBench \
  -l "console;verbosity=detailed"
```

## What is (and isn't) measured

`CountingTransport` wraps the server side of the Loopback hub and tallies every byte the server sends,
bucketed by recipient - so **wire sizes are deterministic** (a pure function of the messages the server
chose to emit) and safe to assert against budgets. The world is seeded the real way (a world-source host
registers consists + world items over the wire; the career board auto-generates jobs), then a fresh
client joins and we weigh exactly what it receives.

- **Deterministic (asserted):** late-join snapshot bytes; per-message relay sizes.
- **Machine-dependent (recorded, loosely bounded):** host tick cost.
- **NOT measured here (needs the game):** the ≤1.5 ms/frame *client* main-thread cost - that's
  Unity-side Shim work (`RealCarSync` lerps, replica spawns), only measurable in-game with a profiler.
  Left for an in-game pass; flagged so "budget met" is never claimed for it from this doc.

## The budgets

| Budget | Target |
|---|---|
| Late-join snapshot | ≤ **10 MB** compressed, streamed with progress UI |
| Steady-state bandwidth | ≤ **128 kbps** down/client @ 32 players, relevance active |
| Host-mode tick overhead | ≤ **2 ms** / tick |
| Join time | ≤ **60 s** connect→playable |
| Client frame cost | ≤ 1.5 ms/frame @ 8 players & 200 cars *(not measured here - game-side)* |

## Results (protocol v9, 2026-07-20)

### 1. Late-join snapshot - the number that decides whether join compression is needed

Bytes the server sends to **one** joining client against a mature world:

| Scale | trains | cars | jobs | items | players | join bytes | msgs | KB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| small  |  8 |  40 |  20 |  30 |  5 |  4,002 |  69 |  3.9 |
| medium | 30 | 150 |  50 | 150 |  9 | 15,389 | 263 | 15.0 |
| large  | 60 | 360 | 100 | 400 | 17 | 38,228 | 623 | 37.3 |

**Largest measured: 37.3 KB - ~270× under the 10 MB budget.** The burst scales ~linearly with world
size; to approach 10 MB you'd need a world ~270× the "large" case (≈16,000 consists) - impossible for a
real DV session (the *entire* extracted map is 2,073 track edges). Join time over UDP for 37 KB is
sub-second, well inside the 60 s budget.

### 2. Per-message relay sizes (the bandwidth model's inputs)

| Message | Bytes to one recipient |
|---|---:|
| Player pose relay | 30 B |
| 5-car trainset snapshot | 107 B |

Tight - these are the hand-rolled hot-path packets, not MessagePack.

### 3. Steady-state DOWN bandwidth per client

Model: every other player emits a pose each tick and every moving consist emits a snapshot each tick,
**all relayed to every client at 30 Hz** - i.e. the *current* behaviour, with interest management
**not yet active** (`ServerTrains`/`ServerCareer` broadcast fan-outs).

| players | moving trains | kbps down/client | vs 128 kbps |
|---:|---:|---:|---|
|  8 |  30 |   820.8 | **6.4× over** |
| 16 |  60 |  1,648.8 | 12.9× over |
| 32 | 100 |  2,791.2 | 21.8× over |
| 32 | 200 |  5,359.2 | **41.9× over** |

### 3b. Interest management - MEASURED (burst 1: players, protocol v11)

The §3 numbers above are the *broadcast-everything* model. Burst 1 of interest management (players
only) is now in and measured, not modelled - `BudgetBench.Interest_management_cuts_a_distant_clients_
pose_bandwidth`. 2 equal clusters of players ~2 km apart, all streaming poses at 30 Hz; the bytes the
server actually sends to a probe in one cluster, filtering **off vs on**:

| filtering | bytes to the probe (steady interval) | vs broadcast-all |
|---|---:|---|
| OFF (broadcast-all) | 9,600 B | - |
| ON (spatial, 200 m enter / 300 m leave) | 4,800 B | **50%** |

The probe receives exactly its near cluster and none of the far one - the far half of the pose traffic
is gated out. This is deterministic (a pure function of who is in range), so it is asserted, not just
recorded. **Scope of Burst 1:** players only (~4% of the total steady-state bandwidth), so this proves
the *mechanism* end-to-end; the dominant channel - railed-train snapshots (~96%, §3) - is gated in
burst 2 (it needs coarse world geometry added to the extracted topology to place a spline-space train in
the world). Off by default; a host/dedicated server opts in via `InterestConfig`.

### 3c. Interest management on RAILED TRAINS - MEASURED (burst 2, protocol v11)

The payoff. §3 identifies railed-train snapshots as **~96%** of steady-state bandwidth, and burst 2 gates
them - `BudgetBench.Interest_management_cuts_a_distant_clients_train_bandwidth`. A probe at the origin
with 4 trains beside it while 20 more work a yard ~3 km away, every consist 5 cars, all streaming; the
bytes the server actually sends the probe, filtering **off vs on**:

| filtering | bytes to the probe (steady interval) | vs broadcast-all |
|---|---:|---|
| OFF (broadcast-all) | 51,360 B | - |
| ON (spatial, 500 m enter / 750 m leave) | 8,560 B | **17%** |

**83% of train bytes eliminated**, and 17% is exactly the 4-of-24 near share - the filter delivers the
in-range consists and nothing else, with no leakage. Deterministic, so it is asserted, not just recorded.

**What made trains possible (and why it needed a schema bump).** `BogieState` is spline-space (`EdgeId` +
metres along), player poses are world-space, and `TrackEdge` was a pure graph - so the server literally
could not tell how far a train was from a player. `TopologyCodec` **v2** adds coarse per-edge world
endpoints (absolute coordinates, i.e. DV's floating-origin shift already removed), and
`WorldTopology.TryEdgeWorldPoint` interpolates along the chord. A **v1 `.lmpw` still loads** and simply
reports no geometry, in which case the server suppresses train filtering and behaves exactly as before -
extracting a topology needs a running game, so refusing old files would cost more than it buys.

**Implication for §3's table (an implication, not a measurement):** a reduction of this class applied to
the 5,359 kbps worst case (32 players / 200 trains) puts a client with a normal-density neighbourhood
back near or under the 128 kbps budget - the 6-42× → <1× headline. The exact figure depends on how
clustered a real session is, which is why it is stated as an implication and left for a populated
session to measure.

### 4. Host tick cost

**~24.8 µs/tick** (`server.Poll` + relay, 8 players actively moving, over 2,000 ticks) - **~80× under**
the 2 ms budget on this machine. Comfortable even allowing an order of magnitude for the 32-player relay
fan-out.

## Verdicts & what they mean

1. **Join snapshot: comfortable → compression/chunking is correctly deferred.** At 37 KB worst case
   the ≤10 MB budget is a non-issue for any realistic world, and the 60 s join budget is met with room
   to spare. What compression *would* still buy: collapsing the **623 individual reliable sends** of a
   large join into one phased/streamed unit (nicer over lossy real UDP, and a hook for a staged loading
   screen). That's a polish/UX motive, **not** a size or time pressure. **Recommendation: keep it
   deferred; when a staged loading bar is wanted, do the *phasing* (cheap) and skip the *compression*.**
   A **join queue** (admission control) has small independent value but is friend-scale-irrelevant.

2. **Bandwidth: over budget by 6-42× at scale → interest management is the genuine next architecture
   priority.** Note the nuance: at **friend scale (8 players ≈ 0.8 Mbps down)** it's over the *mod's
   own* budget but tolerable on a modern home connection, so **small private sessions are not
   blocked**. But the **32-player ceiling is unviable** without relevance filtering, so this data says
   it should **lead** the scaling work and precede any 16+ tester session.
   **Status (2026-07-27): BOTH bursts are BUILT and measured. Burst 1 (players) halves a distant
   client's pose stream (§3b); burst 2 (railed trains - the ~96% channel) removes 83% of train bytes
   for a client whose neighbourhood holds 4 of 24 consists (§3c). Interest management is OFF by
   default and opt-in per server; the remaining work is a populated in-game session to measure a real
   clustering pattern, and regrouping world ITEMS into the same filter (deferred - items are discrete
   and contribute ~0 to steady state, so gating them is risk without bandwidth gain).**

3. **Host tick: no concern** (80× headroom). Revisit only if the 32-player relay loops or future
   per-tick snapshot assembly change the picture.

**Bottom line:** join-snapshot compression is measured as a non-issue; the real scaling gap was
interest management, which is now built and measured. Measuring first re-pointed the effort. The
client frame-cost budget remains unmeasured and wants an in-game profiler pass before any wider
alpha.

<!-- Numbers generated with AI assistance (Claude, Opus 4.8) per our AI-disclosure policy. -->
